// The camera panel switches on and off like a tube set. On Start: black, a dot stretches into a bright
// line, the line opens into the picture with a colour-fringed glow at its edge and a white flash, and
// the scanlines settle. On Stop the same in reverse. A transparent WebGL2 canvas over the panel masks
// whatever is under it (camera or demo scene), so nothing re-renders the video. The shape of each
// frame is computed here; the shader only draws it. Under prefers-reduced-motion, or without WebGL2,
// nothing plays and the panel simply cuts, as it did before.
(function () {
    'use strict';

    const VERTEX = `#version 300 es
in vec2 a_pos;
out vec2 v_uv;
void main() {
  v_uv = vec2(a_pos.x * 0.5 + 0.5, 0.5 - a_pos.y * 0.5);
  gl_Position = vec4(a_pos, 0.0, 1.0);
}`;

    // u_open: half the opening's width and height, in panel units (0.5 is fully open).
    const FRAGMENT = `#version 300 es
precision highp float;
uniform vec2 u_size;
uniform vec2 u_open;
uniform float u_flash;
uniform float u_scan;
uniform float u_glow;
in vec2 v_uv;
out vec4 o_color;

// Signed distance in pixels from the opening's edge: negative inside, positive outside.
float edge(vec2 c, vec2 open) {
  vec2 d = (abs(c) - open) * u_size;
  return max(d.x, d.y);
}

// Brightest on the edge itself, fading both ways: a thin opening is all edge (the bright line),
// a wide one only glows at its rim.
float halo(float d) { return 0.85 * exp(-abs(d) / 6.0) + 0.6 * exp(-abs(d) / 1.5); }

void main() {
  vec2 c = v_uv - 0.5;
  float d = edge(c, u_open);
  float inside = 1.0 - step(0.0, d);
  // Red runs a little wide and blue a little narrow: the fringe of a tube's convergence.
  vec2 shift = vec2(2.0) / u_size;
  vec3 glow = u_glow * vec3(halo(edge(c, u_open + shift)), halo(d), halo(edge(c, u_open - shift)));
  glow *= vec3(1.0, 0.94, 1.08);

  float rows = 0.5 + 0.5 * sin(v_uv.y * u_size.y * 3.14159);
  float dark = inside * u_scan * rows;
  float flash = inside * u_flash;
  float alpha = clamp((1.0 - inside) + dark + flash + max(glow.r, max(glow.g, glow.b)), 0.0, 1.0);
  // Premultiplied: black mask and dark scanlines add nothing to colour, only to coverage.
  o_color = vec4(clamp(glow + vec3(flash), 0.0, 1.0), alpha);
}`;

    const ON_MS = 700;
    const OFF_MS = 480;
    const reduced = matchMedia('(prefers-reduced-motion: reduce)');
    const states = new WeakMap();

    const lerp = (a, b, t) => a + (b - a) * Math.min(1, Math.max(0, t));
    const easeOut = (t) => 1 - Math.pow(1 - Math.min(1, Math.max(0, t)), 3);
    const easeIn = (t) => Math.pow(Math.min(1, Math.max(0, t)), 2);

    /** The opening, flash, scanlines and glow at progress t (0..1) of switching on or off. */
    function shape(on, t) {
        if (on) {
            const stretch = easeOut(t / 0.15), open = easeOut((t - 0.15) / 0.45), settle = (t - 0.6) / 0.4;
            return {
                w: lerp(0.004, 0.5, stretch),
                h: t < 0.15 ? 0.0025 : lerp(0.0025, 0.5, open),
                flash: t < 0.15 ? 0 : t < 0.6 ? 0.55 * (1 - open) : 0,
                scan: t < 0.6 ? 0.35 : lerp(0.35, 0, settle),
                glow: t < 0.6 ? 1 : lerp(1, 0, settle),
            };
        }
        const close = easeIn(t / 0.55), shrink = easeIn((t - 0.55) / 0.3), fade = (t - 0.85) / 0.15;
        return {
            w: t < 0.55 ? 0.5 : lerp(0.5, 0.004, shrink),
            h: lerp(0.5, 0.0025, close),
            flash: t < 0.55 ? 0.45 * close : 0,
            scan: 0.35,
            glow: t < 0.85 ? 1 : lerp(1, 0, fade),
        };
    }

    function create(canvas) {
        const gl = canvas.getContext('webgl2', { alpha: true, premultipliedAlpha: true, antialias: false });
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
        gl.useProgram(program);
        gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
        const pos = gl.getAttribLocation(program, 'a_pos');
        gl.enableVertexAttribArray(pos);
        gl.vertexAttribPointer(pos, 2, gl.FLOAT, false, 0, 0);
        const u = (name) => gl.getUniformLocation(program, name);
        return { gl, raf: 0, done: null, u: { size: u('u_size'), open: u('u_open'), flash: u('u_flash'), scan: u('u_scan'), glow: u('u_glow') } };
    }

    function stateFor(canvas) {
        let state = states.get(canvas);
        if (state === undefined) {
            try { state = create(canvas); } catch { state = null; }
            states.set(canvas, state);
        }
        return state;
    }

    function draw(canvas, state, on, t) {
        const { gl, u } = state;
        const dpr = Math.min(devicePixelRatio || 1, 2);
        const width = Math.max(1, Math.round(canvas.clientWidth * dpr));
        const height = Math.max(1, Math.round(canvas.clientHeight * dpr));
        if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height; }
        const s = shape(on, t);
        gl.viewport(0, 0, width, height);
        gl.uniform2f(u.size, width, height);
        gl.uniform2f(u.open, s.w, s.h);
        gl.uniform1f(u.flash, s.flash);
        gl.uniform1f(u.scan, s.scan);
        gl.uniform1f(u.glow, s.glow);
        gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    }

    function finish(canvas, state) {
        cancelAnimationFrame(state.raf);
        state.raf = 0;
        canvas.style.visibility = 'hidden';
        state.done?.();
        state.done = null;
    }

    /**
     * Plays the switch on (true) or off (false) over `canvas`. Resolves when it has finished, so a
     * caller can keep the picture in place until the tube is dark. Resolves at once when it cannot play.
     */
    function power(canvas, on) {
        if (!canvas || reduced.matches) return Promise.resolve(false);
        const state = stateFor(canvas);
        if (!state) return Promise.resolve(false);
        if (state.raf) finish(canvas, state);

        const length = on ? ON_MS : OFF_MS;
        const started = performance.now();
        canvas.style.visibility = 'visible';
        draw(canvas, state, on, 0); // the first frame lands now, before anything is docked under it
        return new Promise((resolve) => {
            state.done = () => resolve(true);
            const step = (now) => {
                const t = (now - started) / length;
                if (t >= 1 || !canvas.isConnected) {
                    finish(canvas, state);
                    // Live is gone: give its WebGL context back now (Chrome caps live contexts at ~16).
                    if (!canvas.isConnected) { state.gl.getExtension('WEBGL_lose_context')?.loseContext(); states.delete(canvas); }
                    return;
                }
                draw(canvas, state, on, t);
                state.raf = requestAnimationFrame(step);
            };
            state.raf = requestAnimationFrame(step);
        });
    }

    /** Draws one still moment of the effect (for screenshots and checks); false without WebGL2. */
    function frame(canvas, on, t) {
        const state = canvas && stateFor(canvas);
        if (!state) return false;
        canvas.style.visibility = 'visible';
        draw(canvas, state, on, t);
        return true;
    }

    window.powatchCrt = { power, frame };
})();
