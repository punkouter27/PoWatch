// caption-models.js
// The caption models PoWatch can run, and how the in-browser one is loaded, prompted and decoded. Shared
// by inference-worker.js and the offline caption eval (SCRIPTS/caption-eval), which passes in its own
// copy of transformers.js; nothing here touches the DOM or imports the library itself.

// The models Settings offers. They run on this machine's GPU through its own Ollama server; each was
// captioned against the same frames on an RTX 5070 Ti laptop (2026-10-17), and minicpm-v, the default,
// was nearly as accurate as qwen3-vl at a third of the time. Models that made things up, stalled,
// came back empty or filled the GPU were left out. Keys are Ollama tags.
const OLLAMA_CAPTION_MODELS = ['minicpm-v', 'qwen3-vl:8b-instruct', 'qwen2.5vl:7b'];

export function isOllamaModel(key) {
  return OLLAMA_CAPTION_MODELS.includes(key);
}

// In-browser models, used when no Ollama server answers. Each spec lists the precisions to try per
// backend, best first. The worker walks them on load and again at run time when a backend that
// loaded fine produces nothing (NaN logits never throw).
export const CAPTION_MODELS = {
  // A captioner rather than a chat model: a fixed task token, no instruction to echo, short sentences.
  // On SCRIPTS/caption-eval it came closest to human captions (cosine 0.76 against 0.69 for SmolVLM
  // 500M) with none rejected. It cannot take the detector's hint.
  'florence-2': {
    id: 'onnx-community/Florence-2-base-ft',
    // fp16 decoders produce garbage on WebGPU; the q4 encoder/decoder is the published working mix.
    webgpu: [{ embed_tokens: 'fp16', vision_encoder: 'fp16', encoder_model: 'q4', decoder_model_merged: 'q4' }, 'fp32'],
    wasm: ['q8'],
    task: '<CAPTION>',
  },
};

export const DEFAULT_CAPTION_MODEL = 'florence-2';

export function captionSpec(key) {
  return CAPTION_MODELS[key] ?? CAPTION_MODELS[DEFAULT_CAPTION_MODEL];
}

export function dtypeName(dtype) {
  return typeof dtype === 'string' ? dtype : 'mixed';
}

export function loadProcessor(transformers, spec) {
  return transformers.AutoProcessor.from_pretrained(spec.id);
}

export function loadModel(transformers, spec, device, dtype) {
  return transformers.Florence2ForConditionalGeneration.from_pretrained(spec.id, { device, dtype });
}

/** Model inputs for one frame. Florence takes its task token, not the chat prompt. */
export async function prepareInputs(processor, spec, image) {
  return processor(image, processor.construct_prompts(spec.task));
}

export function generateOptions(maxNewTokens) {
  return {
    max_new_tokens: maxNewTokens,
    do_sample: false,
    // Greedy decoding on a small model loops ("the the the…"); forbidding a repeated trigram
    // stops that at the source instead of rejecting the caption afterwards.
    no_repeat_ngram_size: 3,
  };
}

/** The reply as plain text. */
export function decodeGenerated(processor, spec, generatedIds, inputs, image) {
  const raw = processor.batch_decode(generatedIds, { skip_special_tokens: false })[0] ?? '';
  const parsed = processor.post_process_generation(raw, spec.task, image.size);
  return String(parsed?.[spec.task] ?? '').trim();
}
