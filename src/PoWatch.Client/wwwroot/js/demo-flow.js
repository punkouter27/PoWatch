(() => {
    'use strict';
    const visual = window.powatchVisual;
    let active = null;
    const shader = `
struct Point { position: vec2f, velocity: vec2f }
struct Settings { time: f32, dt: f32, aspect: f32, quality: f32 }
@group(0) @binding(0) var<storage, read_write> points: array<Point>;
@group(0) @binding(1) var<uniform> settings: Settings;
@compute @workgroup_size(64)
fn advance(@builtin(global_invocation_id) id: vec3u) {
    let i = id.x;
    if (i >= arrayLength(&points)) { return; }
    var p = points[i];
    let q = p.position * 4.0;
    let t = settings.time * 0.18;
    // Curl of a scalar potential: decorative divergence-free flow, not sensed optical flow.
    let curl = vec2f(-sin(q.x + t) * sin(q.y - t), -cos(q.x + t) * cos(q.y - t));
    p.velocity += (curl * 0.35 - p.velocity * 1.8) * settings.dt;
    p.position += p.velocity * settings.dt;
    p.position = fract((p.position + vec2f(1.0)) * 0.5) * 2.0 - vec2f(1.0);
    points[i] = p;
}`;
    const renderShader = `
struct Point { position: vec2f, velocity: vec2f }
struct Settings { time: f32, dt: f32, aspect: f32, quality: f32 }
@group(0) @binding(0) var<storage, read> points: array<Point>;
@group(0) @binding(1) var<uniform> settings: Settings;
struct Output { @builtin(position) position: vec4f, @location(0) uv: vec2f }
@vertex fn background(@builtin(vertex_index) i: u32) -> Output {
    let positions = array<vec2f, 3>(vec2f(-1,-1), vec2f(3,-1), vec2f(-1,3));
    var o: Output;
    o.position = vec4f(positions[i], 0, 1);
    o.uv = positions[i];
    return o;
}
@fragment fn lighting(o: Output) -> @location(0) vec4f {
    let p = o.uv * vec2f(settings.aspect, 1);
    let t = settings.time * 0.16;
    let field = sin(p.x * 2.7 + sin(p.y * 3.0 + t)) * cos(p.y * 2.2 - t);
    let ribbon = pow(max(0.0, 1.0 - abs(field) * 2.4), 4.0);
    let light = exp(-dot(p - vec2f(sin(t), cos(t) * 0.4), p - vec2f(sin(t), cos(t) * 0.4)));
    let color = vec3f(0.015, 0.035, 0.065) + vec3f(0.04, 0.28, 0.42) * ribbon * light;
    return vec4f(color, 1);
}
@vertex fn particle(@builtin(vertex_index) v: u32, @builtin(instance_index) i: u32) -> Output {
    let quad = array<vec2f, 6>(vec2f(-1,-1),vec2f(1,-1),vec2f(-1,1),vec2f(-1,1),vec2f(1,-1),vec2f(1,1));
    var o: Output;
    let offset = quad[v] * vec2f(0.009 / settings.aspect, 0.009);
    o.position = vec4f(points[i].position + offset, 0, 1);
    o.uv = quad[v];
    return o;
}
@fragment fn glow(o: Output) -> @location(0) vec4f {
    let alpha = max(0.0, 1.0 - dot(o.uv, o.uv));
    return vec4f(0.25, 0.75, 1.0, alpha * alpha * 0.55);
}`;

    function release() {
        if (!active) return;
        const state = active;
        active = null;
        cancelAnimationFrame(state.raf);
        state.observer.disconnect();
        document.removeEventListener('visibilitychange', state.resume);
        visual.reduced.removeEventListener('change', state.resume);
        state.buffer?.destroy();
        state.uniform?.destroy();
        state.context?.unconfigure();
        state.device?.destroy();
        if (state.fallback) { state.fallback.remove(); state.canvas.hidden = false; }
        state.canvas.dataset.renderer = 'off';
    }

    function fallback(state) {
        if (state.fallback) return;
        const canvas = state.canvas.cloneNode();
        state.canvas.after(canvas);
        state.canvas.hidden = true;
        state.fallback = canvas;
        state.ctx = canvas.getContext('2d');
        state.canvas.dataset.renderer = 'canvas';
        const rand = visual.random('powatch-demo');
        state.points = Array.from({ length: 96 }, () => ({ x: rand() * 2 - 1, y: rand() * 2 - 1, vx: 0, vy: 0 }));
    }

    async function gpu(state) {
        if (!navigator.gpu || visual.reduced.matches) return false;
        const adapter = await navigator.gpu.requestAdapter({ powerPreference: 'low-power' });
        if (!adapter || active !== state) return false;
        const device = await adapter.requestDevice();
        if (active !== state) { device.destroy(); return false; }
        state.device = device;
        const module = device.createShaderModule({ code: shader });
        const renderModule = device.createShaderModule({ code: renderShader });
        const messages = await Promise.all([module.getCompilationInfo(), renderModule.getCompilationInfo()]);
        if (messages.some(info => info.messages.some(m => m.type === 'error'))) throw new Error('Demo flow shader compilation failed');
        if (active !== state) return false;
        const compute = await device.createComputePipelineAsync({ layout: 'auto', compute: { module, entryPoint: 'advance' } });
        const format = navigator.gpu.getPreferredCanvasFormat();
        const background = await device.createRenderPipelineAsync({
            layout: 'auto', vertex: { module: renderModule, entryPoint: 'background' },
            fragment: { module: renderModule, entryPoint: 'lighting', targets: [{ format }] }
        });
        const particles = await device.createRenderPipelineAsync({
            layout: 'auto', vertex: { module: renderModule, entryPoint: 'particle' },
            fragment: { module: renderModule, entryPoint: 'glow', targets: [{ format, blend: {
                color: { srcFactor: 'src-alpha', dstFactor: 'one', operation: 'add' },
                alpha: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha', operation: 'add' }
            } }] }
        });
        if (active !== state) return false;
        const initial = new Float32Array(512 * 4), rand = visual.random('powatch-demo');
        for (let i = 0; i < initial.length; i += 4) { initial[i] = rand() * 2 - 1; initial[i + 1] = rand() * 2 - 1; }
        state.buffer = device.createBuffer({ size: initial.byteLength, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
        device.queue.writeBuffer(state.buffer, 0, initial);
        state.uniform = device.createBuffer({ size: 16, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        const entries = [{ binding: 0, resource: { buffer: state.buffer } }, { binding: 1, resource: { buffer: state.uniform } }];
        const computeGroup = device.createBindGroup({ layout: compute.getBindGroupLayout(0), entries });
        const particleGroup = device.createBindGroup({ layout: particles.getBindGroupLayout(0), entries });
        const backgroundGroup = device.createBindGroup({ layout: background.getBindGroupLayout(0), entries: [entries[1]] });
        state.context = state.canvas.getContext('webgpu');
        if (!state.context) throw new Error('WebGPU canvas unavailable');
        state.context.configure({ device, format, alphaMode: 'opaque' });
        state.gpu = { compute, background, particles, computeGroup, particleGroup, backgroundGroup };
        state.canvas.dataset.renderer = 'webgpu';
        device.lost.then(info => {
            if (active !== state) return;
            console.warn(`Demo GPU lost: ${info.message}; using Canvas flow.`);
            state.gpu = null;
            fallback(state);
            state.resume();
        });
        return true;
    }

    function draw(state, now, dt, quality) {
        const canvas = state.fallback ?? state.canvas;
        const dpr = Math.min(devicePixelRatio || 1, 1.5) * quality;
        const width = Math.max(1, Math.round(canvas.clientWidth * dpr));
        const height = Math.max(1, Math.round(canvas.clientHeight * dpr));
        if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height; }
        state.time += dt;
        if (state.gpu) {
            const { device, gpu, context } = state;
            device.queue.writeBuffer(state.uniform, 0, new Float32Array([state.time, dt, width / height, quality]));
            const commands = device.createCommandEncoder();
            const compute = commands.beginComputePass();
            compute.setPipeline(gpu.compute);
            compute.setBindGroup(0, gpu.computeGroup);
            compute.dispatchWorkgroups(8);
            compute.end();
            const pass = commands.beginRenderPass({ colorAttachments: [{ view: context.getCurrentTexture().createView(), clearValue: [0, 0, 0, 1], loadOp: 'clear', storeOp: 'store' }] });
            pass.setPipeline(gpu.background);
            pass.setBindGroup(0, gpu.backgroundGroup);
            pass.draw(3);
            pass.setPipeline(gpu.particles);
            pass.setBindGroup(0, gpu.particleGroup);
            pass.draw(6, Math.floor(512 * quality));
            pass.end();
            device.queue.submit([commands.finish()]);
            return;
        }
        const ctx = state.ctx;
        ctx.fillStyle = '#050d18';
        ctx.fillRect(0, 0, width, height);
        const light = ctx.createRadialGradient(width * 0.5, height * 0.5, 0, width * 0.5, height * 0.5, width * 0.6);
        light.addColorStop(0, '#12334b'); light.addColorStop(1, '#050d18');
        ctx.fillStyle = light; ctx.fillRect(0, 0, width, height);
        ctx.fillStyle = '#8fdcff';
        for (const p of state.points.slice(0, Math.floor(96 * quality))) {
            const t = state.time * 0.18;
            p.vx += (-Math.sin(p.x * 4 + t) * Math.sin(p.y * 4 - t) * 0.35 - p.vx * 1.8) * dt;
            p.vy += (-Math.cos(p.x * 4 + t) * Math.cos(p.y * 4 - t) * 0.35 - p.vy * 1.8) * dt;
            p.x = ((p.x + p.vx * dt + 3) % 2) - 1; p.y = ((p.y + p.vy * dt + 3) % 2) - 1;
            ctx.globalAlpha = 0.65;
            ctx.beginPath(); ctx.arc((p.x + 1) * width / 2, (p.y + 1) * height / 2, 1.5, 0, Math.PI * 2); ctx.fill();
        }
        ctx.globalAlpha = 1;
    }

    async function mount(canvas) {
        release();
        if (!canvas) return;
        const state = { canvas, raf: 0, previous: 0, time: 0, ready: false };
        active = state;
        state.resume = () => {
            if (active !== state || !state.ready || state.raf) return;
            state.previous = 0;
            if (visual.visible(state.fallback ?? canvas)) state.raf = requestAnimationFrame(tick);
        };
        const tick = now => {
            state.raf = 0;
            if (active !== state || !visual.visible(state.fallback ?? canvas)) return;
            const elapsed = state.previous ? now - state.previous : 16.7;
            const quality = visual.budget(now, elapsed);
            draw(state, now, visual.reduced.matches ? 0 : Math.min(elapsed / 1000, 0.05), quality);
            state.previous = now;
            if (!visual.reduced.matches) state.raf = requestAnimationFrame(tick);
        };
        state.observer = new IntersectionObserver(state.resume);
        state.observer.observe(canvas.parentElement);
        document.addEventListener('visibilitychange', state.resume);
        visual.reduced.addEventListener('change', state.resume);
        try {
            if (!await gpu(state) && active === state) fallback(state);
        } catch (error) {
            if (active !== state) return;
            console.warn('Demo GPU unavailable; using Canvas flow.', error);
            if (active === state) fallback(state);
        }
        if (active !== state) return;
        state.ready = true;
        state.resume();
    }

    window.powatchDemo = { mount, release };
})();
