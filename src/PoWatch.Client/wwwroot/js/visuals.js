(() => {
    'use strict';
    const reduced = matchMedia('(prefers-reduced-motion: reduce)');
    const animations = new WeakMap();
    let quality = 1, slow = 0, healthy = 0, lastSample = 0;
    const visible = (el) => {
        if (document.hidden || !el?.isConnected) return false;
        const r = el.getBoundingClientRect();
        return r.width > 0 && r.height > 0 && r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth;
    };

    function budget(now, elapsed) {
        if (now - lastSample < 200) return quality;
        lastSample = now;
        const sensing = document.querySelector('[data-inference-ms]');
        const busy = window.powatchGpu?.captioning || Number(sensing?.dataset.inferenceMs) > 500;
        if (busy || elapsed > 24) { slow++; healthy = 0; }
        else { healthy++; slow = 0; }
        if (slow >= 3) { quality = Math.max(0.35, quality - 0.2); slow = 0; }
        if (healthy >= 30) { quality = Math.min(1, quality + 0.1); healthy = 0; }
        return quality;
    }

    function random(seed) {
        let value = 2166136261;
        for (const c of String(seed)) value = Math.imul(value ^ c.charCodeAt(0), 16777619);
        return () => {
            value += 0x6D2B79F5;
            let n = Math.imul(value ^ value >>> 15, 1 | value);
            n ^= n + Math.imul(n ^ n >>> 7, 61 | n);
            return ((n ^ n >>> 14) >>> 0) / 4294967296;
        };
    }

    function spring(value, velocity, target, dt) {
        // Analytic critically damped spring: stable even after a delayed browser frame.
        const omega = 18, offset = value - target, decay = Math.exp(-omega * dt);
        const impulse = velocity + omega * offset;
        return [target + (offset + impulse * dt) * decay, (velocity - omega * impulse * dt) * decay];
    }

    function animate(el, frames, options) {
        if (reduced.matches || !visible(el)) return;
        animations.get(el)?.cancel();
        const animation = el.animate(frames, options);
        animations.set(el, animation);
        return animation;
    }

    const metricValues = new WeakMap();
    const sparkPoints = new WeakMap();
    function update(root) {
        const metrics = root.matches?.('[data-metric]') ? [root] : root.querySelectorAll?.('[data-metric]') ?? [];
        for (const el of metrics) {
            const value = el.dataset.metric;
            if (metricValues.has(el) && metricValues.get(el) !== value) {
                animate(el, [{ backgroundColor: 'color-mix(in srgb, var(--term-accent) 24%, transparent)' }, { backgroundColor: 'transparent' }], { duration: 420 });
            }
            metricValues.set(el, value);
        }
        const lines = root.matches?.('.term-spark polyline') ? [root] : root.querySelectorAll?.('.term-spark polyline') ?? [];
        for (const line of lines) {
            const points = line.getAttribute('points');
            const before = sparkPoints.get(line);
            sparkPoints.set(line, points);
            if (!before || before === points || reduced.matches || !visible(line)) continue;
            const from = before.split(' ').map(p => p.split(',').map(Number));
            const to = points.split(' ').map(p => p.split(',').map(Number));
            if (from.length !== to.length) continue;
            const old = line.cloneNode();
            // SVG points are not interpolable in Web Animations; a short crossfade preserves true geometry.
            old.setAttribute('points', before);
            old.setAttribute('aria-hidden', 'true');
            line.parentNode.append(old);
            const fade = old.animate([{ opacity: 0.6 }, { opacity: 0 }], { duration: 180 });
            fade.onfinish = () => old.remove();
            animate(line, [{ opacity: 0.4 }, { opacity: 1 }], { duration: 180 });
        }
    }

    let observer;
    function mount() {
        update(document);
        observer = new MutationObserver(records => {
            const roots = new Set();
            for (const record of records) {
                if (record.type === 'attributes') roots.add(record.target);
                else for (const node of record.addedNodes) if (node.nodeType === 1 && !node.hasAttribute('aria-hidden')) roots.add(node);
            }
            roots.forEach(update);
        });
        observer.observe(document.getElementById('app'), { subtree: true, childList: true, attributes: true, attributeFilter: ['data-metric', 'points'] });
    }

    document.addEventListener('click', event => {
        const button = event.target.closest?.('button');
        if (!button || button.disabled) return;
        animate(button, [{ filter: 'brightness(1.3)' }, { filter: 'brightness(1)' }], { duration: 180 });
    });

    // Let Blazor own routing; wait for its actual DOM change before capturing the new view.
    document.addEventListener('click', event => {
        const link = event.target.closest?.('a[href]');
        if (!document.startViewTransition || reduced.matches || event.defaultPrevented || event.button !== 0 ||
            event.ctrlKey || event.metaKey || event.shiftKey || event.altKey || !link || link.target || link.hasAttribute('download')) return;
        const url = new URL(link.href);
        if (url.origin !== location.origin || url.hash || url.pathname.startsWith('/auth/') || url.pathname.startsWith('/api/') ||
            !window.Blazor || url.href === location.href) return;
        event.preventDefault();
        document.startViewTransition(() => new Promise(resolve => {
            const main = document.getElementById('main-content');
            if (!main) { Blazor.navigateTo(url.href); resolve(); return; }
            const changes = new MutationObserver(() => { changes.disconnect(); clearTimeout(timeout); resolve(); });
            const timeout = setTimeout(() => { changes.disconnect(); resolve(); }, 600);
            changes.observe(main, { childList: true, subtree: true });
            Blazor.navigateTo(url.href);
        }));
    }, true);

    window.powatchVisual = { visible, budget, random, spring, animate, reduced, get quality() { return quality; } };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount, { once: true });
    else mount();
})();
