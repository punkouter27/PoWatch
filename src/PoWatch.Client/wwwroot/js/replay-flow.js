// 60 fps flow-agent crowd overlay (#17) for Journal TimelineReplay over the 16×9 room grid.
(() => {
  'use strict';
  const AGENTS = 90;
  let _canvas = null, _raf = 0, _grid = new Float32Array(144);
  const agents = Array.from({ length: AGENTS }, () => ({
    x: Math.random(), y: Math.random(), vx: 0, vy: 0, life: Math.random()
  }));

  function sample(x, y) {
    const gx = Math.max(0, Math.min(15, Math.floor(x * 16)));
    const gy = Math.max(0, Math.min(8, Math.floor(y * 9)));
    return _grid[(gy * 16) + gx] || 0;
  }

  function frame() {
    if (!_canvas) { _raf = 0; return; }
    const w = Math.max(160, _canvas.clientWidth || 320);
    const h = Math.max(90, _canvas.clientHeight || 180);
    if (_canvas.width !== w || _canvas.height !== h) { _canvas.width = w; _canvas.height = h; }
    const ctx = _canvas.getContext('2d');
    ctx.fillStyle = 'rgba(5, 7, 10, 0.22)';
    ctx.fillRect(0, 0, w, h);

    for (const a of agents) {
      const gx = sample(a.x + 0.04, a.y) - sample(a.x - 0.04, a.y);
      const gy = sample(a.x, a.y + 0.06) - sample(a.x, a.y - 0.06);
      a.vx = (a.vx * 0.88) + (gx * 0.04) + ((Math.random() - 0.5) * 0.002);
      a.vy = (a.vy * 0.88) + (gy * 0.04) + ((Math.random() - 0.5) * 0.002);
      a.x += a.vx; a.y += a.vy;
      a.life -= 0.008;
      if (a.life <= 0 || a.x < 0 || a.x > 1 || a.y < 0 || a.y > 1) {
        a.x = Math.random(); a.y = Math.random(); a.vx = 0; a.vy = 0; a.life = 0.6 + Math.random() * 0.4;
      }
      const heat = sample(a.x, a.y);
      ctx.fillStyle = heat > 0.2 ? '#38bdf8' : 'rgba(99, 102, 241, 0.6)';
      ctx.beginPath();
      ctx.arc(a.x * w, a.y * h, 1.3 + heat * 2.5, 0, Math.PI * 2);
      ctx.fill();
    }
    _raf = requestAnimationFrame(frame);
  }

  function mount(canvas) {
    _canvas = canvas;
    if (!_raf) _raf = requestAnimationFrame(frame);
  }

  function update(grid144) {
    if (!grid144 || grid144.length !== 144) return;
    for (let i = 0; i < 144; i++) _grid[i] = Number(grid144[i]) || 0;
  }

  function release() {
    _canvas = null;
    if (_raf) { cancelAnimationFrame(_raf); _raf = 0; }
  }

  window.powatchReplayFlow = { mount, update, release };
})();
