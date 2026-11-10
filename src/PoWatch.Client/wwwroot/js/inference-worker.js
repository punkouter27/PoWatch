// inference-worker.js
// Runs AI model loading and token generation in a dedicated Web Worker,
// keeping the browser's main thread free so the UI stays responsive.
// The main thread (inference-bridge.js) handles all DOM access (video/canvas).

// The vendored transformers.js, its fetch retry and the frame hand-off live in transformers-env.js;
// the models in caption-models.js; what counts as a usable caption in caption-quality.js.
import { FETCH_ATTEMPTS, bitmapToRawImage, loadTransformers } from './transformers-env.js';
import { CAPTION_MODELS, DEFAULT_CAPTION_MODEL, captionSpec, decodeGenerated, dtypeName, generateOptions, isOllamaModel, loadModel, loadProcessor, prepareInputs } from './caption-models.js';
import { judgeCaption } from './caption-quality.js';
import { tryEmbed } from './embedder.js';

// Settings' models run on this machine's own Ollama server. Ollama allows http://localhost origins out
// of the box; any other origin needs OLLAMA_ORIGINS, or captions fall back to the in-browser model.
const OLLAMA_CHAT_URL = 'http://localhost:11434/api/chat';
// The first call loads the model into VRAM, which took up to ~30 s on a laptop GPU.
const OLLAMA_TIMEOUT_MS = 120_000;
let _ollamaModel = null; // the Ollama model that answered last; null while the in-browser model is in use
let _ollamaMissing = null; // the chosen Ollama model when Ollama answered without it (404): the browser model stands in
// Nothing answered (no server, or the browser refused the call): ask again after this long instead of
// on every caption, which cost a JPEG encode and a console error each time.
const OLLAMA_RETRY_MS = 60_000;
let _ollamaRetryAt = 0;

// Model state
let _modelKey = null;
let _spec = null;
let _transformers = null;
let _processor = null;
let _model = null;
let _loadState = 'idle'; // 'idle' | 'loading' | 'ready' | 'error'
let _loadError = null;
let _loadPromise = null;

// Inference diagnostics
let _device = null;
let _dtype = null;
let _fp16FallbackUsed = false;
// The precisions not yet tried on the current backend, best first, then wasm. A backend that loads
// fine and then emits NaN logits never throws, so load-time fallbacks alone let empty output survive
// every precision. Each step is taken once, so a mute model stops reloading.
let _runtimeFallbacks = [];
let _runtimeFallbackStage = 0;
let _loadDurationMs = null;
let _inferenceCount = 0;
let _lastInferenceMs = null;
let _lastInferenceTimestamp = null;
let _lastInferenceOutput = null;
let _inferLock = Promise.resolve();

async function hasWebGpu() {
  try {
    return !!navigator.gpu && (await navigator.gpu.requestAdapter()) !== null;
  } catch {
    return false;
  }
}

async function ensureModelLoaded(modelKey, avoidGpu = false) {
  const key = CAPTION_MODELS[modelKey] ? modelKey : DEFAULT_CAPTION_MODEL;
  if (_loadState === 'loading') await _loadPromise.catch(() => { });
  if (_loadState === 'ready' && key === _modelKey) return;

  // A different model was chosen in Settings: let the old one go before loading the next.
  if (_model && key !== _modelKey) {
    await _model.dispose?.();
    _model = null;
  }

  _modelKey = key;
  _spec = captionSpec(key);
  _loadState = 'loading';
  _loadError = null;
  _runtimeFallbackStage = 0;
  _fp16FallbackUsed = false;
  const started = performance.now();

  _loadPromise = (async () => {
    _transformers ??= await loadTransformers();
    _processor = await loadProcessor(_transformers, _spec);

    const plan = (!avoidGpu && await hasWebGpu())
      ? [..._spec.webgpu.map((dtype) => ({ device: 'webgpu', dtype })), ..._spec.wasm.map((dtype) => ({ device: 'wasm', dtype }))]
      : _spec.wasm.map((dtype) => ({ device: 'wasm', dtype }));
    let lastError = null;
    while (plan.length > 0) {
      const step = plan.shift();
      try {
        await loadOn(step.device, step.dtype);
        _fp16FallbackUsed = step.device === 'webgpu' && step.dtype !== _spec.webgpu[0];
        // A wasm session created as a fallback from a failed WebGPU run can still be unusable, so
        // after a WebGPU load the last wasm step is kept twice: reloaded once with fresh tensors.
        _runtimeFallbacks = step.device === 'webgpu' && plan.length > 0 ? [...plan, plan[plan.length - 1]] : plan;
        lastError = null;
        break;
      } catch (err) {
        lastError = err;
      }
    }
    if (lastError) throw lastError;

    _loadDurationMs = Math.round(performance.now() - started);
    _loadState = 'ready';
  })();

  try {
    await _loadPromise;
  } catch (err) {
    _loadState = 'error';
    _loadError = err?.message ?? 'Model failed to load';
    throw err;
  }
}

