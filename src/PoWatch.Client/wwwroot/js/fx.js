(() => {
    'use strict';
    const visual = window.powatchVisual;
    const colors = ['#8fdcff', '#5b9cff', '#f5a524'];
    let canvas, ctx, frame = 0, previous = 0, particles = [], serial = 0;

    function clear() {
        cancelAnimationFrame(frame);
        frame = 0;
        particles = [];
        ctx?.clearRect(0, 0, innerWidth, innerHeight);
    }

    function burst(pan = 0, seed, anchor) {
        if (visual.reduced.matches || document.hidden) return;
        const source = anchor ?? document.querySelector('.live-feed');
        if (source && !visual.visible(source)) return;
        if (!canvas) {
            canvas = document.createElement('canvas');
            canvas.className = 'fx-burst';
            canvas.setAttribute('aria-hidden', 'true');
            document.body.append(canvas);
            ctx = canvas.getContext('2d');
        }
        const dpr = Math.min(devicePixelRatio || 1, 2) * visual.quality;
        canvas.width = Math.round(innerWidth * dpr);
        canvas.height = Math.round(innerHeight * dpr);
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        const rect = source?.getBoundingClientRect() ?? { left: 0, top: innerHeight * 0.1, width: innerWidth, height: innerHeight * 0.5 };
        const x = rect.left + rect.width * (0.5 + Math.max(-1, Math.min(1, Number(pan) || 0)) * 0.4);
        const y = rect.top + rect.height * 0.45;
        const rand = visual.random(seed ?? `burst-${serial++}`);
        const count = Math.floor(28 * visual.quality);
        particles = particles.slice(-112);
        for (let i = 0; i < count; i++) {
            const angle = rand() * Math.PI * 2, speed = 45 + rand() * 110;
            particles.push({ x, y, vx: Math.cos(angle) * speed, vy: Math.sin(angle) * speed - 35,
                life: 0.7 + rand() * 0.4, color: colors[i % colors.length] });
        }
        if (!frame) { previous = 0; frame = requestAnimationFrame(tick); }
    }

    function tick(now) {
        frame = 0;
        if (document.hidden || visual.reduced.matches) { clear(); return; }
        const elapsed = previous ? now - previous : 16.7;
        visual.budget(now, elapsed);
        const dt = Math.min(elapsed / 1000, 0.04);
        previous = now;
        ctx.clearRect(0, 0, innerWidth, innerHeight);
        particles = particles.filter(p => (p.life -= dt) > 0);
        for (const p of particles) {
            p.vx *= Math.exp(-1.6 * dt);
            p.vy += (95 - p.vy * 0.8) * dt;
            p.x += p.vx * dt; p.y += p.vy * dt;
            if (p.x < 2 || p.x > innerWidth - 2) { p.vx *= -0.5; p.x = Math.max(2, Math.min(innerWidth - 2, p.x)); }
            if (p.y > innerHeight - 3) { p.y = innerHeight - 3; p.vy = -Math.abs(p.vy) * 0.45; }
            ctx.globalAlpha = Math.min(1, p.life * 2);
            ctx.fillStyle = p.color;
            ctx.beginPath(); ctx.arc(p.x, p.y, 1 + p.life * 1.5, 0, Math.PI * 2); ctx.fill();
        }
        ctx.globalAlpha = 1;
        if (particles.length) frame = requestAnimationFrame(tick);
    }

    document.addEventListener('visibilitychange', () => { if (document.hidden) clear(); });
    visual.reduced.addEventListener('change', clear);
    window.powatchFx = { burst, clear };
})();
