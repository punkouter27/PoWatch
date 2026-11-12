// PoWatch caption eval: runs each caption model the app offers over a fixed set of frames and scores
// what the app would keep. It imports the app's own caption-models.js and caption-quality.js, so the
// prompt, decoding and rejection rules are exactly the browser's; only the runtime differs (ONNX
// Runtime on this machine's CPU, so latencies compare models with each other, not with a GPU).
//
// Frames: indoor scenes from the COCO Karpathy test split, each with five human captions, fetched
// once into .cache/. Pass your own folder with --frames <dir> (images + captions.json: { "file.jpg":
// ["reference", …] }) to score webcam frames instead.
//
// For each model: load time, latency (mean, p50, p95), how many replies were kept and why the rest were
// rejected, and how close kept captions are to the references (best MiniLM cosine against the five).
// It also measures how close captions of the same scene are against captions of different scenes,
// which is what HighlightRules.NoveltyMaxCosine has to separate.
//
//   node eval.mjs [--models florence-2] [--count 40] [--frames dir] [--out results/x.json]

import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import * as transformers from '@huggingface/transformers';
import { CAPTION_MODELS, captionSpec, decodeGenerated, generateOptions, loadModel, loadProcessor, prepareInputs } from '../../src/PoWatch.Client/wwwroot/js/caption-models.js';
import { judgeCaption } from '../../src/PoWatch.Client/wwwroot/js/caption-quality.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, a, i, all) => (a.startsWith('--') ? [...pairs, [a.slice(2), all[i + 1]]] : pairs), []));
const MODELS = (args.models ?? Object.keys(CAPTION_MODELS).join(',')).split(',');
const COUNT = Number(args.count ?? 40);
const MAX_EDGE = 512;      // inference-bridge.js sends at most 512 px on the long edge
const MAX_NEW_TOKENS = 32; // CaptionParser.MaxNewTokens
// CaptionParser.Instruction; the app adds "Visible: …" from the detector, which COCO frames lack.
const PROMPT = 'Describe what is happening in this scene in one short sentence.';
const INDOOR = /\b(living room|kitchen|bedroom|couch|sofa|bed|desk|table|television|tv|laptop|cat|dog|chair|office|room)\b/i;

async function cachedFrames() {
  const dir = path.join(here, '.cache', 'coco');
  const manifest = path.join(dir, 'captions.json');
  try {
    const refs = JSON.parse(await fs.readFile(manifest, 'utf8'));
    if (Object.keys(refs).length >= COUNT) return { dir, refs };
  } catch { /* first run */ }

  await fs.mkdir(dir, { recursive: true });
  const refs = {};
  for (let offset = 0; Object.keys(refs).length < COUNT && offset < 2000; offset += 100) {
    const url = `https://datasets-server.huggingface.co/rows?dataset=yerevann/coco-karpathy&config=default&split=test&offset=${offset}&length=100`;
    const { rows } = await (await fetch(url)).json();
    for (const { row } of rows) {
      if (Object.keys(refs).length >= COUNT) break;
      if (!row.sentences.some((s) => INDOOR.test(s))) continue;
      const image = await fetch(row.url);
      if (!image.ok) continue;
      await fs.writeFile(path.join(dir, row.filename), Buffer.from(await image.arrayBuffer()));
      refs[row.filename] = row.sentences.map((s) => s.trim());
    }
  }
  await fs.writeFile(manifest, JSON.stringify(refs, null, 1));
  return { dir, refs };
}

async function framesFrom(dir) {
  return { dir, refs: JSON.parse(await fs.readFile(path.join(dir, 'captions.json'), 'utf8')) };
}

const percentile = (values, p) => {
  const sorted = [...values].sort((a, b) => a - b);
  return sorted.length ? sorted[Math.min(sorted.length - 1, Math.floor(p * sorted.length))] : null;
};
const mean = (values) => (values.length ? values.reduce((a, b) => a + b, 0) / values.length : null);
const round = (v, digits = 3) => (v == null ? null : Math.round(v * 10 ** digits) / 10 ** digits);

