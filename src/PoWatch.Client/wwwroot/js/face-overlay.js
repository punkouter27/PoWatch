// Draws the face model's landmarks straight over the camera view, about five times a second, so the
// reading is something you can see rather than infer. The points are drawn and thrown away: they are
// never stored, posted to the server or kept after the session stops (docs/adr/0002-*).
//
// The <video> is docked over Live's .live-feed slot by feed-dock.js, so the canvas is created inside
// that slot and drawn with the same object-fit: cover crop the video uses. If Live is not on screen
// there is no slot, and the overlay simply has nothing to draw on.
(function () {
  'use strict';

  const WORKER_URL = 'js/sensing/face-worker.js';
  const CAPTURE_WIDTH = 480;
  const EVERY_MS = 200;
  const DOT_RADIUS = 1.6;

  let _worker = null;
  let _video = null;
  let _canvas = null;
  let _busy = false;
  let _timer = null;

  /** The canvas inside the feed, re-found when Live has been navigated away from and back. */
  function ensureCanvas() {
    if (_canvas?.isConnected) return _canvas;
    const feed = document.querySelector('.live-feed');
    if (!feed) { _canvas = null; return null; }
    _canvas = feed.querySelector('canvas.live-face-mesh');
    if (!_canvas) {
      _canvas = document.createElement('canvas');
      _canvas.className = 'live-face-mesh';
      _canvas.setAttribute('aria-hidden', 'true');
      feed.appendChild(_canvas);
    }
    return _canvas;
  }

  function size(canvas) {
    const box = canvas.getBoundingClientRect();
    const w = Math.max(1, Math.round(box.width));
    const h = Math.max(1, Math.round(box.height));
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
    ctx.fillStyle = 'rgba(94, 234, 212, 0.9)';
    for (const points of faces) {
      for (const point of points) {
        const [x, y] = place(point);
        ctx.beginPath();
        ctx.arc(x, y, DOT_RADIUS, 0, Math.PI * 2);
        ctx.fill();
      }
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
      if (event.data?.faces) draw(event.data.faces);
    };
    _worker.onerror = () => { _busy = false; };
    _timer = setInterval(tick, EVERY_MS);
    tick();
  }

  function stop() {
    if (_timer) { clearInterval(_timer); _timer = null; }
    if (_worker) { _worker.terminate(); _worker = null; }
    _busy = false;
    _video = null;
    if (_canvas) {
      _canvas.getContext('2d')?.clearRect(0, 0, _canvas.width, _canvas.height);
    }
  }

  window.powatchFaces = { start, stop };
})();