async function loadOn(device, dtype) {
  _model = await loadModel(_transformers, _spec, device, dtype);
  _device = device;
  _dtype = dtypeName(dtype);
}

function describeError(err) {
  if (err == null) return 'unknown';
  if (typeof err === 'string' && err.trim()) return err.trim();
  if (typeof err === 'number' || typeof err === 'bigint') return String(err);
  const message = typeof err.message === 'string' ? err.message.trim() : '';
  if (message) return message;
  const asString = String(err);
  return asString && asString !== '[object Object]' ? asString : 'unknown';
}

function unavailable(status, rawOutput = '') {
  return { isAvailable: false, status, rawOutput, activity: 'Unavailable', caption: '' };
}

const QUALITY_STATUS = {
  'low-quality': 'Low-quality inference: skipped',
  echo: 'Low-quality inference: prompt echoed instead of describing the scene',
  incomplete: 'Low-quality inference: incomplete sentence',
};

// Ollama takes images as base64 JPEG.
async function toJpegBase64(bitmap) {
  const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
  canvas.getContext('2d').drawImage(bitmap, 0, 0);
  const bytes = new Uint8Array(await (await canvas.convertToBlob({ type: 'image/jpeg', quality: 0.85 })).arrayBuffer());
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(binary);
}

// The reply from the local Ollama server; null when nothing listens there and { missing } when it has no
// such model (the caller then uses the in-browser model either way). Any other error throws.
async function ollamaGenerate(model, bitmap, prompt, maxNewTokens) {
  if (performance.now() < _ollamaRetryAt) return null;
  const image = await toJpegBase64(bitmap);
  let response;
  try {
    response = await fetch(OLLAMA_CHAT_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      signal: AbortSignal.timeout(OLLAMA_TIMEOUT_MS),
      body: JSON.stringify({
        model,
        stream: false,
        keep_alive: '30m',
        messages: [{ role: 'user', content: prompt, images: [image] }],
        options: { temperature: 0, num_predict: maxNewTokens },
      }),
    });
  } catch (err) {
    if (err?.name === 'TimeoutError') throw new Error(`${model} took longer than ${OLLAMA_TIMEOUT_MS / 1000} s to answer`);
    _ollamaRetryAt = performance.now() + OLLAMA_RETRY_MS;
    return null;
  }
  if (!response.ok) {
    const detail = (await response.json().catch(() => null))?.error ?? `HTTP ${response.status}`;
    if (response.status === 404) return { missing: model };
    throw new Error(`Ollama: ${detail}`);
  }
  return ((await response.json()).message?.content ?? '').trim();
}

