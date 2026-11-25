// Runs MediaPipe's Pose Landmarker on the frames the Live page hands it: 33 body points per person
// (shoulders, elbows, wrists, fingers, hips, knees, ankles, heels, toes), each normalised to the frame
// with a visibility, plus the same 33 in metres around the hips ("world" points), which keep their
// depth so a slump towards the camera can be seen. Same vendored runtime as face-worker.js; the only
// thing downloaded at run time is the ~6 MB lite .task model.
//
// Nothing here stores or sends the points: each frame replaces the last, and the worker is torn down
// when the session stops (body-overlay.js, docs/adr/0003-body-readings-on-device.md).
import { PoseLandmarker, FilesetResolver } from '../../lib/mediapipe-1.2.3/vision_bundle.mjs';

const WASM = new URL('../../lib/mediapipe-1.2.3/wasm', import.meta.url).href;
const MODEL = 'https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_lite/float16/1/pose_landmarker_lite.task';

let _landmarker = null;
let _loading = null;
let _lastAt = 0;

function load() {
  _loading ??= (async () => {
    // A module worker needs the ES-module wasm pair (see face-worker.js).
    const fileset = await FilesetResolver.forVisionTasks(WASM, true);
    return PoseLandmarker.createFromOptions(fileset, {
      baseOptions: { modelAssetPath: MODEL, delegate: 'GPU' },
      runningMode: 'VIDEO',
      numPoses: 3,
    });
  })().catch((err) => {
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
    _lastAt = Math.max(at | 0, _lastAt + 1);
    const result = _landmarker.detectForVideo(bitmap, _lastAt);
    const world = result.worldLandmarks ?? [];
    const bodies = (result.landmarks ?? []).map((points, i) => ({
      points: points.map((p) => [p.x, p.y, p.visibility ?? 0]),
      world: (world[i] ?? []).map((p) => [p.x, p.y, p.z]),
    }));
    self.postMessage({ bodies, aspect: bitmap.height / bitmap.width });
  } catch (err) {
    self.postMessage({ bodies: [], error: String(err?.message ?? err) });
  } finally {
    bitmap.close();
  }
};
