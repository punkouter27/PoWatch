// Reads one face crop (224 x 224, cut by face-overlay.js around the landmarks) with small
// transformers.js models, all on the CPU so they never wait for or crowd the GPU the detector and
// captions share:
//   age           jdp8/vit-age-classifier                 probabilities over 9 bands
//   presentation  onnx-community/gender-classification-ONNX probabilities over female, male
//   descriptor    Xenova/dinov2-small                     384 numbers for the session's "Person N" labels
//   emotion       Xenova/facial_emotions_image_detection  probabilities over 7 emotions (EMOTIONS order)
// The emotion model (~87 MB) loads on its own once the others are ready, so a slow or failed download
// never holds up age, presentation or labels; until it is ready a reading's emotion is empty.
// The answers go back to the page and on to SensingSession; nothing here keeps a crop or a reading.
// Speaks worker-rpc.js: replies echo the `id`, and a failure is a type 'ERROR' reply.
import { bitmapToRawImage, loadTransformers, modelStatus } from '../transformers-env.js';

const MODELS = {
  age: 'jdp8/vit-age-classifier',
  presentation: 'onnx-community/gender-classification-ONNX',
  descriptor: 'Xenova/dinov2-small',
  emotion: 'Xenova/facial_emotions_image_detection',
};
// The order FaceReadings.Emotions expects; the model's own label order is mapped onto it.
const EMOTIONS = ['happy', 'neutral', 'surprise', 'sad', 'angry', 'fear', 'disgust'];
const EMOTION_RETRY_MS = 60000;
const DEVICE = 'wasm';
const DTYPE = 'q8';

const status = modelStatus();
let _loading = null;
let _readyStatus = null;
let _emotion = null;
let _emotionLoading = null;
let _emotionRetryAt = 0;

function softmax(logits) {
  const values = Array.from(logits);
  const max = Math.max(...values);
  const exps = values.map((v) => Math.exp(v - max));
  const sum = exps.reduce((a, b) => a + b, 0);
  return exps.map((v) => v / sum);
}

function load() {
  _loading ??= (async () => {
    const started = performance.now();
    const t = await loadTransformers();
    const options = { device: DEVICE, dtype: DTYPE, progress_callback: status.progress('face models', DEVICE, DTYPE) };
    const [ageProcessor, age, presentationProcessor, presentation, descriptorProcessor, descriptor] = await Promise.all([
      t.AutoProcessor.from_pretrained(MODELS.age),
      t.AutoModelForImageClassification.from_pretrained(MODELS.age, options),
      t.AutoProcessor.from_pretrained(MODELS.presentation),
      t.AutoModelForImageClassification.from_pretrained(MODELS.presentation, options),
      t.AutoProcessor.from_pretrained(MODELS.descriptor),
      t.AutoModel.from_pretrained(MODELS.descriptor, options),
    ]);
    _readyStatus = { phase: 'ready', model: 'face models', device: DEVICE, dtype: DTYPE, loadMs: Math.round(performance.now() - started) };
    status.report(_readyStatus);
    return { RawImage: t.RawImage, ageProcessor, age, presentationProcessor, presentation, descriptorProcessor, descriptor };
  })().catch((err) => {
    // A failed download is not remembered: the next reading tries again.
    _loading = null;
    status.report({ phase: 'failed', model: 'face models', device: DEVICE, dtype: DTYPE, error: String(err?.message ?? err) });
    throw err;
  });
  return _loading;
}

/**
 * Starts the emotion model's load once, in the background. It reports its own download on this worker's
 * status line; a failure puts the face models' ready status back (emotion is optional) and is retried later.
 */
function loadEmotion(t) {
  if (_emotion || _emotionLoading || performance.now() < _emotionRetryAt) return;
  _emotionLoading = (async () => {
    const started = performance.now();
    const options = { device: DEVICE, dtype: DTYPE, progress_callback: status.progress('emotion model', DEVICE, DTYPE) };
    const [processor, model] = await Promise.all([
      t.AutoProcessor.from_pretrained(MODELS.emotion),
      t.AutoModelForImageClassification.from_pretrained(MODELS.emotion, options),
    ]);
    const labels = model.config?.id2label ?? {};
    // order[i] is the model's output index for EMOTIONS[i].
    const order = EMOTIONS.map((name) => Number(Object.keys(labels).find((k) => String(labels[k]).toLowerCase() === name) ?? -1));
    if (order.some((i) => i < 0)) throw new Error('Emotion model labels did not match');
    _emotion = { processor, model, order };
    status.report({ phase: 'ready', model: 'emotion model', device: DEVICE, dtype: DTYPE, loadMs: Math.round(performance.now() - started) });
  })().catch((err) => {
    console.warn('Emotion model did not load:', err);
    _emotionRetryAt = performance.now() + EMOTION_RETRY_MS;
    if (_readyStatus) status.report(_readyStatus);
  }).finally(() => { _emotionLoading = null; });
}

async function readEmotion(image) {
  if (!_emotion) return [];
  const probabilities = softmax((await _emotion.model(await _emotion.processor(image))).logits.data);
  return _emotion.order.map((i) => probabilities[i]);
}

async function read(bitmap) {
  const m = await load();
  const { image } = bitmapToRawImage(m.RawImage, bitmap);
  const started = performance.now();
  const age = softmax((await m.age(await m.ageProcessor(image))).logits.data);
  const presentation = softmax((await m.presentation(await m.presentationProcessor(image))).logits.data);
  const out = await m.descriptor(await m.descriptorProcessor(image));
  // DINOv2's pooled output is its class token after the last norm; the first 384 values either way.
  const descriptor = Array.from((out.pooler_output ?? out.last_hidden_state).data.slice(0, 384));
  const emotion = await readEmotion(image);
  if (!_emotion && !_emotionLoading) loadEmotion(await loadTransformers());
  return { age, presentation, descriptor, emotion, latencyMs: Math.round(performance.now() - started) };
}

self.onmessage = async (event) => {
  const { id, bitmap } = event.data ?? {};
  try {
    if (!bitmap) throw new Error('No face crop to read');
    self.postMessage({ id, ...(await read(bitmap)) });
  } catch (err) {
    self.postMessage({ id, type: 'ERROR', message: String(err?.message ?? err) });
  } finally {
    bitmap?.close();
  }
};
