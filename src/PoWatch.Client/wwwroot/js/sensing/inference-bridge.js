// Page side of the L2 caption model: opens the webcam for a session (camera.js owns the stream and the
// preview element), captures frames on the main thread (the worker cannot touch the DOM) and hands
// them to inference-worker.js.
(() => {
  'use strict';

  const rpc = window.powatchWorkerRpc;
  const camera = window.powatchCamera;
  const call = rpc('sensing/inference-worker.js', 'caption');

  let _lastFramePixels = null;
  let _lastCaptionModel = null;

  let _diffCtx = null;

  // Fraction of pixels that changed significantly since the last caption (0–1), sampled at 120×68.
  function computeFrameDiff(videoElement) {
    if (!videoElement || videoElement.videoWidth === 0 || videoElement.videoHeight === 0) return 1;
    const w = 120;
    const h = 68;
    if (!_diffCtx) {
      const canvas = document.createElement('canvas');
      canvas.width = w;
      canvas.height = h;
      _diffCtx = canvas.getContext('2d', { willReadFrequently: true });
    }
    _diffCtx.drawImage(videoElement, 0, 0, w, h);
    const pixels = _diffCtx.getImageData(0, 0, w, h).data;
    const previous = _lastFramePixels;
    _lastFramePixels = new Uint8ClampedArray(pixels);
    if (!previous || previous.length !== pixels.length) return 1;

    let changed = 0;
    let sampled = 0;
    // Every 2nd pixel (stride of 8 in RGBA) keeps this under a millisecond.
    for (let i = 0; i < pixels.length; i += 8) {
      sampled++;
      const d = Math.abs(pixels[i] - previous[i]) + Math.abs(pixels[i + 1] - previous[i + 1]) + Math.abs(pixels[i + 2] - previous[i + 2]);
      if (d > 28) changed++;
    }
    return sampled > 0 ? changed / sampled : 0;
  }

  // Longest edge sent to the model: the processor downsamples anyway, so a full webcam frame only
  // buys latency. The frame travels as a transferable ImageBitmap — no JPEG, no base64.
  const _MAX_CAPTURE_EDGE = 512;

  function captureFrame(videoElement) {
    const srcW = videoElement.videoWidth;
    const srcH = videoElement.videoHeight;
    const scale = Math.min(1, _MAX_CAPTURE_EDGE / Math.max(srcW, srcH));
    return createImageBitmap(videoElement, {
      resizeWidth: Math.max(1, Math.round(srcW * scale)),
      resizeHeight: Math.max(1, Math.round(srcH * scale)),
      resizeQuality: 'medium',
    });
  }

  // Shared with detector-bridge.js: how many caption generations are in flight.
  const gpu = (window.powatchGpu ??= { captioning: 0 });

  const unavailable = (status) => ({ isAvailable: false, status, activity: 'Unavailable', caption: '' });

  window.powatchInference = {
    async startPreview(videoElement) {
      const status = await camera.open();
      if (status === 'OK') {
        call.keep();
        await camera.attach(videoElement);
      }
      return status;
    },

    /** `model` is Settings' caption model, an Ollama tag from caption-models.js; without Ollama the in-browser default answers. */
    async captureAndInfer(prompt, videoElement, maxInferenceTokens = 96, model = null) {
      const status = await camera.open();
      if (status !== 'OK') return unavailable(status);
      call.keep();
      await camera.attach(videoElement);

      if (!videoElement || videoElement.videoWidth === 0 || videoElement.videoHeight === 0) return unavailable('No frame captured');
      // A model switch must run even when the camera frame has not changed.
      const diff = computeFrameDiff(videoElement);
      if (model === _lastCaptionModel && diff < 0.015) return unavailable('Frame unchanged: skipped');

      const frame = await captureFrame(videoElement);
      // The detector yields the GPU while this runs (detector-bridge.js).
      gpu.captioning++;
      try {
        // After two GPU resets the in-browser model reloads on wasm instead (worker-rpc.js counts them).
        const res = await call({ type: 'RUN_INFERENCE', payload: { frame, prompt, maxNewTokens: maxInferenceTokens, model, avoidGpu: call.gpuLosses >= 2 } }, [frame]);
        _lastCaptionModel = model;
        // Searchable by meaning from the command palette (caption-index.js); the vector stays on this device.
        if (res.result?.isAvailable && res.result.embedding) window.powatchCaptionIndex?.put(Date.now(), res.result.activity, res.result.embedding);
        return res.result;
      } catch (err) {
        // Transferred frames are the worker's to close; one that never left (the worker could not start) is ours.
        frame.close();
        // The worker died under this caption (GPU reset, crash): this one is lost, the next starts a fresh worker.
        return unavailable(`Model unavailable: ${err?.message ?? err}`);
      } finally {
        gpu.captioning--;
      }
    },

    async getInferenceDiagnostics() {
      // A status query must time out: a silent worker would otherwise hang the System page's render.
      try {
        return (await call({ type: 'GET_DIAGNOSTICS' }, [], 5000)).result;
      } finally {
        // Asked with no camera session (the System page): the worker this started is let go again.
        if (!camera.isOpen()) call.releaseAfter(rpc.IDLE_MS);
      }
    },

    stopMonitor() {
      camera.close();
      // The caption model stays loaded for a quick restart, then its worker is let go (worker-rpc.js).
      call.releaseAfter(rpc.IDLE_MS);
    },
  };
})();
