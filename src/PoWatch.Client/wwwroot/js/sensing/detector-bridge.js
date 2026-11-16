// Page side of the L1 detector: owns the detector worker, grabs frames from the <video> on a
// worker-driven clock (about 1 Hz), and hands normalised boxes to .NET as typed arrays through a
// [JSExport] (SensingSession.OnDetectorBoxes), not JSON. A frame is skipped while the previous one is
// still being detected, so a slow device lowers the rate instead of queueing.
//
// The detector and the caption model live in separate workers but share one GPU, so every load and
// every WebGPU detection takes a turn on it (powatchWorkerRpc.gpu). A tick that finds the GPU taken is
// skipped rather than queued: a skipped second costs the tracker nothing, while two models on the GPU
// at once made both several times slower and could make the browser reset the GPU.
(function () {
  'use strict';

  const rpc = window.powatchWorkerRpc;
  const CLOCK_URL = rpc.url('sensing/sample-clock.js');
  const CAPTURE_WIDTH = 640;

  const call = rpc('sensing/detector-worker.js', 'detector');
  let _clock = null;
  let _busy = false;
  let _device = null;
  let _exports = null;
  let _pose = false;
  let _passes = 0;

  // Posture changes slowly and the pose model costs CPU per person, so it looks on every third pass;
  // the tracker keeps what it last read in between (CentroidTracker.Settle).
  const POSE_EVERY = 3;

  /** The client assembly's [JSExport] methods (the .NET runtime's own API, as Blazor's docs use it). */
  function dotnetExports() {
    _exports ??= Promise.resolve(globalThis.getDotnetRuntime(0))
      .then((runtime) => runtime.getAssemblyExports('PoWatch.Client.dll'))
      .catch((err) => { _exports = null; throw err; });
    return _exports;
  }

  /**
   * Labels, and per box score, x0, y0, x1, y1, the signature's length, the signature, the keypoints'
   * length (0, or 51 for a person whose pose was read) and the keypoints, in one Float64Array.
   */
  function pack(detections) {
    const labels = [];
    let size = 0;
    for (const d of detections) size += 7 + (d.signature?.length ?? 0) + (d.keypoints?.length ?? 0);
    const values = new Float64Array(size);
    let at = 0;
    for (const d of detections) {
      const signature = d.signature ?? [];
      const keypoints = d.keypoints ?? [];
      labels.push(d.label);
      values.set([d.score, d.x0, d.y0, d.x1, d.y1, signature.length], at);
      values.set(signature, at + 6);
      at += 6 + signature.length;
      values[at] = keypoints.length;
      values.set(keypoints, at + 1);
      at += 1 + keypoints.length;
    }
    return { labels, values };
  }

  /** Loads the detector; resolves to { modelId, device, dtype, loadMs }. `device` forces 'webgpu' or 'wasm'. */
  async function load(device) {
    call.keep();
    const loadOnce = async () => {
      // After two GPU resets the GPU is not asked again (worker-rpc.js counts them).
      const want = device ?? (call.gpuLosses >= 2 ? 'wasm' : undefined);
      let info = await call({ type: 'LOAD', device: want });
      // ONNX Runtime aborted on one precision and took the worker with it: carry on in a fresh worker
      // from the next candidate (detector-worker.js). Each round skips at least one, so this ends.
      while (info?.type === 'POISONED') {
        call.restart();
        info = await call({ type: 'LOAD', device: want, from: info.next, rejected: info.rejected });
      }
      return info;
    };
    let info;
    try {
      info = await rpc.gpu.run(loadOnce);
    } catch (err) {
      // A GPU reset during the load drops the worker (worker-rpc.js); a fresh one gets one more try.
      if (call.gpuLosses === 0) throw err;
      info = await rpc.gpu.run(loadOnce);
    }
    _device = info?.device ?? null;
    return info;
  }

  /** Detects on the current frame; resolves to { latencyMs, detections: [{ label, score, x0, y0, x1, y1 }] }. */
  async function detect(video, threshold) {
    if (!video || video.readyState < 2 || !video.videoWidth) return null;
    const height = Math.round(CAPTURE_WIDTH * video.videoHeight / video.videoWidth);
    const bitmap = await createImageBitmap(video, { resizeWidth: CAPTURE_WIDTH, resizeHeight: height });
    // A fresh worker (after a GPU reset) loads the model on its first frame: on wasm once the GPU has failed twice.
    const pose = _pose && _passes++ % POSE_EVERY === 0;
    let result;
    try {
      const run = () => call({ type: 'DETECT', bitmap, threshold, pose, device: call.gpuLosses >= 2 ? 'wasm' : undefined }, [bitmap]);
      // On the CPU it needs no turn; anything else (WebGPU, or a fresh worker about to load) does.
      result = await (_device === 'wasm' ? run() : rpc.gpu.run(run));
    } catch (err) {
      // A transferred frame is the worker's to close; one that never left (the worker could not start) is ours.
      bitmap.close();
      throw err;
    }
    _device = result.device ?? _device;
    return result;
  }

  /** `pose` also reads people's body keypoints (posture); setPose changes it while running. */
  async function start(video, dotnetRef, intervalMs, threshold, pose) {
    stop();
    call.keep();
    _pose = pose === true;
    _clock = new Worker(CLOCK_URL, { type: 'module' });
    _clock.onmessage = async () => {
      if (_busy) return;
      if (_device !== 'wasm' && rpc.gpu.busy) return;
      _busy = true;
      try {
        const result = await detect(video, threshold ?? 0.5);
        if (result) {
          const exports = await dotnetExports();
          const { labels, values } = pack(result.detections);
          exports.PoWatch.Client.Services.SensingSession.OnDetectorBoxes(new Date(), result.latencyMs, labels, values);
        }
      } catch (err) {
        dotnetRef.invokeMethodAsync('OnDetectorError', String(err?.message ?? err)).catch(() => { });
      } finally {
        _busy = false;
      }
    };
    _clock.postMessage({ intervalMs: intervalMs || 1000 });
  }

  function stop() {
    if (_clock) { _clock.terminate(); _clock = null; }
    _busy = false;
    // The detector stays loaded for a quick restart, then its worker is let go (worker-rpc.js).
    call.releaseAfter(rpc.IDLE_MS);
  }

  function setPose(on) { _pose = on === true; }

  window.powatchDetector = { load, start, stop, setPose };
})();