const embedder = await transformers.pipeline('feature-extraction', 'Xenova/all-MiniLM-L6-v2', { dtype: 'q8' });
async function embed(texts) {
  const out = await embedder(texts, { pooling: 'mean', normalize: true });
  const [rows, dims] = out.dims;
  return Array.from({ length: rows }, (_, r) => out.data.subarray(r * dims, (r + 1) * dims));
}
const dot = (a, b) => a.reduce((sum, v, i) => sum + v * b[i], 0);

async function loadImage(file) {
  const image = await transformers.RawImage.read(file);
  const scale = Math.min(1, MAX_EDGE / Math.max(image.width, image.height));
  const sized = scale < 1 ? await image.resize(Math.round(image.width * scale), Math.round(image.height * scale)) : image;
  return sized.rgb();
}

async function evaluate(key, frames) {
  const spec = captionSpec(key);
  const started = performance.now();
  const processor = await loadProcessor(transformers, spec);
  const model = await loadModel(transformers, spec, 'cpu', spec.wasm[0]);
  const loadMs = Math.round(performance.now() - started);

  const latencies = [], similarities = [], reasons = {}, samples = [];
  for (const [file, references] of Object.entries(frames.refs)) {
    const image = await loadImage(path.join(frames.dir, file));
    const t0 = performance.now();
    const inputs = await prepareInputs(processor, spec, image, PROMPT);
    const ids = await model.generate({ ...inputs, ...generateOptions(MAX_NEW_TOKENS) });
    const output = decodeGenerated(processor, spec, ids, inputs, image);
    latencies.push(performance.now() - t0);

    const verdict = judgeCaption(output, PROMPT);
    if (!verdict.ok) {
      reasons[verdict.reason] = (reasons[verdict.reason] ?? 0) + 1;
      samples.push({ file, output, rejected: verdict.reason });
      continue;
    }
    const [caption, ...refs] = await embed([verdict.activity, ...references]);
    const best = Math.max(...refs.map((r) => dot(caption, r)));
    similarities.push(best);
    samples.push({ file, caption: verdict.activity, similarity: round(best), reference: references[0] });
  }
  await model.dispose?.();

  const total = Object.keys(frames.refs).length;
  return {
    model: key,
    id: spec.id,
    dtype: spec.wasm[0],
    loadMs,
    latencyMs: { mean: Math.round(mean(latencies)), p50: Math.round(percentile(latencies, 0.5)), p95: Math.round(percentile(latencies, 0.95)) },
    kept: `${total - Object.values(reasons).reduce((a, b) => a + b, 0)} / ${total}`,
    rejected: reasons,
    similarity: { mean: round(mean(similarities)), p10: round(percentile(similarities, 0.1)) },
    samples,
  };
}

// Same scene (two human captions of one frame) against different scenes: novelty must sit between them.
async function noveltyCalibration(frames) {
  const lists = Object.values(frames.refs);
  const same = [], different = [];
  const vectors = await Promise.all(lists.map((refs) => embed(refs.slice(0, 2))));
  vectors.forEach(([a, b], i) => {
    same.push(dot(a, b));
    const other = vectors[(i + 1) % vectors.length][0];
    different.push(dot(a, other));
  });
  return {
    sameSceneCosine: { p10: round(percentile(same, 0.1)), median: round(percentile(same, 0.5)) },
    differentSceneCosine: { median: round(percentile(different, 0.5)), p90: round(percentile(different, 0.9)) },
  };
}

const frames = args.frames ? await framesFrom(args.frames) : await cachedFrames();
console.log(`${Object.keys(frames.refs).length} frames from ${frames.dir}`);
const report = { at: new Date().toISOString(), prompt: PROMPT, novelty: await noveltyCalibration(frames), models: [] };
for (const key of MODELS) {
  console.log(`\n${key} …`);
  const result = await evaluate(key, frames);
  report.models.push(result);
  const { samples, ...summary } = result;
  console.log(JSON.stringify(summary, null, 1));
}
console.log('\nnovelty', JSON.stringify(report.novelty));

const out = args.out ?? path.join(here, 'results', `${report.at.slice(0, 10)}.json`);
await fs.mkdir(path.dirname(out), { recursive: true });
await fs.writeFile(out, JSON.stringify(report, null, 1));
console.log(`\nfull report: ${out}`);
