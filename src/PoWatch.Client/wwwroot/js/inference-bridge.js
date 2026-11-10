// Page side of the L2 caption model: owns the webcam stream and the preview element, captures frames
// on the main thread (the worker cannot touch the DOM) and hands them to inference-worker.js.
(() => {
  const call = window.powatchWorkerRpc('/js/inference-worker.js?v=20261109-loadstatus', 'caption');

  let activeStream = null;
  let activePreviewElement = null;
  let _lastFramePixels = null;
  let _lastCaptionModel = null;

  // Which camera to open, remembered per browser (powatchCamera in the block below). Empty means the
  // front camera, which is what a laptop has.
  const CAMERA_KEY = 'powatch:camera';
  function chosenCamera() {
    try { return localStorage.getItem(CAMERA_KEY) || ''; } catch { return ''; }
  }

  function videoConstraints() {
    const size = { width: { ideal: 1280 }, height: { ideal: 720 } };
    const id = chosenCamera();
    return id ? { ...size, deviceId: { exact: id } } : { ...size, facingMode: 'user' };
  }

  async function openCamera() {
    try {
      return await navigator.mediaDevices.getUserMedia({ audio: false, video: videoConstraints() });
    } catch (e) {
      // A remembered camera that is gone (unplugged, another dock) must not leave you with no camera.
      if (e?.name === 'OverconstrainedError' && chosenCamera()) {
        try { localStorage.removeItem(CAMERA_KEY); } catch { /* private mode */ }
        return await navigator.mediaDevices.getUserMedia({ audio: false, video: { width: { ideal: 1280 }, height: { ideal: 720 }, facingMode: 'user' } });
      }
      throw e;
    }
  }

  async function ensureWebcamAccess() {
    if (!navigator?.mediaDevices?.getUserMedia) {
      return 'This browser has no camera access. Try another browser, or use Demo scene.';
    }
    try {
      activeStream ??= await openCamera();
      return 'OK';
    } catch (e) {
      // Nothing falls back to a preview: say what went wrong and what to do about it.
      const why = {
        NotReadableError: 'The camera is in use by another app or tab. Close it (or restart Windows if nothing else is open) and press Start camera again.',
        NotAllowedError: 'Camera permission was denied. Allow it in the address bar or in Windows camera privacy settings.',
        NotFoundError: 'No camera was found on this device.',
        NotSupportedError: 'This browser cannot use a camera here.',
        OverconstrainedError: 'The camera could not be opened at a usable size.',
      }[e?.name] ?? 'The camera could not be started.';
      return `${why} The demo (“No camera? Try the demo”) works without one.`;
    }
  }

  async function attachStreamToElement(videoElement) {
    if (!videoElement || !activeStream) return;
    if (videoElement.srcObject !== activeStream) videoElement.srcObject = activeStream;
    videoElement.muted = true;
    videoElement.playsInline = true;
    activePreviewElement = videoElement;
    try {
      await videoElement.play();
    } catch {
      // Ignore autoplay timing issues; the stream remains attached.
    }
  }

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
      const status = await ensureWebcamAccess();
      if (status === 'OK') await attachStreamToElement(videoElement);
      return status;
    },

    /** `model` is Settings' caption model, an Ollama tag from caption-models.js; without Ollama the in-browser default answers. */
    async captureAndInfer(prompt, videoElement, maxInferenceTokens = 96, model = null) {
      const status = await ensureWebcamAccess();
      if (status !== 'OK') return unavailable(status);
      await attachStreamToElement(videoElement);

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
        // The worker died under this caption (GPU reset, crash): this one is lost, the next starts a fresh worker.
        return unavailable(`Model unavailable: ${err?.message ?? err}`);
      } finally {
        gpu.captioning--;
      }
    },

    async getInferenceDiagnostics() {
      // A status query must time out: a silent worker would otherwise hang the System page's render.
      return (await call({ type: 'GET_DIAGNOSTICS' }, [], 5000)).result;
    },

    stopMonitor() {
      if (activePreviewElement) {
        activePreviewElement.pause();
        activePreviewElement.srcObject = null;
        activePreviewElement = null;
      }
      for (const track of activeStream?.getTracks() ?? []) track.stop();
      activeStream = null;
    },
  };
})();

// Which camera starts a session: Settings lists the devices, the choice is remembered per browser, and
// the next Start uses it. A stream that is already open is not swapped mid-session (that would restart
// the whole sensing pipeline), so pick before pressing Start.
(() => {
  'use strict';
  const KEY = 'powatch:camera';

  /** "id<TAB>label" per line: the client splits text instead of handing a device list through JSON. */
  async function list() {
    try {
      if (!navigator?.mediaDevices?.enumerateDevices) return '';
      const all = (await navigator.mediaDevices.enumerateDevices()).filter((d) => d.kind === 'videoinput');
      return all.map((d, i) => `${d.deviceId}\t${d.label || `Camera ${i + 1}`}`).join('\n');
    } catch { return ''; }
  }

  function selected() {
    try { return localStorage.getItem(KEY) || ''; } catch { return ''; }
  }

  function select(id) {
    try { id ? localStorage.setItem(KEY, id) : localStorage.removeItem(KEY); } catch { /* private mode */ }
    return selected();
  }

  window.powatchCamera = { list, selected, select };
})();
