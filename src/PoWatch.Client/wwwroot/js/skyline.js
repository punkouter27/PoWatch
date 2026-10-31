// History's year as a 3D skyline: one tower per day on the same 53-week × 7-day grid as the 2D
// calendar, height = occupancy, rainy days tinted blue. Drag to turn, scroll (or + −) to zoom, hover
// for the day, click a tower to open it. "Fly over" glides along the year and plays each week as a
// note, higher when it was busier. Plain WebGL2 with instanced boxes: one draw call for the year.
// It draws only when something changes (no idle loop), because the detector and the caption model
// share the GPU. Under prefers-reduced-motion the towers do not grow and the fly-over does not move
// the camera (a highlight walks the weeks instead).
(function () {
    'use strict';

    const COLS = 53, ROWS = 7;
    const MAX_HEIGHT = 6;
    const TOWER = 0.82;
    const reduced = matchMedia('(prefers-reduced-motion: reduce)');
    const states = new WeakMap();

    const VERTEX = `#version 300 es
in vec3 a_pos;      // unit box: x, z in [-0.5, 0.5], y in [0, 1]
in vec3 a_normal;
in vec4 a_box;      // centre x, centre z, height, cell index
in vec3 a_color;
in vec2 a_extent;   // footprint width (x) and depth (z)
uniform mat4 u_viewProj;
uniform float u_grow;
uniform float u_selected;
uniform float u_hover;
out vec3 v_normal;
out vec3 v_color;
out vec3 v_world;
out float v_mark;
out float v_top;
void main() {
  float h = a_box.w < 0.0 ? a_box.z : max(a_box.z * u_grow, 0.04);
  vec3 world = vec3(a_box.x + a_pos.x * a_extent.x, a_pos.y * h - (a_box.w < 0.0 ? h : 0.0), a_box.y + a_pos.z * a_extent.y);
  v_normal = a_normal;
  v_color = a_color;
  v_world = world;
  v_top = a_pos.y;
  // The floor (index -1) is never marked, even when nothing is selected or hovered (also -1).
  v_mark = a_box.w < 0.0 ? 0.0 : a_box.w == u_selected ? 1.0 : (a_box.w == u_hover ? 0.5 : 0.0);
  gl_Position = u_viewProj * vec4(world, 1.0);
}`;

    const FRAGMENT = `#version 300 es
precision highp float;
uniform vec3 u_eye;
uniform vec3 u_bg;
uniform vec3 u_mark;
in vec3 v_normal;
in vec3 v_color;
in vec3 v_world;
in float v_mark;
in float v_top;
out vec4 o_color;
void main() {
  vec3 light = normalize(vec3(-0.45, 1.0, -0.35));
  float diffuse = max(dot(normalize(v_normal), light), 0.0);
  vec3 color = v_color * (0.38 + 0.62 * diffuse);
  // A lit cap and a darker foot give each tower its shape.
  color *= mix(0.72, 1.0, v_top);
  color = mix(color, u_mark, v_mark * 0.65);
  color += u_mark * v_mark * 0.25 * step(0.999, v_top);
  float fog = smoothstep(28.0, 95.0, distance(v_world, u_eye));
  o_color = vec4(mix(color, u_bg, fog), 1.0);
}`;

    // ── Small matrix kit (column-major, as WebGL expects) ────────────────────────────────────────
    function perspective(fov, aspect, near, far) {
        const f = 1 / Math.tan(fov / 2), nf = 1 / (near - far);
        return new Float32Array([f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) * nf, -1, 0, 0, 2 * far * near * nf, 0]);
    }
    const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
    const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    const norm = (a) => { const l = Math.hypot(a[0], a[1], a[2]) || 1; return [a[0] / l, a[1] / l, a[2] / l]; };
    function lookAt(eye, target) {
        const z = norm(sub(eye, target)), x = norm(cross([0, 1, 0], z)), y = cross(z, x);
        return new Float32Array([x[0], y[0], z[0], 0, x[1], y[1], z[1], 0, x[2], y[2], z[2], 0, -dot(x, eye), -dot(y, eye), -dot(z, eye), 1]);
    }
    function multiply(a, b) {
        const out = new Float32Array(16);
        for (let c = 0; c < 4; c++)
            for (let r = 0; r < 4; r++)
                out[c * 4 + r] = a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1] + a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3];
        return out;
    }

    /** A unit box with flat normals: x, z in [-0.5, 0.5], y in [0, 1]. */
    function box() {
        const faces = [
            [[0, 1, 0], [[-1, 1, -1], [1, 1, -1], [1, 1, 1], [-1, 1, 1]]],
            [[0, 0, 1], [[-1, 0, 1], [1, 0, 1], [1, 1, 1], [-1, 1, 1]]],
            [[0, 0, -1], [[1, 0, -1], [-1, 0, -1], [-1, 1, -1], [1, 1, -1]]],
            [[1, 0, 0], [[1, 0, 1], [1, 0, -1], [1, 1, -1], [1, 1, 1]]],
            [[-1, 0, 0], [[-1, 0, -1], [-1, 0, 1], [-1, 1, 1], [-1, 1, -1]]],
        ];
        const data = [];
        for (const [n, q] of faces) {
            for (const i of [0, 1, 2, 0, 2, 3]) {
                const [x, y, z] = q[i];
                data.push(x * 0.5, y, z * 0.5, ...n);
            }
        }
        return new Float32Array(data);
    }

    function hex(el, variable, fallback) {
        const value = (getComputedStyle(el).getPropertyValue(variable).trim() || fallback).replace('#', '');
        const n = parseInt(value.length === 3 ? value.split('').map((c) => c + c).join('') : value.slice(0, 6), 16);
        return [(n >> 16 & 255) / 255, (n >> 8 & 255) / 255, (n & 255) / 255];
    }

    function create(canvas) {
        const gl = canvas.getContext('webgl2', { antialias: true, alpha: false });
        if (!gl) return null;
        const compile = (type, source) => {
            const shader = gl.createShader(type);
            gl.shaderSource(shader, source);
            gl.compileShader(shader);
            if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader));
            return shader;
        };
        const program = gl.createProgram();
        gl.attachShader(program, compile(gl.VERTEX_SHADER, VERTEX));
        gl.attachShader(program, compile(gl.FRAGMENT_SHADER, FRAGMENT));
        gl.linkProgram(program);
        if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program));

        const vao = gl.createVertexArray();
        gl.bindVertexArray(vao);
        const attr = (name) => gl.getAttribLocation(program, name);

        gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
        gl.bufferData(gl.ARRAY_BUFFER, box(), gl.STATIC_DRAW);
        gl.enableVertexAttribArray(attr('a_pos'));
        gl.vertexAttribPointer(attr('a_pos'), 3, gl.FLOAT, false, 24, 0);
        gl.enableVertexAttribArray(attr('a_normal'));
        gl.vertexAttribPointer(attr('a_normal'), 3, gl.FLOAT, false, 24, 12);

        const instances = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, instances);
        const stride = 9 * 4; // box (4) + colour (3) + extent (2)
        for (const [name, size, offset] of [['a_box', 4, 0], ['a_color', 3, 16], ['a_extent', 2, 28]]) {
            gl.enableVertexAttribArray(attr(name));
            gl.vertexAttribPointer(attr(name), size, gl.FLOAT, false, stride, offset);
            gl.vertexAttribDivisor(attr(name), 1);
        }

        const u = (name) => gl.getUniformLocation(program, name);
        return {
            gl, program, vao, instances, count: 0,
            u: { viewProj: u('u_viewProj'), grow: u('u_grow'), selected: u('u_selected'), hover: u('u_hover'), eye: u('u_eye'), bg: u('u_bg'), mark: u('u_mark') },
        };
    }

    /** Where a cell's tower stands: weeks run along x, weekdays along z. */
    const place = (i) => [(i % COLS) - (COLS - 1) / 2, Math.floor(i / COLS) - (ROWS - 1) / 2];

    function mount(canvas, values, wet, labels, selected, dotnet, tip) {
        teardown(canvas);
        const view = create(canvas);
        if (!view) return false;

        const amber = hex(canvas, '--term-accent', '#8fdcff'), cyan = hex(canvas, '--term-cyan', '#5b9cff');
        const line = hex(canvas, '--term-line', '#262d38'), bg = hex(canvas, '--term-bg', '#0d1014');
        const towers = [];
        const data = [];
        for (let i = 0; i < values.length; i++) {
            const v = values[i];
            if (v < 0) continue; // a day still to come
            const [x, z] = place(i);
            const height = v > 0 ? 0.25 + v * MAX_HEIGHT : 0.06;
            const warm = amber.map((c, k) => c * (0.45 + 0.55 * Math.min(1, v * 1.4)) + (1 - (0.45 + 0.55 * Math.min(1, v * 1.4))) * line[k] * 0.6);
            const color = v <= 0 ? line : wet[i] ? warm.map((c, k) => c * 0.35 + cyan[k] * 0.65) : warm;
            towers.push({ i, x, z, height });
            data.push(x, z, height, i, ...color, TOWER, TOWER);
        }
        // The floor: one flat box under the year, slightly larger than it.
        data.push(0, 0, 0.05, -1, ...bg.map((c, k) => c * 0.6 + line[k] * 0.4), COLS + 2, ROWS + 2);
        view.count = data.length / 9;
        view.gl.bindBuffer(view.gl.ARRAY_BUFFER, view.instances);
        view.gl.bufferData(view.gl.ARRAY_BUFFER, new Float32Array(data), view.gl.STATIC_DRAW);

        const state = {
            view, towers, labels, tip, dotnet, bg, mark: cyan,
            selected, hover: -1, grow: reduced.matches ? 1 : 0,
            // Nearly head-on and well above: 53 weeks by 7 days is a long strip, and a low camera
            // flattens the seven rows into a sliver. From here the year fills the width, tall towers included.
            yaw: -0.05, pitch: 1.0, dist: 37, target: [0, 1.2, 0],
            raf: 0, flying: null, detach: null, observer: null,
        };
        states.set(canvas, state);

        const camera = () => {
            const eye = [
                state.target[0] + state.dist * Math.cos(state.pitch) * Math.sin(state.yaw),
                state.target[1] + state.dist * Math.sin(state.pitch),
                state.target[2] + state.dist * Math.cos(state.pitch) * Math.cos(state.yaw),
            ];
            return { eye, forward: norm(sub(state.target, eye)) };
        };

        function render() {
            state.raf = 0;
            const { gl, u } = view;
            const dpr = Math.min(devicePixelRatio || 1, 2);
            const width = Math.max(1, Math.round(canvas.clientWidth * dpr)), height = Math.max(1, Math.round(canvas.clientHeight * dpr));
            if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height; }
            const { eye } = camera();
            gl.viewport(0, 0, width, height);
            gl.clearColor(bg[0], bg[1], bg[2], 1);
            gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
            gl.enable(gl.DEPTH_TEST);
            gl.useProgram(view.program);
            gl.bindVertexArray(view.vao);
            gl.uniformMatrix4fv(u.viewProj, false, multiply(perspective(0.7, width / height, 0.5, 300), lookAt(eye, state.target)));
            gl.uniform1f(u.grow, state.grow);
            gl.uniform1f(u.selected, state.selected);
            gl.uniform1f(u.hover, state.hover);
            gl.uniform3fv(u.eye, eye);
            gl.uniform3fv(u.bg, bg);
            gl.uniform3fv(u.mark, state.mark);
            gl.drawArraysInstanced(gl.TRIANGLES, 0, 30, view.count);
        }
        state.render = () => { if (!state.raf) state.raf = requestAnimationFrame(render); };

        /** The tower under a point of the canvas, by casting a ray through it; -1 for none. */
        function pick(clientX, clientY) {
            const r = canvas.getBoundingClientRect();
            const nx = ((clientX - r.left) / r.width) * 2 - 1, ny = 1 - ((clientY - r.top) / r.height) * 2;
            const { eye, forward } = camera();
            const right = norm(cross(forward, [0, 1, 0])), up = cross(right, forward);
            const t = Math.tan(0.35), aspect = r.width / r.height;
            const dir = norm([0, 1, 2].map((k) => forward[k] + nx * t * aspect * right[k] + ny * t * up[k]));
            let best = -1, nearest = Infinity;
            for (const tower of towers) {
                const lo = [tower.x - TOWER / 2, 0, tower.z - TOWER / 2], hi = [tower.x + TOWER / 2, Math.max(tower.height * state.grow, 0.04), tower.z + TOWER / 2];
                let tmin = -Infinity, tmax = Infinity;
                for (let k = 0; k < 3; k++) {
                    const a = (lo[k] - eye[k]) / dir[k], b = (hi[k] - eye[k]) / dir[k];
                    tmin = Math.max(tmin, Math.min(a, b));
                    tmax = Math.min(tmax, Math.max(a, b));
                }
                if (tmax >= Math.max(tmin, 0) && tmin < nearest) { nearest = tmin; best = tower.i; }
            }
            return best;
        }

        function showTip(index, clientX, clientY) {
            if (!tip) return;
            if (index < 0 || !labels[index]) { tip.hidden = true; return; }
            const r = canvas.getBoundingClientRect();
            tip.textContent = labels[index];
            tip.hidden = false;
            tip.style.left = `${Math.min(clientX - r.left + 12, r.width - tip.offsetWidth - 4)}px`;
            tip.style.top = `${Math.max(clientY - r.top - 34, 4)}px`;
        }

        let drag = null;
        const onDown = (e) => { drag = { x: e.clientX, y: e.clientY, moved: 0 }; canvas.setPointerCapture(e.pointerId); };
        const onMove = (e) => {
            if (drag) {
                const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
                drag.moved += Math.abs(dx) + Math.abs(dy);
                drag.x = e.clientX; drag.y = e.clientY;
                state.yaw -= dx * 0.008;
                state.pitch = Math.min(1.35, Math.max(0.12, state.pitch + dy * 0.006));
                if (tip) tip.hidden = true;
                state.render();
                return;
            }
            const index = pick(e.clientX, e.clientY);
            if (index !== state.hover) { state.hover = index; state.render(); }
            showTip(index, e.clientX, e.clientY);
        };
        const onUp = (e) => {
            const wasClick = drag && drag.moved < 5;
            drag = null;
            if (!wasClick) return;
            const index = pick(e.clientX, e.clientY);
            if (index >= 0) state.dotnet?.invokeMethodAsync('Pick', index).catch(() => { });
        };
        const onLeave = () => { if (state.hover !== -1) { state.hover = -1; state.render(); } if (tip) tip.hidden = true; };
        const onWheel = (e) => { e.preventDefault(); zoom(Math.exp(e.deltaY * 0.0012)); };
        const zoom = (factor) => { state.dist = Math.min(110, Math.max(14, state.dist * factor)); state.render(); };
        // Keys stay here: History's own ← → step the day everywhere else on the page.
        const onKey = (e) => {
            const turns = { ArrowLeft: 0.12, ArrowRight: -0.12 }, tilts = { ArrowUp: 0.08, ArrowDown: -0.08 };
            if (e.key in turns) state.yaw += turns[e.key];
            else if (e.key in tilts) state.pitch = Math.min(1.35, Math.max(0.12, state.pitch + tilts[e.key]));
            else if (e.key === '+' || e.key === '=') zoom(0.88);
            else if (e.key === '-') zoom(1.14);
            else return;
            e.preventDefault();
            e.stopPropagation();
            state.render();
        };
        canvas.addEventListener('pointerdown', onDown);
        canvas.addEventListener('pointermove', onMove);
        canvas.addEventListener('pointerup', onUp);
        canvas.addEventListener('pointerleave', onLeave);
        canvas.addEventListener('wheel', onWheel, { passive: false });
        canvas.addEventListener('keydown', onKey);
        state.detach = () => {
            canvas.removeEventListener('pointerdown', onDown);
            canvas.removeEventListener('pointermove', onMove);
            canvas.removeEventListener('pointerup', onUp);
            canvas.removeEventListener('pointerleave', onLeave);
            canvas.removeEventListener('wheel', onWheel);
            canvas.removeEventListener('keydown', onKey);
        };
        state.observer = new ResizeObserver(() => state.render());
        state.observer.observe(canvas);

        // The towers rise out of the floor, tallest last to finish.
        if (state.grow < 1) {
            const started = performance.now();
            const rise = (now) => {
                const t = Math.min(1, (now - started) / 900);
                state.grow = 1 - Math.pow(1 - t, 3);
                render();
                if (t < 1 && canvas.isConnected) requestAnimationFrame(rise);
            };
            requestAnimationFrame(rise);
        } else {
            state.render();
        }
        return true;
    }

    function select(canvas, index) {
        const state = states.get(canvas);
        if (!state) return;
        state.selected = index;
        state.render();
    }

    /**
     * Glides along the year, week by week, playing each week as a note: a minor pentatonic over two
     * octaves, higher when the week was busier, panned from left to right as the year goes by.
     * Resolves when it lands back where it started; calling it again while flying stops it.
     */
    function flyover(canvas, weekMeans) {
        const state = states.get(canvas);
        if (!state) return Promise.resolve(false);
        if (state.flying) { state.flying.stop(); return Promise.resolve(false); }

        const scale = [0, 3, 5, 7, 10, 12, 15, 17, 19, 22, 24];
        const note = (mean) => 220 * Math.pow(2, scale[Math.min(scale.length - 1, Math.round(mean * (scale.length - 1)))] / 12);
        const home = { yaw: state.yaw, pitch: state.pitch, dist: state.dist, target: [...state.target] };
        const moving = !reduced.matches;
        const length = 9000;
        const started = performance.now();
        let lastWeek = -1, stopped = false;

        return new Promise((resolve) => {
            const end = () => {
                Object.assign(state, { yaw: home.yaw, pitch: home.pitch, dist: home.dist, target: home.target, hover: -1, flying: null });
                state.render();
                resolve(true);
            };
            state.flying = { stop: () => { stopped = true; } };
            const step = (now) => {
                const t = (now - started) / length;
                if (stopped || t >= 1 || !canvas.isConnected) { end(); return; }
                const week = Math.min(COLS - 1, Math.floor(t * COLS));
                if (moving) {
                    // Low over the towers, looking along the year as it passes.
                    state.target = [(t * COLS) - (COLS - 1) / 2, 1.5, 0];
                    state.yaw = -1.05;
                    state.pitch = 0.32;
                    state.dist = 15;
                }
                if (week !== lastWeek) {
                    lastWeek = week;
                    // The busiest day of the week lights up as its note plays.
                    let lit = -1, best = -1;
                    for (const tower of state.towers) if (tower.i % COLS === week && tower.height > best) { best = tower.height; lit = tower.i; }
                    state.hover = lit;
                    const mean = weekMeans[week];
                    if (mean > 0) window.powatchCues?.tone(note(mean), 0.32, (week / (COLS - 1)) * 1.4 - 0.7);
                }
                state.render();
                requestAnimationFrame(step);
            };
            requestAnimationFrame(step);
        });
    }

    function teardown(canvas) {
        const state = canvas && states.get(canvas);
        if (!state) return null;
        state.flying?.stop();
        state.detach?.();
        state.observer?.disconnect();
        cancelAnimationFrame(state.raf);
        states.delete(canvas);
        return state;
    }

    /** The component is going away: also give its WebGL context back now, not at GC (Chrome caps live contexts at ~16). */
    function unmount(canvas) {
        teardown(canvas)?.view.gl.getExtension('WEBGL_lose_context')?.loseContext();
    }

    window.powatchSkyline = { mount, select, flyover, unmount };
})();
