// Page side of the L1 detector: owns the detector worker, grabs frames from the <video> on a
// worker-driven clock (about 1 Hz), and hands normalised boxes to .NET as typed arrays through a
// [JSExport] (SensingSession.OnDetectorBoxes), not JSON. A frame is skipped while the previous one is
// still being detected, so a slow device lowers the rate instead of queueing.
//
// The detector and the caption model live in separate workers but share one GPU. A WebGPU detector
// therefore sits out the ticks while a caption is being generated (powatchGpu, set by
// inference-bridge.js): a skipped second costs the tracker nothing, while two models fighting for
// the GPU made both of them several times slower.
(function () {
  'use strict';

  const WORKER_URL = new URL('./detector-worker.js', document.currentScript?.src ?? location.href);
  const CLOCK_URL = new URL('./sample-clock.js', document.currentScript?.src ?? location.href);
  const CAPTURE_WIDTH = 640;

  const call = window.powatchWorkerRpc(WORKER_URL);
  let _clock = null;
  let _busy = false;
  let _device = null;
  let _yielded = 0;
  let _exports = null;

  /** The client assembly's [JSExport] methods (the .NET runtime's own API, as Blazor's docs use it). */
  function dotnetExports() {
    _exports ??= Promise.resolve(globalThis.getDotnetRuntime(0))
      .then((runtime) => runtime.getAssemblyExports('PoWatch.Client.dll'))
      .catch((err) => { _exports = null; throw err; });
    return _exports;
  }

  /** Labels, and per box score, x0, y0, x1, y1, the signature's length and the signature, in one Float64Array. */
  function pack(detections) {
    const labels = [];
    let size = 0;
    for (const d of detections) size += 6 + (d.signature?.length ?? 0);
    const values = new Float64Array(size);
    let at = 0;
    for (const d of detections) {
      const signature = d.signature ?? [];
      labels.push(d.label);
      values.set([d.score, d.x0, d.y0, d.x1, d.y1, signature.length], at);
      values.set(signature, at + 6);
      at += 6 + signature.length;
    }
    return { labels, values };
  }

  /** Loads the detector; resolves to { modelId, device, dtype, loadMs }. `device` forces 'webgpu' or 'wasm'. */
  async function load(device) {
    let info = await call({ type: 'LOAD', device });
    // ONNX Runtime aborted on one precision and took the worker with it: carry on in a fresh worker
    // from the next candidate (detector-worker.js). Each round skips at least one, so this ends.
    while (info?.type === 'POISONED') {
      call.restart();
      info = await call({ type: 'LOAD', device, from: info.next, rejected: info.rejected });
    }
    _device = info?.device ?? null;
    return info;
  }

  /** Detects on the current frame; resolves to { latencyMs, detections: [{ label, score, x0, y0, x1, y1 }] }. */
  async function detect(video, threshold) {
    if (!video || video.readyState < 2 || !video.videoWidth) return null;
    const height = Math.round(CAPTURE_WIDTH * video.videoHeight / video.videoWidth);
    const bitmap = await createImageBitmap(video, { resizeWidth: CAPTURE_WIDTH, resizeHeight: height });
    return call({ type: 'DETECT', bitmap, threshold }, [bitmap]);
  }

  async function start(video, dotnetRef, intervalMs, threshold) {
    stop();
    _clock = new Worker(CLOCK_URL, { type: 'module' });
    _clock.onmessage = async () => {
      if (_busy) return;
      if (_device === 'webgpu' && window.powatchGpu?.captioning) { _yielded++; return; }
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
  }

  /** How many detector ticks gave the GPU to the caption model. */
  const yielded = () => _yielded;

  window.powatchDetector = { load, detect, start, stop, yielded };
})();
