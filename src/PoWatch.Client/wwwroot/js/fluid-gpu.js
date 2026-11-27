// Thermal contour shader (#5) and 2D Navier-Stokes fluid advection field (#15) for the Space panel.
(() => {
  'use strict';
  const COLS = 16, ROWS = 9;
  const W = 64, H = 36;
  const density = new Float32Array(W * H);
  const vx = new Float32Array(W * H);
  const vy = new Float32Array(W * H);
  let _canvas = null, _mode = 'thermal', _raf = 0;

  function viridis(t) {
    t = Math.max(0, Math.min(1, t));
    const r = Math.round(255 * Math.min(1, Math.max(0, -0.05 + 1.6 * t * t)));
    const g = Math.round(255 * Math.min(1, Math.max(0, Math.sin(t * Math.PI * 0.92))));
    const b = Math.round(255 * Math.min(1, Math.max(0, 0.35 + 0.55 * (1 - t) - 0.35 * t * t)));
    return [r, g, b];
  }

  function inject(grid144) {
    if (!grid144 || grid144.length !== 144) return;
    for (let gy = 0; gy < ROWS; gy++) {
      for (let gx = 0; gx < COLS; gx++) {
        const val = Math.max(0, Math.min(1, Number(grid144[(gy * COLS) + gx]) || 0));
        if (val <= 0.01) continue;
        const sx = Math.floor((gx + 0.5) * (W / COLS));
        const sy = Math.floor((gy + 0.5) * (H / ROWS));
        for (let dy = -2; dy <= 2; dy++) {
          for (let dx = -2; dx <= 2; dx++) {
            const x = sx + dx, y = sy + dy;
            if (x < 1 || x >= W - 1 || y < 1 || y >= H - 1) continue;
            const falloff = Math.exp(-((dx * dx) + (dy * dy)) / 3.5);
            const idx = (y * W) + x;
            density[idx] = Math.min(1, density[idx] + val * falloff * (_mode === 'fluid' ? 0.45 : 0.7));
            if (_mode === 'fluid') {
              vx[idx] += (dx * 0.12 + (Math.random() - 0.5) * 0.15) * val;
              vy[idx] += (dy * 0.12 - 0.08) * val;
            }
          }
        }
      }
    }
  }

  function stepFluid() {
    const next = new Float32Array(W * H);
    for (let y = 1; y < H - 1; y++) {
      for (let x = 1; x < W - 1; x++) {
        const i = (y * W) + x;
        const px = Math.max(1, Math.min(W - 2, x - vx[i]));
        const py = Math.max(1, Math.min(H - 2, y - vy[i]));
        const x0 = Math.floor(px), y0 = Math.floor(py);
        const fx = px - x0, fy = py - y0;
        const d00 = density[(y0 * W) + x0], d10 = density[(y0 * W) + x0 + 1];
        const d01 = density[((y0 + 1) * W) + x0], d11 = density[((y0 + 1) * W) + x0 + 1];
        const advected = ((d00 * (1 - fx) + d10 * fx) * (1 - fy)) + ((d01 * (1 - fx) + d11 * fx) * fy);
        const lap = (density[i - 1] + density[i + 1] + density[i - W] + density[i + W]) * 0.25;
        next[i] = ((advected * 0.82) + (lap * 0.18)) * 0.94;
        vx[i] *= 0.92;
        vy[i] *= 0.92;
      }
    }
    density.set(next);
  }

  function render() {
    if (!_canvas) { _raf = 0; return; }
    if (_canvas.width !== W || _canvas.height !== H) { _canvas.width = W; _canvas.height = H; }
    if (_mode === 'fluid') stepFluid();
    else for (let i = 0; i < density.length; i++) density[i] *= 0.88;

    const ctx = _canvas.getContext('2d');
    const img = ctx.createImageData(W, H);
    for (let i = 0; i < W * H; i++) {
      const v = density[i];
      const [r, g, b] = viridis(v);
      const p = i * 4;
      img.data[p] = r;
      img.data[p + 1] = g;
      img.data[p + 2] = b;
      img.data[p + 3] = Math.round(Math.min(255, 40 + v * 215));
    }
    ctx.putImageData(img, 0, 0);
    _raf = requestAnimationFrame(render);
  }

  function mount(canvas, mode) {
    _canvas = canvas;
    _mode = mode || 'thermal';
    density.fill(0); vx.fill(0); vy.fill(0);
    if (!_raf) _raf = requestAnimationFrame(render);
  }

  function update(grid144, mode) {
    if (mode) _mode = mode;
    inject(grid144);
  }

  function release() {
    _canvas = null;
    if (_raf) { cancelAnimationFrame(_raf); _raf = 0; }
  }

  window.powatchFluid = { mount, update, release };
})();
