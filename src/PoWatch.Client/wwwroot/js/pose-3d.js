// Real-time 3D ergonomic pose mannequin rendered from MediaPipe's 33 metre-scale world landmarks.
(() => {
  'use strict';
  const BONES = [
    [11, 12], [11, 13], [13, 15], [12, 14], [14, 16],
    [11, 23], [12, 24], [23, 24], [23, 25], [25, 27], [24, 26], [26, 28],
    [0, 11], [0, 12],
  ];
  let _canvas = null;
  let _angle = 0.28;

  function mount(canvas) {
    _canvas = canvas;
  }

  function release() {
    _canvas = null;
  }

  function update(worldPoints) {
    if (!_canvas || !worldPoints || worldPoints.length < 29) return;
    const w = Math.max(120, _canvas.clientWidth || 180);
    const h = Math.max(90, _canvas.clientHeight || 110);
    if (_canvas.width !== w || _canvas.height !== h) { _canvas.width = w; _canvas.height = h; }
    const ctx = _canvas.getContext('2d');
    ctx.clearRect(0, 0, w, h);

    // Subtle slow orbit to show 3D depth.
    _angle = Math.sin(performance.now() / 3200) * 0.42;
    const cos = Math.cos(_angle), sin = Math.sin(_angle);
    const scale = h * 0.62;
    const project = (pt) => {
      const rx = (pt[0] * cos) - (pt[2] * sin);
      const rz = (pt[0] * sin) + (pt[2] * cos) + 2.2;
      const persp = 2.2 / Math.max(0.6, rz);
      return [w * 0.5 + rx * scale * persp, h * 0.52 + pt[1] * scale * persp, rz];
    };

    // Floor grid ellipse for 3D grounding.
    ctx.strokeStyle = 'rgba(148, 163, 184, 0.22)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.ellipse(w * 0.5, h * 0.88, w * 0.28, h * 0.08, 0, 0, Math.PI * 2);
    ctx.stroke();

    // Bones with depth shading.
    ctx.lineCap = 'round';
    for (const [a, b] of BONES) {
      const pa = project(worldPoints[a]);
      const pb = project(worldPoints[b]);
      const depth = Math.max(0.35, Math.min(1, 2.6 - ((pa[2] + pb[2]) * 0.5)));
      ctx.strokeStyle = a === 0 ? `rgba(56, 189, 248, ${depth.toFixed(2)})` : `rgba(99, 102, 241, ${depth.toFixed(2)})`;
      ctx.lineWidth = 2.4 * depth;
      ctx.beginPath();
      ctx.moveTo(pa[0], pa[1]);
      ctx.lineTo(pb[0], pb[1]);
      ctx.stroke();
    }

    // Head sphere.
    const head = project(worldPoints[0]);
    ctx.fillStyle = '#38bdf8';
    ctx.beginPath();
    ctx.arc(head[0], head[1], 5, 0, Math.PI * 2);
    ctx.fill();
  }

  window.powatchPose3d = { mount, update, release };
})();
