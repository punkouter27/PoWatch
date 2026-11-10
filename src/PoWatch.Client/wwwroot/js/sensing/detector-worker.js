// L1 detector worker: runs an object detector on frames posted from the page.
//
// Model: RF-DETR Nano (Apache-2.0, COCO classes), falling back to RT-DETRv2 R18 (Apache-2.0).
// Both architectures ship in the vendored transformers.js 3.8.1, loaded from our own origin like
// the VLM worker. WebGPU is tried first, then the WASM backend. Boxes come back normalised to
// [0, 1] so the page and the tracker never care about the capture resolution.
//
// Each backend gets the precision it is fast at — fp16 on WebGPU (half the weights and bandwidth),
// int8 on WASM (SIMD integer kernels) — with fp32 behind it: a GPU whose fp16 maths is broken fails
// the calibration frame and the loader steps down to fp32 before trying the next backend.
//
// Some GPUs (seen on Adreno) do worse than fail: ONNX Runtime aborts inside its WASM core and throws a
// bare number, and every later session in this worker fails the same way. Such an attempt is reported
// as POISONED with where to resume; detector-bridge.js then starts a fresh worker that skips it.

import { bitmapToRawImage, loadTransformers, modelStatus } from '../transformers-env.js';

const status = modelStatus();

const MODELS = [
  { id: 'onnx-community/rfdetr_nano-ONNX', dtypes: { webgpu: ['fp16', 'fp32'], wasm: ['q8', 'fp32'] } },
  { id: 'onnx-community/rtdetr_v2_r18vd-ONNX', dtypes: { webgpu: ['fp32'], wasm: ['fp32'] } },
];

let detector = null;
let RawImage = null;
let loaded = null;       // { modelId, device, dtype, loadMs }
let loading = null;

/** Thrown when ONNX Runtime aborted: this worker is unusable, a fresh one resumes at `next`. */
class Poisoned extends Error {
  constructor(next, rejected) {
    super('ONNX Runtime aborted; restart the worker');
    this.next = next;
    this.rejected = rejected;
  }
}

/** `from` and `prior` continue a load a poisoned worker gave up: the candidate to start at, and what was rejected. */
async function load(forceDevice, from = 0, prior = []) {
  if (loaded) return loaded;
  if (loading) return loading;

  loading = (async () => {
    const started = performance.now();
    const transformers = await loadTransformers();
    RawImage = transformers.RawImage;

    const devices = forceDevice ? [forceDevice]
      : typeof navigator !== 'undefined' && navigator.gpu ? ['webgpu', 'wasm'] : ['wasm'];
    const candidates = MODELS.flatMap((model) => devices.flatMap((device) => model.dtypes[device].map((dtype) => ({ model, device, dtype }))));
    const errors = [...prior];
    // A backend whose calibration run itself failed is not known to be broken, only unchecked: it is
    // held back while the others get their turn, and used only if none of them passes.
    let unchecked = null;
    const use = (candidate, model, device, dtype) => {
      detector = candidate;
      loaded = { modelId: model.id, device, dtype, loadMs: Math.round(performance.now() - started), rejected: errors };
      // On the CPU, say why: the page tells the user once (AiSelectors.razor), since it is several times slower.
      const reason = device === 'webgpu' ? null
        : forceDevice === 'wasm' ? 'the GPU reset twice in this session'
          : !(typeof navigator !== 'undefined' && navigator.gpu) ? 'this browser offers no WebGPU'
            : 'WebGPU did not pass its check on this machine';
      status.report({ phase: 'ready', model: model.id, device, dtype, loadMs: loaded.loadMs, reason, error: reason && errors.length ? errors.join(' | ') : null });
      return loaded;
    };
    for (let i = from; i < candidates.length; i++) {
      const { model, device, dtype } = candidates[i];
      const name = `${model.id}@${device}/${dtype}`;
      try {
        const candidate = await transformers.pipeline('object-detection', model.id, { device, dtype, progress_callback: status.progress(model.id, device, dtype) });
        // The calibration frame is the first run, which is when WebGPU compiles its shaders.
        status.report({ phase: 'warming', model: model.id, device, dtype });
        const verdict = await calibrate(candidate);
        if (verdict === 'sane') {
          if (unchecked) await unchecked.candidate.dispose?.();
          return use(candidate, model, device, dtype);
        }
        if (verdict === 'unchecked' && !unchecked) {
          errors.push(`${name}: calibration run failed; kept as a fallback`);
          unchecked = { candidate, model, device, dtype };
          continue;
        }
        errors.push(`${name}: ${verdict === 'insane' ? 'calibration frame produced out-of-frame boxes' : 'calibration run failed'}`);
        await candidate.dispose?.();
      } catch (err) {
        if (typeof err === 'number') throw new Poisoned(i + 1, [...errors, `${name}: ONNX Runtime aborted (${err})`]);
        errors.push(`${name}: ${err?.message ?? err}`);
      }
    }
    if (unchecked) return use(unchecked.candidate, unchecked.model, unchecked.device, unchecked.dtype);
    const failure = `No detector could be loaded. ${errors.join(' | ')}`;
    status.report({ phase: 'failed', model: MODELS[0].id, error: failure });
    throw new Error(failure);
  })();

  try {
    return await loading;
  } finally {
    loading = null;
  }
}

