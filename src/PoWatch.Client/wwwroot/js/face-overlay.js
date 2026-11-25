// Draws the face model's landmarks straight over the camera view, about five times a second, so the
// reading is something you can see rather than infer. It also follows each face from frame to frame
// (a slot number per face) and hands .NET two things, both kept in memory for the session only
// (SensingSession.Faces, docs/adr/0001-* and 0002-*):
//   - every half second, each face's 478 points, which .NET scores for traditional beauty;
//   - every 1.5 s, one face's reading from face-reader-worker.js (age band, presentation, and a
//     descriptor for its temporary "Person N" label), read from a crop cut around its landmarks.
// Nothing is stored or posted to the server.
//
// The <video> is fixed in the layout and docked over Live's .live-feed slot by feed-dock.js, so the
// canvas copies the video's box and clip (ensureCanvas) and is drawn with the same object-fit: cover
// crop. When Live is not on screen the video is hidden, and so are the points.
(function () {
  'use strict';

  const WORKER_URL = 'js/sensing/face-worker.js';
  const CAPTURE_WIDTH = 480;
  const EVERY_MS = 200;
  const SEND_EVERY_MS = 500;
  const READ_EVERY_MS = 1500;
  const READ_RETRY_MS = 30000;
  const CROP_SIZE = 224;
  // The crop is this many times the landmarks' box, so it holds the hairline and jaw the classifiers expect.
  const CROP_MARGIN = 1.4;
  // A face this close (share of frame width) to where a slot was last frame is the same face.
  const FOLLOW_DISTANCE = 0.12;
  // Faint on purpose: the picture stays the picture, and the points only say "your face is being read".
  const DOT_RADIUS = 1;
  const DOT_COLOR = 'rgba(94, 234, 212, 0.25)';

  let _worker = null;
  let _reader = null;
  let _video = null;
  let _canvas = null;
  let _busy = false;
  let _reading = false;
  let _readPausedUntil = 0;
  let _timer = null;
  let _readTimer = null;
  let _lastSent = 0;
  let _exports = null;
  let _nextSlot = 1;
  /** The faces in view: { slot, cx, cy, points, reads }. */
  let _slots = [];
  let _aspect = 9 / 16;

  /** The client assembly's [JSExport] methods, as detector-bridge.js gets them. */
  function dotnetExports() {
    _exports ??= Promise.resolve(globalThis.getDotnetRuntime(0))
      .then((runtime) => runtime.getAssemblyExports('PoWatch.Client.dll'))
      .catch((err) => { _exports = null; throw err; });
    return _exports;
  }

  /**
   * The canvas, laid exactly over the video. The video is fixed in the layout and moved over Live's
   * feed slot by feed-dock.js, so a canvas inside the slot would be painted under it; this one copies
   * the video's box and clip instead, and hides with it when Live is not on screen.
   */
  function ensureCanvas() {
    if (!_canvas?.isConnected) {
      _canvas = document.querySelector('canvas.live-face-mesh');
      if (!_canvas) {
        _canvas = document.createElement('canvas');
        _canvas.className = 'live-face-mesh';
        _canvas.setAttribute('aria-hidden', 'true');
        document.body.appendChild(_canvas);
      }
    }
    const { left, top, width, height, clipPath, visibility } = _video.style;
    Object.assign(_canvas.style, { left, top, width, height, clipPath, visibility });
    return visibility === 'visible' ? _canvas : null;
  }

  function size(canvas) {
    const w = Math.max(1, Math.round(parseFloat(canvas.style.width) || 1));
    const h = Math.max(1, Math.round(parseFloat(canvas.style.height) || 1));
    if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
    return { w, h };
  }

  /** Normalised model points to canvas pixels, through the video's cover crop. */
  function placer(w, h) {
    const vw = _video.videoWidth || 16;
    const vh = _video.videoHeight || 9;
    const scale = Math.max(w / vw, h / vh);
    const ox = (w - (vw * scale)) / 2;
    const oy = (h - (vh * scale)) / 2;
    return (point) => [ox + (point[0] * vw * scale), oy + (point[1] * vh * scale)];
  }

  function draw(faces) {
    const canvas = ensureCanvas();
    if (!canvas) return;
    const { w, h } = size(canvas);
    const ctx = canvas.getContext('2d');
    ctx.clearRect(0, 0, w, h);
    if (!faces?.length) return;

    const place = placer(w, h);
    ctx.fillStyle = DOT_COLOR;
    for (const points of faces) {
      for (const point of points) {
        const [x, y] = place(point);
        ctx.beginPath();
        ctx.arc(x, y, DOT_RADIUS, 0, Math.PI * 2);
        ctx.fill();
      }
    }
  }

  function centre(points) {
    let x = 0, y = 0;
    for (const p of points) { x += p[0]; y += p[1]; }
    return [x / points.length, (y / points.length) * _aspect];
  }

  /** Gives each face the slot of the nearest face last frame, or a new one; slots not matched have left. */
  function follow(faces) {
    const free = [..._slots];
    _slots = faces.map((points) => {
      const [cx, cy] = centre(points);
      let best = -1, bestDistance = FOLLOW_DISTANCE;
      free.forEach((s, i) => {
        const d = Math.hypot(s.cx - cx, s.cy - cy);
        if (d < bestDistance) { best = i; bestDistance = d; }
      });
      const known = best >= 0 ? free.splice(best, 1)[0] : null;
      return { slot: known?.slot ?? _nextSlot++, reads: known?.reads ?? 0, cx, cy, points };
    });
  }

  /** Every slot's points to .NET: x, then y in units of the frame's width, 478 per face. */
  async function send() {
    const now = performance.now();
    if (now - _lastSent < SEND_EVERY_MS) return;
    _lastSent = now;
    const slots = new Float64Array(_slots.length);
    const meshes = new Float64Array(_slots.reduce((n, s) => n + (s.points.length * 2), 0));
    let at = 0;
    _slots.forEach((s, i) => {
      slots[i] = s.slot;
      for (const p of s.points) { meshes[at++] = p[0]; meshes[at++] = p[1] * _aspect; }
    });
    try {
      (await dotnetExports()).PoWatch.Client.Services.SensingSession.OnFaceLandmarks(slots, meshes);
    } catch (err) {
      console.warn('Face landmarks were not handed over:', err);
    }
  }

  /** A square crop around one face's landmarks, from the full-size video, resized for the classifiers. */
  function crop(points) {
    const vw = _video.videoWidth, vh = _video.videoHeight;
    let x0 = 1, y0 = 1, x1 = 0, y1 = 0;
    for (const [x, y] of points) {
      x0 = Math.min(x0, x); x1 = Math.max(x1, x);
      y0 = Math.min(y0, y); y1 = Math.max(y1, y);
    }
    const side = Math.min(vw, vh, Math.max((x1 - x0) * vw, (y1 - y0) * vh) * CROP_MARGIN);
    if (side < 16) return null;
    const sx = Math.min(Math.max(0, (((x0 + x1) / 2) * vw) - (side / 2)), vw - side);
    const sy = Math.min(Math.max(0, (((y0 + y1) / 2) * vh) - (side / 2)), vh - side);
    return createImageBitmap(_video, sx, sy, side, side, { resizeWidth: CROP_SIZE, resizeHeight: CROP_SIZE });
  }

  /** Reads the face read least so far (a new face first). */
  async function readOne() {
    if (_reading || !_reader || !_video || !_slots.length || performance.now() < _readPausedUntil) return;
    const face = _slots.reduce((a, b) => (b.reads < a.reads ? b : a));
    _reading = true;
    try {
      const bitmap = await crop(face.points);
      if (!bitmap) return;
      const r = await _reader({ type: 'READ', bitmap }, [bitmap]);
      // follow() rebuilds the slots every frame, so the count goes on the face as it is now.
      const now = _slots.find((s) => s.slot === face.slot);
      if (now) now.reads++;
      (await dotnetExports()).PoWatch.Client.Services.SensingSession.OnFaceReading(
        face.slot, new Float64Array(r.age), new Float64Array(r.presentation), new Float64Array(r.descriptor));
    } catch (err) {
      // A model that could not load is not asked again every 1.5 s; the Live models strip shows why.
      console.warn('Face reading failed:', err);
      _readPausedUntil = performance.now() + READ_RETRY_MS;
    } finally {
      _reading = false;
    }
  }

  function tick() {
    if (!_video || !_worker || _busy) return;
    if (_video.readyState < 2 || !_video.videoWidth) return;
    _busy = true;
    const height = Math.round(CAPTURE_WIDTH * _video.videoHeight / _video.videoWidth);
    createImageBitmap(_video, { resizeWidth: CAPTURE_WIDTH, resizeHeight: height })
      .then((bitmap) => _worker.postMessage({ bitmap, at: Math.round(performance.now()) }, [bitmap]))
      .catch(() => { _busy = false; });
  }

  function start(video) {
    stop();
    if (!video) return;
    _video = video;
    _worker = new Worker(WORKER_URL, { type: 'module' });
    _worker.onmessage = (event) => {
      _busy = false;
      const faces = event.data?.faces;
      if (!faces) return;
      if (event.data.aspect) _aspect = event.data.aspect;
      draw(faces);
      follow(faces);
      send();
    };
    _worker.onerror = () => { _busy = false; };
    _reader ??= window.powatchWorkerRpc?.('sensing/face-reader-worker.js', 'faces') ?? null;
    _reader?.keep();
    _timer = setInterval(tick, EVERY_MS);
    _readTimer = setInterval(readOne, READ_EVERY_MS);
    tick();
  }

  function stop() {
    if (_timer) { clearInterval(_timer); _timer = null; }
    if (_readTimer) { clearInterval(_readTimer); _readTimer = null; }
    if (_worker) { _worker.terminate(); _worker = null; }
    // The face models stay loaded for a quick restart, then their worker is let go (worker-rpc.js).
    _reader?.releaseAfter(window.powatchWorkerRpc.IDLE_MS);
    _busy = false;
    _video = null;
    _slots = [];
    _readPausedUntil = 0;
    if (_canvas) {
      _canvas.getContext('2d')?.clearRect(0, 0, _canvas.width, _canvas.height);
      _canvas.style.visibility = 'hidden';
    }
  }

  window.powatchFaces = { start, stop };
})();
