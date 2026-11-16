// caption-models.js
// The caption models PoWatch can run, and how the in-browser one is loaded, prompted and decoded. Shared
// by inference-worker.js and the offline caption eval (scripts/caption-eval), which passes in its own
// copy of transformers.js; nothing here touches the DOM or imports the library itself.

// The models Settings offers. They run on this machine's GPU through its own Ollama server; each was
// captioned against the same frames on an RTX 5070 Ti laptop (2026-10-17), and minicpm-v, the default,
// was nearly as accurate as qwen3-vl at a third of the time. Models that made things up, stalled,
// came back empty or filled the GPU were left out. Keys are Ollama tags.
const OLLAMA_CAPTION_MODELS = ['minicpm-v', 'qwen3-vl:8b-instruct', 'qwen2.5vl:7b'];

export function isOllamaModel(key) {
  return OLLAMA_CAPTION_MODELS.includes(key);
}

// In-browser models (WebGPU, else the CPU): picked in Settings, and Florence-2 stands in for an Ollama
// model when no Ollama server answers. Each spec lists the precisions to try per backend, best first.
// The worker walks them on load and again at run time when a backend that loaded fine produces nothing
// (NaN logits never throw).
export const CAPTION_MODELS = {
  // A captioner rather than a chat model: a fixed task token, no instruction to echo, short sentences.
  // On scripts/caption-eval it came closest to human captions (cosine 0.76 against 0.69 for SmolVLM
  // 500M) with none rejected. It cannot take the detector's hint.
  'florence-2': {
    id: 'onnx-community/Florence-2-base-ft',
    kind: 'florence',
    // fp16 decoders produce garbage on WebGPU; the q4 encoder/decoder is the published working mix.
    webgpu: [{ embed_tokens: 'fp16', vision_encoder: 'fp16', encoder_model: 'q4', decoder_model_merged: 'q4' }, 'fp32'],
    wasm: ['q8'],
    task: '<CAPTION>',
  },
  // A chat VLM that reads the detector's "Visible: …" hint, so it names what the detector saw; wordier.
  'smolvlm-500m': {
    id: 'HuggingFaceTB/SmolVLM2-500M-Video-Instruct',
    kind: 'smolvlm',
    webgpu: ['fp16', 'fp32'],
    wasm: ['q8'],
  },
  // Half the size of SmolVLM 500M, for slow GPUs; same prompt, vaguer.
  'smolvlm-256m': {
    id: 'HuggingFaceTB/SmolVLM2-256M-Video-Instruct',
    kind: 'smolvlm',
    webgpu: ['fp16', 'fp32'],
    wasm: ['q8'],
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

/** `onProgress` is transformers.js's progress_callback (inference-worker.js reports it to the page); optional. */
export function loadModel(transformers, spec, device, dtype, onProgress) {
  const ModelClass = spec.kind === 'florence' ? transformers.Florence2ForConditionalGeneration : transformers.AutoModelForImageTextToText;
  return ModelClass.from_pretrained(spec.id, { device, dtype, progress_callback: onProgress });
}

/** Model inputs for one frame. `prompt` is the chat prompt; Florence takes its task token instead. */
export async function prepareInputs(processor, spec, image, prompt) {
  if (spec.kind === 'florence') {
    return processor(image, processor.construct_prompts(spec.task));
  }
  const messages = [{ role: 'user', content: [{ type: 'image' }, { type: 'text', text: prompt }] }];
  const text = processor.apply_chat_template(messages, { add_generation_prompt: true });
  // SmolVLM otherwise tiles the frame into up to 16 crops plus a global view: hundreds of image
  // tokens for a one-sentence caption. One global view is enough.
  return processor(text, [image], { do_image_splitting: false });
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
  if (spec.kind === 'florence') {
    const raw = processor.batch_decode(generatedIds, { skip_special_tokens: false })[0] ?? '';
    const parsed = processor.post_process_generation(raw, spec.task, image.size);
    return String(parsed?.[spec.task] ?? '').trim();
  }

  const newTokenIds = generatedIds.slice(null, [inputs.input_ids.dims[1], null]);
  let output = processor.batch_decode(newTokenIds, { skip_special_tokens: true })[0].trim();
  if (output.length === 0) {
    // Some backends echo the whole conversation; recover the assistant's reply from the full decode.
    try {
      const full = (processor.batch_decode(generatedIds, { skip_special_tokens: true })[0] ?? '').trim();
      const split = full.split(/Assistant:\s*/i);
      if (split.length > 1) output = split[split.length - 1].trim();
    } catch {
      // Nothing to recover.
    }
  }
  return output;
}