// Some WebGPU stacks (seen on Chromium's software GPU) run the model but return garbage: boxes many
// frame-widths wide with near-random labels. A plain grey frame must never yield a box outside the
// frame, so one calibration pass catches that and the loader moves on to the next backend.
// 'sane' passes; 'insane' is rejected; 'unchecked' means the calibration run itself failed.
async function calibrate(candidate) {
  let output;
  try {
    const width = 320, height = 240;
    const grey = new RawImage(new Uint8ClampedArray(width * height * 3).fill(128), width, height, 3);
    output = await candidate(grey, { threshold: 0.05, percentage: true });
  } catch (err) {
    console.warn('Detector calibration run failed:', err);
    return 'unchecked';
  }
  if (!Array.isArray(output)) return 'unchecked';
  const inside = (v) => v >= -0.1 && v <= 1.1;
  return output.every((d) => inside(d.box.xmin) && inside(d.box.ymin) && inside(d.box.xmax) && inside(d.box.ymax)) ? 'sane' : 'insane';
}

// Appearance signature for recognising regulars: a 64-bin colour histogram (4 levels per RGB
// channel) of the centre 60% of the box, where the subject is rather than the background.
// Colours only — no faces, landmarks or anything biometric.
function signature(pixels, box) {
  const { width, height, data } = pixels;
  const clamp = (v, max) => Math.max(0, Math.min(max - 1, Math.round(v)));
  const bw = (box.xmax - box.xmin) * width, bh = (box.ymax - box.ymin) * height;
  const x0 = clamp(box.xmin * width + bw * 0.2, width), x1 = clamp(box.xmax * width - bw * 0.2, width);
  const y0 = clamp(box.ymin * height + bh * 0.2, height), y1 = clamp(box.ymax * height - bh * 0.2, height);
  const bins = new Array(64).fill(0);
  let n = 0;
  for (let y = y0; y <= y1; y += 2) {
    for (let x = x0; x <= x1; x += 2) {
      const i = (y * width + x) * 4;
      bins[((data[i] >> 6) << 4) | ((data[i + 1] >> 6) << 2) | (data[i + 2] >> 6)]++;
      n++;
    }
  }
  return n === 0 ? [] : bins.map((c) => Math.round((c / n) * 10000) / 10000);
}

async function detect(bitmap, threshold, forceDevice) {
  let image, pixels;
  try {
    await load(forceDevice);
    ({ image, pixels } = bitmapToRawImage(RawImage, bitmap));
  } finally {
    bitmap.close(); // also when the model failed to load, or the transferred frame leaks
  }

  const started = performance.now();
  const output = await detector(image, { threshold, percentage: true });
  return {
    latencyMs: Math.round(performance.now() - started),
    device: loaded.device,
    detections: output.map((d) => ({
      label: d.label,
      score: d.score,
      x0: d.box.xmin, y0: d.box.ymin, x1: d.box.xmax, y1: d.box.ymax,
      signature: signature(pixels, d.box),
    })),
  };
}

self.onmessage = async (event) => {
  const { id, type } = event.data ?? {};
  try {
    if (type === 'LOAD') {
      self.postMessage({ id, type: 'LOADED', ...(await load(event.data.device, event.data.from, event.data.rejected)) });
    } else if (type === 'DETECT') {
      self.postMessage({ id, type: 'DETECTED', ...(await detect(event.data.bitmap, event.data.threshold ?? 0.5, event.data.device)) });
    }
  } catch (err) {
    if (err instanceof Poisoned) self.postMessage({ id, type: 'POISONED', next: err.next, rejected: err.rejected });
    else self.postMessage({ id, type: 'ERROR', message: String(err?.message ?? err) });
  }
};