async function runInference(bitmap, prompt, maxNewTokens = 32, modelKey = null, avoidGpu = false) {
  if (!bitmap) return unavailable('No frame captured');

  const safeMaxNewTokens = Number.isFinite(maxNewTokens)
    ? Math.min(96, Math.max(16, Math.trunc(maxNewTokens)))
    : 32;

  if (isOllamaModel(modelKey)) {
    const started = performance.now();
    let output;
    try {
      output = await ollamaGenerate(modelKey, bitmap, prompt, safeMaxNewTokens);
    } catch (err) {
      bitmap.close?.();
      return unavailable(`Model unavailable: ${describeError(err)}`);
    }
    if (typeof output === 'string') {
      bitmap.close?.();
      _ollamaModel = modelKey;
      _ollamaMissing = null;
      return finish(output, prompt, started, null);
    }
    // Ollama answered but has no such model: caption in the browser instead, and say why (GET_DIAGNOSTICS).
    _ollamaMissing = output?.missing ?? null;
  } else {
    _ollamaMissing = null;
  }
  _ollamaModel = null;

  try {
    await ensureModelLoaded(modelKey, avoidGpu);
  } catch (err) {
    bitmap.close?.();
    // "Failed to fetch" reads as an app bug rather than what it is — the weights could not be downloaded.
    const detail = describeError(err);
    return unavailable(/failed to fetch|networkerror|network error|load failed/i.test(detail)
      ? `Model unavailable: could not download model weights after ${FETCH_ATTEMPTS} attempts — check the network connection to the model host`
      : `Model unavailable: ${detail}`);
  }

  const inferStart = performance.now();

  // Frames arrive as a transferred ImageBitmap: no JPEG encode on the page, no base64 copy, no decode here.
  const { image } = bitmapToRawImage(_transformers.RawImage, bitmap);
  bitmap.close?.();
  // Rebuild tensors after every backend switch: reusing WebGPU inputs on wasm is how generate()
  // started throwing a bare ONNX code with no .message.
  let inputs = await prepareInputs(_processor, _spec, image, prompt);
  let generateError = null;
  let output = '';

  for (let attempt = 0; attempt < 4; attempt++) {
    generateError = null;
    let generatedIds = null;
    try {
      generatedIds = await _model.generate({ ...inputs, ...generateOptions(safeMaxNewTokens) });
    } catch (err) {
      generateError = describeError(err);
    }

    if (generatedIds) {
      output = decodeGenerated(_processor, _spec, generatedIds, inputs, image);
      if (output.length > 0) break;
    }

    const fallback = _runtimeFallbacks.shift();
    if (!fallback) break;

    _runtimeFallbackStage += 1;
    try {
      await loadOn(fallback.device, fallback.dtype);
      _fp16FallbackUsed = fallback.device === 'webgpu';
      inputs = await prepareInputs(_processor, _spec, image, prompt);
    } catch (err) {
      generateError = `Fallback to ${fallback.device}/${dtypeName(fallback.dtype)} failed: ${describeError(err)}`;
      break;
    }
  }

  return finish(output, prompt, inferStart, generateError
    ? `Inference error: ${generateError}`
    : (_runtimeFallbackStage > 0 ? `Model returned an empty response (retried on ${_device}/${_dtype})` : null));
}

// Records the run and judges the reply; `emptyStatus` says why when nothing came back.
async function finish(output, prompt, started, emptyStatus) {
  _lastInferenceMs = Math.round(performance.now() - started);
  _lastInferenceTimestamp = new Date().toISOString();
  _inferenceCount++;
  _lastInferenceOutput = output;

  // Distinguish "the model said nothing" from "the model said something we rejected".
  const verdict = judgeCaption(output, prompt);
  if (verdict.reason === 'empty') return unavailable(emptyStatus ?? 'Model returned an empty response');
  if (!verdict.ok) return unavailable(QUALITY_STATUS[verdict.reason], output);

  // The sentence embedding rides along for novelty and search; without the embedder it is null.
  const embedding = (await tryEmbed([verdict.activity]))?.[0] ?? null;
  return { isAvailable: true, status: 'OK', activity: verdict.activity, caption: verdict.caption, embedding };
}

// Each request carries a unique `id` so the bridge can match responses to the awaiting Promise.
self.onmessage = async (e) => {
  const { id, type, payload } = e.data;

  switch (type) {
    case 'RUN_INFERENCE': {
      const run = _inferLock.then(() => runInference(payload.frame, payload.prompt, payload.maxNewTokens, payload.model, payload.avoidGpu));
      _inferLock = run.then(() => undefined, () => undefined);
      let result;
      try {
        result = await run;
      } catch (err) {
        result = unavailable(`Inference error: ${describeError(err)}`);
      }
      self.postMessage({ id, result });
      break;
    }

    case 'GET_DIAGNOSTICS': {
      self.postMessage({
        id,
        result: {
          ...(_ollamaModel
            ? { modelId: _ollamaModel, modelKey: _ollamaModel, loadState: 'ready', loadError: null, device: 'ollama', dtype: 'local GPU', fp16FallbackUsed: false, loadDurationMs: null }
            : {
              modelId: (_spec ?? captionSpec(DEFAULT_CAPTION_MODEL)).id,
              modelKey: _modelKey,
              loadState: _loadState,
              loadError: _loadError,
              device: _device,
              dtype: _dtype,
              fp16FallbackUsed: _fp16FallbackUsed,
              loadDurationMs: _loadDurationMs,
            }),
          ollamaMissing: _ollamaMissing,
          inferenceCount: _inferenceCount,
          lastInferenceMs: _lastInferenceMs,
          lastInferenceTimestamp: _lastInferenceTimestamp,
          lastInferenceOutput: _lastInferenceOutput,
          webGpuPresent: !!navigator.gpu,
        },
      });
      break;
    }
  }
};
