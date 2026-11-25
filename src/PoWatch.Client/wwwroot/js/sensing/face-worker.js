// Runs MediaPipe's Face Landmarker on the frames the Live page hands it: 478 landmarks per face,
// normalised to the frame. The runtime is vendored under wwwroot/lib/mediapipe-1.2.3/; the only thing
// downloaded at run time is the ~4 MB .task model, from the same kind of host as the HF models.
//
// The points are what the page draws. Nothing here stores, sends or keeps them: each frame replaces
// the last, and the worker is torn down when the session stops.
import { FaceLandmarker, FilesetResolver } from '../../lib/mediapipe-1.2.3/vision_bundle.mjs';

const WASM = new URL('../../lib/mediapipe-1.2.3/wasm', import.meta.url).href;
const MODEL = 'https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/1/face_landmarker.task';

let _landmarker = null;
let _loading = null;
let _lastAt = 0;

function load() {
  _loading ??= (async () => {
    // `true` picks the ES-module loader (vision_wasm_module_internal.*): this is a module worker, where
    // the classic loader is import()ed, keeps ModuleFactory to itself, and every frame fails with
    // "ModuleFactory not set." The classic pair stays vendored for a classic worker.
    const fileset = await FilesetResolver.forVisionTasks(WASM, true);
    return FaceLandmarker.createFromOptions(fileset, {
      baseOptions: { modelAssetPath: MODEL, delegate: 'GPU' },
      runningMode: 'VIDEO',
      numFaces: 4,
    });
  })().catch((err) => {
    // A failed download is not remembered: the next frame tries again, as the detector does.
    _loading = null;
    throw err;
  });
  return _loading;
}

self.onmessage = async (event) => {
  const { bitmap, at } = event.data ?? {};
  if (!bitmap) return;
  try {
    _landmarker ??= await load();
    // MediaPipe wants a strictly increasing timestamp; a throttled tab can hand us the same one twice.
    _lastAt = Math.max(at | 0, _lastAt + 1);
    const started = performance.now();
    const result = _landmarker.detectForVideo(bitmap, _lastAt);
    const faces = (result.faceLandmarks ?? []).map((points) => points.map((p) => [p.x, p.y]));
    // Points are normalised to each side; the page needs the frame's shape to make lengths compare.
    self.postMessage({ faces, aspect: bitmap.height / bitmap.width, latencyMs: Math.round(performance.now() - started) });
  } catch (err) {
    self.postMessage({ faces: [], error: String(err?.message ?? err) });
  } finally {
    bitmap.close();
  }
};
