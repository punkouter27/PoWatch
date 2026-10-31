// The Live (and replay) motion grid as a WebGL2 fragment shader: the 16×9 grid is one small texture,
// sampled bilinearly (LINEAR filtering) so cells blend into each other, with a soft two-ring glow
// around hot spots, faint cell lines and scanlines on top — a thermal-camera look. Values ease toward each new frame, so the picture flows instead of stepping four times a
// second. Honours prefers-reduced-motion (no easing, no shimmer). draw() returns false when WebGL2
// is unavailable and the page falls back to the plain grid.
(function () {
    'use strict';

    const VERTEX = `#version 300 es
in vec2 a_pos;
out vec2 v_uv;
void main() {
  v_uv = vec2(a_pos.x * 0.5 + 0.5, 0.5 - a_pos.y * 0.5);
  gl_Position = vec4(a_pos, 0.0, 1.0);
}`;

    const FRAGMENT = `#version 300 es
precision highp float;
uniform sampler2D u_heat;
uniform vec2 u_grid;
uniform vec2 u_size;
uniform float u_time;
uniform vec3 u_hot;
uniform vec3 u_cold;
in vec2 v_uv;
out vec4 o_color;

// The texture's LINEAR filter does the bilinear blend; a smoothstep on the fraction first rounds off
// its creases, so there are no visible cell edges and no diamond artefacts.
float heat(vec2 uv) {
  vec2 p = uv * u_grid - 0.5;
  vec2 f = fract(p);
  f = f * f * (3.0 - 2.0 * f);
  return texture(u_heat, (floor(p) + f + 0.5) / u_grid).r;
}

// A soft glow: the average of two rings of taps, about one and two cells out, the far ring weaker,
// so heat bleeds into its neighbours like light through fog.
float glow(vec2 uv) {
  vec2 cell = 1.0 / u_grid;
  float near = 0.0;
  float far = 0.0;
  for (int k = 0; k < 8; k++) {
    float a = float(k) * 0.7853982 + 0.3926991;
    vec2 d = vec2(cos(a), sin(a)) * cell;
    near += heat(uv + d * 0.9);
    far += heat(uv + d * 2.0);
  }
  return (near * 0.65 + far * 0.35) / 8.0;
}

void main() {
  float h = heat(v_uv);
  float g = glow(v_uv);
  float v = clamp(pow(h, 1.5) * 0.85 + g * 0.2, 0.0, 1.0);

  vec3 hot = mix(u_hot, vec3(1.0, 0.96, 0.82), smoothstep(0.82, 1.0, v) * 0.55);
  vec3 color = mix(u_cold, hot, smoothstep(0.02, 1.0, v));
  color += u_hot * smoothstep(0.05, 0.9, g) * 0.14;
  color += hot * v * 0.07 * sin(u_time * 2.2 + v_uv.x * 18.0 + v_uv.y * 7.0);

  vec2 cell = abs(fract(v_uv * u_grid) - 0.5);
  color = mix(color, color * 0.8 + 0.02, smoothstep(0.47, 0.5, max(cell.x, cell.y)) * 0.2);
  color *= 0.93 + 0.07 * sin(v_uv.y * u_size.y * 3.14159);
  o_color = vec4(color, 1.0);
}`;

    const states = new WeakMap();
    const reduced = matchMedia('(prefers-reduced-motion: reduce)');

    function compile(gl, type, source) {
        const shader = gl.createShader(type);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader));
        return shader;
    }

    function create(canvas, cols, rows) {
        const gl = canvas.getContext('webgl2', { antialias: false, alpha: false });
        if (!gl) return null;
        const program = gl.createProgram();
        gl.attachShader(program, compile(gl, gl.VERTEX_SHADER, VERTEX));
        gl.attachShader(program, compile(gl, gl.FRAGMENT_SHADER, FRAGMENT));
        gl.linkProgram(program);
        if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program));
        gl.useProgram(program);

        gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
        const pos = gl.getAttribLocation(program, 'a_pos');
        gl.enableVertexAttribArray(pos);
        gl.vertexAttribPointer(pos, 2, gl.FLOAT, false, 0, 0);

        gl.bindTexture(gl.TEXTURE_2D, gl.createTexture());
        gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
        for (const [name, value] of [[gl.TEXTURE_MIN_FILTER, gl.LINEAR], [gl.TEXTURE_MAG_FILTER, gl.LINEAR], [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]])
            gl.texParameteri(gl.TEXTURE_2D, name, value);

        const uniform = (name) => gl.getUniformLocation(program, name);
        return {
            gl, cols, rows, raf: 0,
            shown: new Float32Array(cols * rows),
            target: new Float32Array(cols * rows),
            pixels: new Uint8Array(cols * rows),
            u: { grid: uniform('u_grid'), size: uniform('u_size'), time: uniform('u_time'), hot: uniform('u_hot'), cold: uniform('u_cold') },
        };
    }

    function rgb(canvas, variable, fallback) {
        const hex = getComputedStyle(canvas).getPropertyValue(variable).trim() || fallback;
        const n = parseInt(hex.slice(1), 16);
        return [(n >> 16 & 255) / 255, (n >> 8 & 255) / 255, (n & 255) / 255];
    }

    function render(canvas, state) {
        const { gl, shown, target, pixels, u } = state;
        const ease = reduced.matches ? 1 : 0.12;
        for (let i = 0; i < shown.length; i++) {
            shown[i] += (target[i] - shown[i]) * ease;
            pixels[i] = Math.round(shown[i] * 255);
        }

        const dpr = Math.min(devicePixelRatio || 1, 2);
        const width = Math.max(1, Math.round(canvas.clientWidth * dpr));
        const height = Math.max(1, Math.round(canvas.clientHeight * dpr));
        if (canvas.width !== width || canvas.height !== height) {
            canvas.width = width;
            canvas.height = height;
        }
        gl.viewport(0, 0, width, height);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8, state.cols, state.rows, 0, gl.RED, gl.UNSIGNED_BYTE, pixels);
        gl.uniform2f(u.grid, state.cols, state.rows);
        gl.uniform2f(u.size, canvas.clientWidth, canvas.clientHeight);
        gl.uniform1f(u.time, reduced.matches ? 0 : performance.now() / 1000);
        gl.uniform3fv(u.hot, rgb(canvas, '--term-accent', '#8fdcff'));
        gl.uniform3fv(u.cold, rgb(canvas, '--term-bg', '#030a12'));
        gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    }

    function loop(canvas, state) {
        // The page that owned the canvas is gone: stop, and let the context be collected.
        if (!canvas.isConnected) { state.raf = 0; return; }
        render(canvas, state);
        state.raf = requestAnimationFrame(() => loop(canvas, state));
    }

    /** Sets the grid's values (row-major, any non-negative scale) and draws. False when WebGL2 is unavailable. */
    function draw(canvas, values, cols, rows) {
        if (!canvas) return false;
        let state = states.get(canvas);
        if (state === undefined) {
            try { state = create(canvas, cols, rows); } catch { state = null; }
            states.set(canvas, state);
        }
        if (!state) return false;

        // Shade relative to the busiest cell, so an even but busy frame still shows where it is busiest.
        let max = 0;
        for (const v of values) if (v > max) max = v;
        for (let i = 0; i < state.target.length; i++) state.target[i] = max > 0 ? (values[i] || 0) / max : 0;

        if (reduced.matches) render(canvas, state);
        else if (!state.raf) loop(canvas, state);
        return true;
    }

    window.powatchHeat = { draw };
})();
