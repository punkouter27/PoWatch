// Draws the face model's landmarks straight over the camera view, about five times a second, so the
// reading is something you can see rather than infer. It also follows each face from frame to frame
// (a slot number per face) and hands .NET two things, both kept in memory for the session only
// (SensingSession.Faces, docs/adr/0001-* and 0002-*):
//   - every half second, each face's 478 points, which .NET scores for traditional beauty;
//   - every 1.5 s, one face's reading from face-reader-worker.js (age band, presentation, emotion mix,
//     and a descriptor for its temporary "Person N" label), read from a crop cut around its landmarks;
//   - blinks counted on every landmarker frame (5 a second, so short blinks are not missed between
//     the half-second hand-overs), and for the experimental heart rate the mean colour of the forehead
//     and cheeks about 15 times a second. Only the colour means go to .NET, never pixels.
// .NET hands back each face's label, and this paints one chip per face on the hairline (Person 1,
// Person 2 …), so the picture itself says who is who. Nothing is stored or posted to the server.
//
// The <video> is fixed in the layout and docked over Live's .live-feed slot by feed-dock.js, so the
// canvas copies the video's box and clip (ensureCanvas) and is drawn with the same object-fit: cover
// crop. When Live is not on screen the video is hidden, and so are the points and the chips.
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
  // MediaPipe's eyeBlink weight above this is a closed eye; a closed-after-open frame is one blink.
  const BLINK_CLOSED = 0.45;
  // Skin colour samples for the heart rate: ~15 a second, from three small patches per face.
  const PULSE_EVERY_MS = 66;
  const PULSE_PATCH = 8;
  const PULSE_MIN_PX = 4;
  // Forehead (between landmarks 108/337 across, 10 down to 9) and cheek centres (50, 280).
  const FOREHEAD_L = 108, FOREHEAD_R = 337, FOREHEAD_TOP = 10, FOREHEAD_BOTTOM = 9;
  const CHEEK_L = 50, CHEEK_R = 280, FACE_L = 234, FACE_R = 454;

  let _worker = null;
  let _reader = null;
  let _video = null;
  let _canvas = null;
  let _tags = null;
  let _busy = false;
  let _reading = false;
  let _readPausedUntil = 0;
  let _timer = null;
  let _readTimer = null;
  let _pulseTimer = null;
  let _pulseCtx = null;
  let _lastSent = 0;
  let _exports = null;
  let _nextSlot = 1;
  /** The faces in view: { slot, cx, cy, points, reads, blinks, closed, pulse }. */
  let _slots = [];
  /** Each face's temporary label, by slot, as .NET last handed it over. */
  let _labels = new Map();
  /** The label chips on screen, by slot, reused rather than rebuilt every frame. */
  const _chips = new Map();
  let _aspect = 9 / 16;
  let _visible = true;
  let _highlightedSlot = null;
  let _blendshapes = [];

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

  /** Normalised model points to canvas pixels, through the video's cover crop. */
  function placer(w, h) {
    const vw = _video.videoWidth || 16;
    const vh = _video.videoHeight || 9;
    const scale = Math.max(w / vw, h / vh);
    const ox = (w - (vw * scale)) / 2;
    const oy = (h - (vh * scale)) / 2;
    return (point) => [ox + (point[0] * vw * scale), oy + (point[1] * vh * scale)];
  }

  /**
   * The chip layer: the video's own box and clip, so a label lands on the face it names through the
   * same cover crop as the points, and hides with the video when Live is not on screen.
   */
  function ensureTags() {
    if (!_tags?.isConnected) {
      _tags = document.querySelector('.live-face-tags');
      if (!_tags) {
        _tags = document.createElement('div');
        _tags.className = 'live-face-tags';
        _tags.setAttribute('aria-hidden', 'true');
        document.body.appendChild(_tags);
      }
    }
    const { left, top, width, height, clipPath, visibility } = _video.style;
    const shown = _visible && visibility === 'visible';
    Object.assign(_tags.style, { left, top, width, height, clipPath, visibility: shown ? 'visible' : 'hidden' });
    return shown ? _tags : null;
  }

  /** Each face's label, by slot, as .NET knows them (SensingSession.PushFaceLabels). */
  function labels(slots, names) {
    _labels = new Map((slots ?? []).map((slot, i) => [Math.round(slot), names?.[i] || '']));
  }

  /**
   * One chip per face on its hairline, saying which person it is; a face no model has read yet wears a
   * "…" chip instead, so it is still visibly one of the counted faces. Chips for faces that have left
   * go with them.
   */
  function paintTags(place) {
    const layer = ensureTags();
    if (!layer) return;
    for (const face of _slots) {
      let x0 = 1, x1 = 0, y0 = 1;
      for (const [x, y] of face.points) {
        if (x < x0) x0 = x;
        if (x > x1) x1 = x;
        if (y < y0) y0 = y;
      }
      const [x, y] = place([(x0 + x1) / 2, y0]);
      let chip = _chips.get(face.slot);
      if (!chip) {
        chip = document.createElement('span');
        chip.className = 'live-face-tag';
        layer.appendChild(chip);
        _chips.set(face.slot, chip);
      }
      const name = _labels.get(face.slot);
      chip.textContent = name || '…';
      chip.dataset.reading = name ? 'false' : 'true';
      chip.classList.toggle('is-highlighted', _highlightedSlot === face.slot);
      chip.style.left = `${Math.round(x)}px`;
      // On the hairline, but never off the top of the picture (the chip is drawn 19px tall above its point).
      chip.style.top = `${Math.max(20, Math.round(y) - 4)}px`;
    }
    const here = new Set(_slots.map((face) => face.slot));
    for (const [slot, chip] of _chips) {
      if (!here.has(slot)) {
        chip.remove();
        _chips.delete(slot);
      }
    }
  }

  function clearTags() {
    for (const chip of _chips.values()) chip.remove();
    _chips.clear();
    _labels = new Map();
    if (_tags) {
      _tags.replaceChildren();
      _tags.style.visibility = 'hidden';
    }
  }

  function draw() {
    const canvas = ensureCanvas();
    if (!canvas) return;
    const { w, h } = size(canvas);
    const ctx = canvas.getContext('2d');
    ctx.clearRect(0, 0, w, h);
    if (!_slots.length) {
      clearTags();
      return;
    }

    const place = placer(w, h);
    ctx.fillStyle = DOT_COLOR;
    for (const face of _slots) {
      for (const point of face.points) {
        const [x, y] = place(point);
        ctx.beginPath();
        ctx.arc(x, y, DOT_RADIUS, 0, Math.PI * 2);
        ctx.fill();
      }
    }
    paintTags(place);
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
      return {
        slot: known?.slot ?? _nextSlot++,
        reads: known?.reads ?? 0,
        blinks: known?.blinks ?? 0,
        closed: known?.closed ?? false,
        pulse: known?.pulse ?? [],
        cx, cy, points,
      };
    });
  }

  /** Counts a blink on each face whose eyes closed this frame after being open the last. */
  function countBlinks() {
    _slots.forEach((face, i) => {
      const blink = _blendshapes[i]?.[2];
      if (blink == null) return;
      const closed = blink > BLINK_CLOSED;
      if (closed && !face.closed) face.blinks++;
      face.closed = closed;
    });
  }

  /**
   * Adds one [seconds, r, g, b] sample per face: the mean colour of its forehead and both cheeks, each
   * patch shrunk to PULSE_PATCH pixels square by the browser (which averages it) on one small canvas.
   */
  function samplePulse() {
    if (!_video || !_slots.length || _video.readyState < 2 || !_video.videoWidth) return;
    const vw = _video.videoWidth, vh = _video.videoHeight;
    if (!_pulseCtx) {
      const canvas = new OffscreenCanvas(PULSE_PATCH * 3, PULSE_PATCH);
      _pulseCtx = canvas.getContext('2d', { willReadFrequently: true });
    }
    const at = performance.now() / 1000;
    for (const face of _slots) {
      const p = face.points;
      if (p.length < 478) continue;
      const faceW = Math.abs(p[FACE_R][0] - p[FACE_L][0]) * vw;
      const cheek = faceW * 0.12;
      const patches = [
        [Math.min(p[FOREHEAD_L][0], p[FOREHEAD_R][0]) * vw, p[FOREHEAD_TOP][1] * vh,
          Math.abs(p[FOREHEAD_R][0] - p[FOREHEAD_L][0]) * vw, (p[FOREHEAD_BOTTOM][1] - p[FOREHEAD_TOP][1]) * vh],
        [(p[CHEEK_L][0] * vw) - (cheek / 2), (p[CHEEK_L][1] * vh) - (cheek / 2), cheek, cheek],
        [(p[CHEEK_R][0] * vw) - (cheek / 2), (p[CHEEK_R][1] * vh) - (cheek / 2), cheek, cheek],
      ];
      let r = 0, g = 0, b = 0, n = 0;
      patches.forEach(([sx, sy, sw, sh], k) => {
        if (sw < PULSE_MIN_PX || sh < PULSE_MIN_PX || sx < 0 || sy < 0 || sx + sw > vw || sy + sh > vh) return;
        _pulseCtx.drawImage(_video, sx, sy, sw, sh, k * PULSE_PATCH, 0, PULSE_PATCH, PULSE_PATCH);
        const data = _pulseCtx.getImageData(k * PULSE_PATCH, 0, PULSE_PATCH, PULSE_PATCH).data;
        for (let i = 0; i < data.length; i += 4) { r += data[i]; g += data[i + 1]; b += data[i + 2]; n++; }
      });
      if (n === 0) continue;
      face.pulse.push(at, r / n, g / n, b / n);
      // Never more than ~15 s waiting if .NET is not taking them.
      if (face.pulse.length > 4 * 240) face.pulse.splice(0, face.pulse.length - (4 * 240));
    }
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
      const exp = (await dotnetExports()).PoWatch.Client.Services.SensingSession;
      exp.OnFaceLandmarks(slots, meshes);
      if (_blendshapes.length === _slots.length && _slots.length > 0) {
        // Five blendshape weights, then the blinks counted on this face so far.
        const bs = new Float64Array(_slots.length * 6);
        for (let i = 0; i < _slots.length; i++) {
          const row = _blendshapes[i] ?? [0, 0, 0, 0, 0];
          for (let j = 0; j < 5; j++) bs[(i * 6) + j] = row[j] ?? 0;
          bs[(i * 6) + 5] = _slots[i].blinks;
        }
        exp.OnFaceBlendshapes(slots, bs);
      }
      const counts = new Float64Array(_slots.length);
      const total = _slots.reduce((sum, s) => sum + s.pulse.length, 0);
      if (total > 0) {
        const samples = new Float64Array(total);
        let k = 0;
        _slots.forEach((s, i) => {
          counts[i] = s.pulse.length / 4;
          samples.set(s.pulse, k);
          k += s.pulse.length;
          s.pulse.length = 0;
        });
        exp.OnFacePulse(slots, counts, samples);
      }
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

  /** Small 96×96 JPEG portrait data URL of a face crop, taken once when a slot is first read. */
  function faceThumbnail(bitmap) {
    try {
      const c = document.createElement('canvas');
      c.width = 96;
      c.height = 96;
      c.getContext('2d')?.drawImage(bitmap, 0, 0, 96, 96);
      return c.toDataURL('image/jpeg', 0.72);
    } catch {
      return '';
    }
  }

  /** Reads the face read least so far (a new face first). */
  async function readOne() {
    if (_reading || !_reader || !_video || !_slots.length || performance.now() < _readPausedUntil) return;
    const face = _slots.reduce((a, b) => (b.reads < a.reads ? b : a));
    _reading = true;
    try {
      const bitmap = await crop(face.points);
      if (!bitmap) return;
      const thumb = face.reads === 0 ? faceThumbnail(bitmap) : '';
      const r = await _reader({ type: 'READ', bitmap }, [bitmap]);
      // follow() rebuilds the slots every frame, so the count goes on the face as it is now.
      const now = _slots.find((s) => s.slot === face.slot);
      if (now) now.reads++;
      (await dotnetExports()).PoWatch.Client.Services.SensingSession.OnFaceReading(
        face.slot, new Float64Array(r.age), new Float64Array(r.presentation), new Float64Array(r.descriptor),
        new Float64Array(r.emotion ?? []), thumb);
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
      _blendshapes = event.data.blendshapes ?? [];
      // Slot numbers first: both the points and their chips are drawn for the faces they now name.
      follow(faces);
      countBlinks();
      draw();
      send();
    };
    _worker.onerror = () => { _busy = false; };
    _reader ??= window.powatchWorkerRpc?.('sensing/face-reader-worker.js', 'faces') ?? null;
    _reader?.keep();
    _timer = setInterval(tick, EVERY_MS);
    _readTimer = setInterval(readOne, READ_EVERY_MS);
    _pulseTimer = setInterval(samplePulse, PULSE_EVERY_MS);
    tick();
  }

  function stop() {
    if (_timer) { clearInterval(_timer); _timer = null; }
    if (_readTimer) { clearInterval(_readTimer); _readTimer = null; }
    if (_pulseTimer) { clearInterval(_pulseTimer); _pulseTimer = null; }
    if (_worker) { _worker.terminate(); _worker = null; }
    // The face models stay loaded for a quick restart, then their worker is let go (worker-rpc.js).
    _reader?.releaseAfter(window.powatchWorkerRpc.IDLE_MS);
    _busy = false;
    _video = null;
    _slots = [];
    _readPausedUntil = 0;
    clearTags();
    if (_canvas) {
      _canvas.getContext('2d')?.clearRect(0, 0, _canvas.width, _canvas.height);
      _canvas.style.visibility = 'hidden';
    }
  }

  function setVisible(visible) {
    _visible = Boolean(visible);
    if (!_visible) {
      if (_canvas) _canvas.style.visibility = 'hidden';
      if (_tags) _tags.style.visibility = 'hidden';
    } else if (_video) {
      draw();
    }
  }

  function highlight(slot) {
    _highlightedSlot = slot == null ? null : Math.round(slot);
    for (const [s, chip] of _chips) {
      chip.classList.toggle('is-highlighted', _highlightedSlot === s);
    }
  }

  window.powatchFaces = { start, stop, labels, setVisible, highlight };
})();
