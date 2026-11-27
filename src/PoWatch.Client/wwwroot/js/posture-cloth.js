// Reactive 18×6 spring-mass cloth/ribbon visualizer (#20) driven by session limb energy & posture score.
(() => {
  'use strict';
  let _canvas = null, _raf = 0, _energy = [], _posture = 80;

  function render(now) {
    if (!_canvas) { _raf = 0; return; }
    const w = Math.max(140, _canvas.clientWidth || 220);
    const h = Math.max(54, _canvas.clientHeight || 64);
    if (_canvas.width !== w || _canvas.height !== h) { _canvas.width = w; _canvas.height = h; }
    const ctx = _canvas.getContext('2d');
    ctx.clearRect(0, 0, w, h);

    const cols = 18, rows = 6;
    const slouch = Math.max(0, Math.min(1, (100 - _posture) / 60));
    const t = now * 0.0022;
    const pts = [];

    for (let r = 0; r < rows; r++) {
      const row = [];
      for (let c = 0; c < cols; c++) {
        const u = c / (cols - 1);
        const v = r / (rows - 1);
        const eIdx = Math.min(_energy.length - 1, Math.floor(u * _energy.length));
        const amp = (_energy.length > 0 ? _energy[Math.max(0, eIdx)] : 0.15) * 14 + 2;
        const wave = Math.sin((u * 7.5) + t + (v * 1.4)) * amp * (0.4 + 0.6 * v);
        const sag = slouch * 12 * Math.sin(u * Math.PI) * v;
        row.push([8 + u * (w - 16), 10 + v * (h - 22) + wave + sag]);
      }
      pts.push(row);
    }

    ctx.lineWidth = 1;
    for (let r = 0; r < rows; r++) {
      ctx.strokeStyle = r === 0 ? '#38bdf8' : `rgba(99, 102, 241, ${(0.25 + (r / rows) * 0.55).toFixed(2)})`;
      ctx.beginPath();
      for (let c = 0; c < cols; c++) {
        const [x, y] = pts[r][c];
        if (c === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      }
      ctx.stroke();
    }
    for (let c = 0; c < cols; c += 2) {
      ctx.strokeStyle = 'rgba(148, 163, 184, 0.22)';
      ctx.beginPath();
      for (let r = 0; r < rows; r++) {
        const [x, y] = pts[r][c];
        if (r === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      }
      ctx.stroke();
    }
    _raf = requestAnimationFrame(render);
  }

  function mount(canvas, energyHistory, postureScore) {
    _canvas = canvas;
    _energy = Array.isArray(energyHistory) ? energyHistory : [];
    _posture = Number.isFinite(postureScore) ? postureScore : 80;
    if (!_raf) _raf = requestAnimationFrame(render);
  }

  function release() {
    _canvas = null;
    if (_raf) { cancelAnimationFrame(_raf); _raf = 0; }
  }

  window.powatchCloth = { mount, release };
})();
