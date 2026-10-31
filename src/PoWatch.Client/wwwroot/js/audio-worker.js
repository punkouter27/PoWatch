// audio-worker.js
// Hears the room, off the main thread: Whisper (base, English) turns a stretch of speech into words,
// and an AudioSet classifier (AST) names a loud moment ("Dog", "Music", "Knock"). Both run on WASM, as
// the embedder does, so the GPU stays with the detector and the caption model. Audio arrives as 16 kHz
// mono Float32Array, is used once and dropped: nothing is kept or sent anywhere.
// Speaks worker-rpc.js: replies echo the `id`, and a failure is a type 'ERROR' reply.
import { loadTransformers } from './transformers-env.js';

const SPEECH_MODEL = 'Xenova/whisper-base.en';
const SOUND_MODEL = 'Xenova/ast-finetuned-audioset-10-10-0.4593';

let _speech = null;
let _sounds = null;

const speech = () => _speech ??= loadTransformers()
  .then((t) => t.pipeline('automatic-speech-recognition', SPEECH_MODEL, { device: 'wasm', dtype: 'q8' }))
  .catch((err) => { _speech = null; throw err; });

const sounds = () => _sounds ??= loadTransformers()
  .then((t) => t.pipeline('audio-classification', SOUND_MODEL, { device: 'wasm', dtype: 'q8' }))
  .catch((err) => { _sounds = null; throw err; });

// Whisper fills silence and noise with stock phrases; none of these is speech worth keeping.
const NOT_SPEECH = /^\s*(\[[^\]]*\]|\([^)]*\)|thank you\.?|thanks for watching!?|you|\.+|bye\.?)\s*$/i;

self.onmessage = async (e) => {
  const { id, type, samples } = e.data ?? {};
  try {
    if (type === 'warm') {
      await Promise.all([speech(), sounds()]);
      self.postMessage({ id });
    } else if (type === 'transcribe') {
      const result = await (await speech())(samples);
      const text = String(result?.text ?? '').trim();
      self.postMessage({ id, text: NOT_SPEECH.test(text) || text.length < 2 ? '' : text });
    } else if (type === 'classify') {
      const labels = await (await sounds())(samples, { top_k: 3 });
      self.postMessage({ id, labels: (labels ?? []).map((l) => ({ label: l.label, score: l.score })) });
    } else {
      throw new Error(`Unknown request ${type}`);
    }
  } catch (err) {
    self.postMessage({ id, type: 'ERROR', message: String(err?.message ?? err) });
  }
};
