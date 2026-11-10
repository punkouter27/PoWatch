// Shared set-up for the workers that run transformers.js (inference-worker.js, sensing/detector-worker.js).
//
// Pinned, self-hosted transformers.js supply chain: the library AND its ONNX Runtime wasm are vendored
// under wwwroot/lib/transformers-<version>/ and loaded from our own origin. To upgrade: vendor a new
// dist folder (transformers.min.js + ort-wasm-simd-threaded.jsep.{mjs,wasm}) and bump this line.
const TRANSFORMERS_VERSION = '3.8.1';
const TRANSFORMERS_BASE = new URL(`../lib/transformers-${TRANSFORMERS_VERSION}/`, import.meta.url);
const TRANSFORMERS_URL = new URL('transformers.min.js', TRANSFORMERS_BASE).href;

// Transient-network retry. On some networks connections to the HF hub are reset mid-request, and a
// single load issues many fetches (the largest ~190 MB) that transformers.js does not retry, so ONE
// reset failed the whole load. Wrapping the worker's global fetch is the only place the library's
// internal requests can be reached. Only idempotent methods are retried.
const FETCH_RETRIES = 3;
const FETCH_BACKOFF_MS = 600;
export const FETCH_ATTEMPTS = FETCH_RETRIES + 1;
const nativeFetch = self.fetch.bind(self);

async function fetchWithRetry(input, init) {
  const method = (init?.method ?? (typeof input === 'object' && input !== null ? input.method : null) ?? 'GET').toUpperCase();
  if (method !== 'GET' && method !== 'HEAD') return nativeFetch(input, init);

  let lastError = null;
  for (let attempt = 0; attempt <= FETCH_RETRIES; attempt++) {
    if (attempt > 0) {
      await new Promise((resolve) => setTimeout(resolve, FETCH_BACKOFF_MS * 2 ** (attempt - 1)));
    }
    try {
      const response = await nativeFetch(input, init);
      // 429 and 5xx are transient upstream states. A 4xx is a real answer and fails fast.
      if (response.status === 429 || response.status >= 500) {
        lastError = new Error(`HTTP ${response.status}`);
        continue;
      }
      return response;
    } catch (err) {
      // A reset, DNS failure or TLS failure all surface as TypeError("Failed to fetch").
      if (init?.signal?.aborted) throw err;
      lastError = err;
    }
  }
  throw lastError ?? new Error('Failed to fetch');
}

self.fetch = fetchWithRetry;

// A GPU reset (driver crash, sleep, out of memory) kills every WebGPU session in this worker. ONNX
// Runtime's buffer read-back then rejects where nothing awaits it, and the run that was waiting on it
// never settles: the caption queue or the detector stalls for good. Tell the page instead; worker-rpc.js
// fails what was pending and starts a fresh worker on the next call.
self.addEventListener('unhandledrejection', (event) => {
  const message = String(event.reason?.message ?? event.reason ?? '');
  if (!/mapAsync|GPUBuffer|Instance reference|device (is|was) lost/i.test(message)) return;
  event.preventDefault();
  self.postMessage({ type: 'GPU_LOST', message });
});

/** Imports the vendored library once and points its ONNX Runtime at our own wasm. */
export async function loadTransformers() {
  const transformers = await import(TRANSFORMERS_URL);
  const { env } = transformers;
  env.useFSCache = false;
  env.backends.onnx.wasm.wasmPaths = TRANSFORMERS_BASE.href;
  // Multi-threaded wasm needs SharedArrayBuffer, which needs cross-origin isolation.
  if (self.crossOriginIsolated) {
    env.backends.onnx.wasm.numThreads = Math.max(1, Math.min(4, navigator.hardwareConcurrency || 1));
  }
  return transformers;
}

/**
 * Turns a transferred ImageBitmap into the 3-channel RawImage the processors expect, drawing through
 * one OffscreenCanvas per worker that is reused while the frame size stays the same.
 */
let frameCanvas = null;
export function bitmapToRawImage(RawImage, bitmap) {
  if (!frameCanvas || frameCanvas.width !== bitmap.width || frameCanvas.height !== bitmap.height) {
    frameCanvas = new OffscreenCanvas(bitmap.width, bitmap.height);
  }
  const ctx = frameCanvas.getContext('2d', { willReadFrequently: true });
  ctx.drawImage(bitmap, 0, 0);
  const pixels = ctx.getImageData(0, 0, bitmap.width, bitmap.height);
  return { image: new RawImage(pixels.data, bitmap.width, bitmap.height, 4).rgb(), pixels };
}
