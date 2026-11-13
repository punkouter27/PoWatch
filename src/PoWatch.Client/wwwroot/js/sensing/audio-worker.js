// audio-worker.js
// Hears the room, off the main thread: Whisper (base, English) turns a stretch of speech into words,
// WavLM (speaker verification) turns the same stretch into a voice embedding, so the page can tell who
// said it (VoiceBook, kept in the browser), and an AudioSet classifier (AST) names a loud moment
// ("Dog", "Music", "Knock"). All three run on WASM, as the embedder does, so the GPU stays with the
// detector and the caption model. Audio arrives as 16 kHz mono Float32Array, is used once and dropped:
// nothing is kept or sent anywhere.
// Speaks worker-rpc.js: replies echo the `id`, and a failure is a type 'ERROR' reply.
import { loadTransformers, modelStatus } from './transformers-env.js';

const SPEECH_MODEL = 'Xenova/whisper-base.en';
const SOUND_MODEL = 'Xenova/ast-finetuned-audioset-10-10-0.4593';
const VOICE_MODEL = 'Xenova/wavlm-base-plus-sv';
// Under a second of speech says too little about a voice to be worth an embedding.
const VOICE_MIN_SAMPLES = 16000;

let _speech = null;
let _sounds = null;
let _voice = null;

// The Live strip's HEARS chip (worker-rpc.js files it under 'hearing'). Whisper and AST load together and
// report as one download ('speech'); the voice model loads later on its own ('voice').
const status = modelStatus();
let _speechProgress = null;
const speechProgress = () => _speechProgress ??= status.progress('speech', 'wasm', 'q8');

const speech = () => _speech ??= loadTransformers()
  .then((t) => t.pipeline('automatic-speech-recognition', SPEECH_MODEL, { device: 'wasm', dtype: 'q8', progress_callback: speechProgress() }))
  .catch((err) => { _speech = null; throw err; });

const sounds = () => _sounds ??= loadTransformers()
  .then((t) => t.pipeline('audio-classification', SOUND_MODEL, { device: 'wasm', dtype: 'q8', progress_callback: speechProgress() }))
  .catch((err) => { _sounds = null; throw err; });

/** A function from 16 kHz samples to a 512-number voice embedding. */
const voice = () => _voice ??= loadTransformers()
  .then(async (t) => {
    const onProgress = status.progress('voice', 'wasm', 'fp32');
    const processor = await t.AutoProcessor.from_pretrained(VOICE_MODEL);
    // Full precision: int8 blurred the embeddings until two speakers looked as alike as one.
    const model = await t.AutoModelForXVector.from_pretrained(VOICE_MODEL, { device: 'wasm', dtype: 'fp32', progress_callback: onProgress });
    // All three are in: back to the one chip for hearing.
    status.report({ phase: 'ready', model: 'speech', device: 'wasm', dtype: 'q8' });
    return async (samples) => {
      const { embeddings } = await model(await processor(samples));
      return Array.from(embeddings.data, (v) => Math.round(v * 1e5) / 1e5);
    };
  })
  .catch((err) => {
    _voice = null;
    // Words still come without it, so the chip stays ready and the hover says what is missing.
    status.report({ phase: 'ready', model: 'speech', device: 'wasm', dtype: 'q8', error: `Voices off: the voice model did not load (${err?.message ?? err})` });
    throw err;
  });

// Whisper fills silence and noise with stock phrases; none of these is speech worth keeping.
const NOT_SPEECH = /^\s*(\[[^\]]*\]|\([^)]*\)|thank you\.?|thanks for watching!?|you|\.+|bye\.?)\s*$/i;

self.onmessage = async (e) => {
  const { id, type, samples } = e.data ?? {};
  try {
    if (type === 'warm') {
      // The voice model (~400 MB, once) loads on the first line heard instead, so it never delays these.
      const started = performance.now();
      try {
        await Promise.all([speech(), sounds()]);
      } catch (err) {
        status.report({ phase: 'failed', model: 'speech', device: 'wasm', error: String(err?.message ?? err) });
        throw err;
      }
      status.report({ phase: 'ready', model: 'speech', device: 'wasm', dtype: 'q8', loadMs: Math.round(performance.now() - started) });
      self.postMessage({ id });
    } else if (type === 'transcribe') {
      const result = await (await speech())(samples);
      let text = String(result?.text ?? '').trim();
      if (NOT_SPEECH.test(text) || text.length < 2) text = '';
      // Who said it is optional: without the voice model the words still count.
      let print = null;
      if (text && samples.length >= VOICE_MIN_SAMPLES) {
        try { print = await (await voice())(samples); } catch { print = null; }
      }
      self.postMessage({ id, text, voice: print });
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
