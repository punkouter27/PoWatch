// A tiny burst of light when something worth noticing happens (a known regular walks in). One canvas
// overlay, created on first use; the animation loop stops the moment the last particle dies, so an idle
// page runs nothing. Skipped entirely under reduced motion.
(function () {
  'use strict';
  const COLORS = ['#4fd1ff', '#5b9cff', '#f5a524'];
  const STILL = () => window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;

  let canvas = null;
  let ctx = null;
  let frame = 0;
  let particles = [];

  function ensure() {
    if (canvas) return;
    canvas = document.createElement('canvas');
    canvas.className = 'fx-burst';
    canvas.setAttribute('aria-hidden', 'true');
    document.body.appendChild(canvas);
    ctx = canvas.getContext('2d');
  }

  function resize() {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    canvas.width = window.innerWidth * dpr;
    canvas.height = window.innerHeight * dpr;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }

  function spawn(pan) {
    const x = window.innerWidth * (0.5 + Math.max(-1, Math.min(1, Number(pan) || 0)) * 0.35);
    const y = window.innerHeight * 0.32;
    for (let i = 0; i < 18; i++) {
      const angle = Math.random() * Math.PI * 2;
      const speed = 1.4 + Math.random() * 3.4;
      particles.push({
        x, y,
        vx: Math.cos(angle) * speed,
        vy: Math.sin(angle) * speed - 1,
        life: 1,
        color: COLORS[i % COLORS.length],
      });
    }
    if (!frame) frame = requestAnimationFrame(tick);
  }

  function tick() {
    ctx.clearRect(0, 0, window.innerWidth, window.innerHeight);
    particles = particles.filter((p) => (p.life -= 0.02) > 0);
    for (const p of particles) {
      p.x += p.vx;
      p.y += p.vy;
      p.vy += 0.06;
      ctx.globalAlpha = Math.max(0, p.life);
      ctx.fillStyle = p.color;
      ctx.beginPath();
      ctx.arc(p.x, p.y, 2.2 * p.life + 0.6, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.globalAlpha = 1;
    if (particles.length) {
      frame = requestAnimationFrame(tick);
    } else {
      frame = 0;
      ctx.clearRect(0, 0, window.innerWidth, window.innerHeight);
    }
  }

  window.addEventListener('resize', () => { if (canvas) resize(); });

  window.powatchFx = {
    burst(pan) {
      if (STILL()) return;
      ensure();
      resize();
      spawn(pan);
    },
  };
})();
