// Draws a faint stick figure over each person the Pose Landmarker finds (js/sensing/body-worker.js),
// about five times a second, follows each body from frame to frame (a slot number per body) and every
// half second hands .NET each body's 33 points: x, y (units of the frame's width), visibility, then the
// world point in metres (x, y, z). .NET turns them into activity, posture, energy, left/right balance
// and body language for this session only (SensingSession.Bodies, docs/adr/0003-*). Nothing is stored
// or posted to the server.
//
// The canvas copies the docked video's box and clip, as face-overlay.js does.
(function () {
  'use strict';

  const WORKER_URL = 'js/sensing/body-worker.js';
  const CAPTURE_WIDTH = 480;
  const EVERY_MS = 200;
  const SEND_EVERY_MS = 500;
  // A body whose hips are this close (share of frame width) to where a slot was last frame is the same body.
  const FOLLOW_DISTANCE = 0.15;
  // A joint the model is less sure of than this is not drawn.
  const MIN_VISIBILITY = 0.5;
  // Faint on purpose, like the face dots: the picture stays the picture.
  const LINE_COLOR = 'rgba(251, 191, 36, 0.35)';
  const LINE_WIDTH = 1.5;
  // MediaPipe's 33-point skeleton: arms, trunk, legs and feet (the face is the face overlay's job).
  const BONES = [
    [11, 12], [11, 13], [13, 15], [12, 14], [14, 16], [15, 19], [16, 20],
    [11, 23], [12, 24], [23, 24], [23, 25], [25, 27], [24, 26], [26, 28],
    [27, 29], [29, 31], [27, 31], [28, 30], [30, 32], [28, 32],
  ];
  const POINTS = 33;
  const VALUES = 6;

  let _worker = null;
  let _video = null;
  let _canvas = null;
  let _busy = false;
  let _warned = false;
  let _timer = null;
  let _lastSent = 0;
  let _exports = null;
  let _nextSlot = 1;
  /** The bodies in view: { slot, cx, cy, body }. */
  let _slots = [];
  let _aspect = 9 / 16;
  let _visible = true;

  function dotnetExports() {
    _exports ??= Promise.resolve(globalThis.getDotnetRuntime(0))
      .then((runtime) => runtime.getAssemblyExports('PoWatch.Client.dll'))
      .catch((err) => { _exports = null; throw err; });
    return _exports;
  }

  function ensureCanvas() {
    if (!_canvas?.isConnected) {
      _canvas = document.querySelector('canvas.live-body-skeleton');
      if (!_canvas) {
        _canvas = document.createElement('canvas');
        _canvas.className = 'live-body-skeleton';
        _canvas.setAttribute('aria-hidden', 'true');
        document.body.appendChild(_canvas);
      }
    }
    const { left, top, width, height, clipPath, visibility } = _video.style;
    const shown = _visible && visibility === 'visible';
    Object.assign(_canvas.style, { left, top, width, height, clipPath, visibility: shown ? 'visible' : 'hidden' });
    return shown ? _canvas : null;
  }

  function size(canvas) {
    const w = Math.max(1, Math.round(parseFloat(canvas.style.width) || 1));
    const h = Math.max(1, Math.round(parseFloat(canvas.style.height) || 1));
    if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
    return { w, h };
  }

  function placer(w, h) {
    const vw = _video.videoWidth || 16;
    const vh = _video.videoHeight || 9;
    const scale = Math.max(w / vw, h / vh);
    const ox = (w - (vw * scale)) / 2;
    const oy = (h - (vh * scale)) / 2;
    return (point) => [ox + (point[0] * vw * scale), oy + (point[1] * vh * scale)];
  }

  function draw(bodies) {
    const canvas = ensureCanvas();
    if (!canvas) return;
    const { w, h } = size(canvas);
    const ctx = canvas.getContext('2d');
    ctx.clearRect(0, 0, w, h);
    if (!bodies?.length) return;

    const place = placer(w, h);
    ctx.strokeStyle = LINE_COLOR;
    ctx.lineWidth = LINE_WIDTH;
    ctx.lineCap = 'round';
    ctx.beginPath();
    for (const { points } of bodies) {
      for (const [a, b] of BONES) {
        if (points[a][2] < MIN_VISIBILITY || points[b][2] < MIN_VISIBILITY) continue;
        const [x0, y0] = place(points[a]);
        const [x1, y1] = place(points[b]);
        ctx.moveTo(x0, y0);
        ctx.lineTo(x1, y1);
      }
    }
    ctx.stroke();
  }

  /** Where a body is: between its hips, or its shoulders when the hips are out of frame. */
  function centre(points) {
    const [a, b] = points[23][2] >= MIN_VISIBILITY && points[24][2] >= MIN_VISIBILITY ? [23, 24] : [11, 12];
    return [(points[a][0] + points[b][0]) / 2, ((points[a][1] + points[b][1]) / 2) * _aspect];
  }

  function follow(bodies) {
    const free = [..._slots];
    _slots = bodies.map((body) => {
      const [cx, cy] = centre(body.points);
      let best = -1, bestDistance = FOLLOW_DISTANCE;
      free.forEach((s, i) => {
        const d = Math.hypot(s.cx - cx, s.cy - cy);
        if (d < bestDistance) { best = i; bestDistance = d; }
      });
      const known = best >= 0 ? free.splice(best, 1)[0] : null;
      return { slot: known?.slot ?? _nextSlot++, cx, cy, body };
    });
  }

  /** Every slot to .NET: per point x, y (frame-width units), visibility, world x, y, z. */
  async function send() {
    const now = performance.now();
    if (now - _lastSent < SEND_EVERY_MS) return;
    _lastSent = now;
    const slots = new Float64Array(_slots.length);
    const values = new Float64Array(_slots.length * POINTS * VALUES);
    let at = 0;
    _slots.forEach((s, i) => {
      slots[i] = s.slot;
      for (let p = 0; p < POINTS; p++) {
        const point = s.body.points[p] ?? [0, 0, 0];
        const world = s.body.world[p] ?? [0, 0, 0];
        values[at++] = point[0];
        values[at++] = point[1] * _aspect;
        values[at++] = point[2];
        values[at++] = world[0];
        values[at++] = world[1];
        values[at++] = world[2];
      }
    });
    try {
      (await dotnetExports()).PoWatch.Client.Services.SensingSession.OnBodyLandmarks(slots, values, now / 1000);
    } catch (err) {
      console.warn('Body landmarks were not handed over:', err);
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
      const bodies = event.data?.bodies;
      if (!bodies) return;
      // Said once per session: a model that cannot load fails every frame.
      if (event.data.error && !_warned) { _warned = true; console.warn('Body landmarks failed:', event.data.error); }
      if (event.data.aspect) _aspect = event.data.aspect;
      draw(bodies);
      follow(bodies);
      send();
    };
    _worker.onerror = () => { _busy = false; };
    _timer = setInterval(tick, EVERY_MS);
    tick();
  }

  function stop() {
    if (_timer) { clearInterval(_timer); _timer = null; }
    if (_worker) { _worker.terminate(); _worker = null; }
    _busy = false;
    _warned = false;
    _video = null;
    _slots = [];
    if (_canvas) {
      _canvas.getContext('2d')?.clearRect(0, 0, _canvas.width, _canvas.height);
      _canvas.style.visibility = 'hidden';
    }
  }

  function setVisible(visible) {
    _visible = Boolean(visible);
    if (!_visible && _canvas) {
      _canvas.style.visibility = 'hidden';
    }
  }

  window.powatchBodies = { start, stop, setVisible };
})();
