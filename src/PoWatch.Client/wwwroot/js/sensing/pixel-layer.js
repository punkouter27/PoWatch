// L0 pixel layer: cheap per-frame measurements that need no model.
//
// Each sample downscales the current video frame to 64×36 — exactly 4×4 pixels per cell of the
// 16×9 grid the server aggregates — and returns:
//   motion     fraction of pixels whose RGB changed by more than the threshold since the last sample
//   grid       the same fraction per grid cell (144 values, row-major, top-left first)
//   luminance  mean Rec.709 luma in [0, 1]
//   palette    up to five dominant colours as 0xRRGGBB, binned 4 bits per channel like the server
//
// The sampling clock lives in a dedicated worker (sample-clock.js): a hidden tab throttles
// main-thread timers and stops requestAnimationFrame, but dedicated workers keep ticking.
(function () {
  'use strict';

  const COLS = 16;
  const ROWS = 9;
  const CELL = 4;
  const WIDTH = COLS * CELL;   // 64
  const HEIGHT = ROWS * CELL;  // 36
  const DELTA = 28;            // same per-pixel threshold as the VLM's frame-diff gate
  const TIMELAPSE_MS = 60000;  // one time-lapse frame a minute (timelapse.js)

  // currentScript is only set while this file first runs, so resolve the worker URL now.
  const CLOCK_URL = new URL('./sample-clock.js', document.currentScript?.src ?? location.href);

  let _canvas = null;
  let _ctx = null;
  let _previous = null;
  let _clock = null;
  let _wakeLock = null;

  function context() {
    if (!_ctx) {
      _canvas = document.createElement('canvas');
      _canvas.width = WIDTH;
      _canvas.height = HEIGHT;
      _ctx = _canvas.getContext('2d', { willReadFrequently: true });
    }
    return _ctx;
  }

  function binCentre(bin) {
    const channel = (nibble) => (nibble << 4) | 0x8;
    return (channel((bin >> 8) & 0xF) << 16) | (channel((bin >> 4) & 0xF) << 8) | channel(bin & 0xF);
  }

  /** Measures one frame. Returns null when the video has no frame yet. */
  function sample(video) {
    if (!video || video.readyState < 2 || !video.videoWidth) return null;

    const ctx = context();
    ctx.drawImage(video, 0, 0, WIDTH, HEIGHT);
    const pixels = ctx.getImageData(0, 0, WIDTH, HEIGHT).data;

    const changedPerCell = new Array(COLS * ROWS).fill(0);
    const bins = new Map();
    let changed = 0;
    let lumaSum = 0;

    for (let y = 0; y < HEIGHT; y++) {
      for (let x = 0; x < WIDTH; x++) {
        const i = (y * WIDTH + x) * 4;
        const r = pixels[i], g = pixels[i + 1], b = pixels[i + 2];

        lumaSum += 0.2126 * r + 0.7152 * g + 0.0722 * b;

        const bin = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
        bins.set(bin, (bins.get(bin) || 0) + 1);

        if (_previous) {
          const d = Math.abs(r - _previous[i]) + Math.abs(g - _previous[i + 1]) + Math.abs(b - _previous[i + 2]);
          if (d / 3 > DELTA) {
            changed++;
            changedPerCell[Math.floor(y / CELL) * COLS + Math.floor(x / CELL)]++;
          }
        }
      }
    }

    const first = _previous === null;
    _previous = pixels.slice();
    const total = WIDTH * HEIGHT;
    const perCell = CELL * CELL;

    return {
      motion: first ? 0 : changed / total,
      grid: changedPerCell.map((n) => (first ? 0 : n / perCell)),
      luminance: lumaSum / total / 255,
      palette: [...bins.entries()].sort((a, b) => b[1] - a[1]).slice(0, 5).map(([bin]) => binCentre(bin)),
      atUtc: new Date().toISOString()
    };
  }

  /**
   * Starts a worker-driven clock that calls back into .NET with a pixel sample every
   * `intervalMs` (default 250 ms = 4 Hz). Also takes a screen wake lock so an unattended
   * laptop keeps the display — and the camera — awake.
   */
  async function start(video, dotnetRef, intervalMs) {
    stop();
    _previous = null;
    const every = Math.max(1, Math.round(TIMELAPSE_MS / (intervalMs || 250)));
    let ticks = 0;
    _clock = new Worker(CLOCK_URL, { type: 'module' });
    _clock.onmessage = () => {
      const s = sample(video);
      // Only ticks with a frame count, so the first time-lapse frame lands as soon as the camera has one.
      if (s && ticks++ % every === 0) window.powatchTimelapse?.capture(video);
      // JSON text, parsed by .NET's source-generated context: trim-safe, no reflection.
      if (s) dotnetRef.invokeMethodAsync('OnPixelSample', JSON.stringify(s)).catch(() => { /* circuit gone */ });
    };
    _clock.postMessage({ intervalMs: intervalMs || 250 });

    try {
      if ('wakeLock' in navigator) _wakeLock = await navigator.wakeLock.request('screen');
    } catch { /* wake lock is best-effort (denied, or the page is hidden) */ }
  }

  function stop() {
    if (_clock) { _clock.terminate(); _clock = null; }
    if (_wakeLock) { _wakeLock.release().catch(() => { }); _wakeLock = null; }
    _previous = null;
  }

  /** A highlight snapshot: the current frame as a 640-px-wide JPEG data URL, or null without a frame. */
  function snapshot(video) {
    if (!video || video.readyState < 2 || !video.videoWidth) return null;
    const width = Math.min(640, video.videoWidth);
    const height = Math.round(width * video.videoHeight / video.videoWidth);
    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    canvas.getContext('2d').drawImage(video, 0, 0, width, height);
    return canvas.toDataURL('image/jpeg', 0.8);
  }

  window.powatchPixels = { start, stop, snapshot };
})();
