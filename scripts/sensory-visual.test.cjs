const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

function runtime() {
    let hidden = false;
    const listeners = new Map(), frames = new Map();
    let id = 0;
    const element = { isConnected: true, getBoundingClientRect: () => ({ top: 0, bottom: 200, left: 0, right: 200, width: 200, height: 200 }) };
    const context = {
        console, Math, Set, Map, WeakMap, Number, String, URL, Float32Array,
        innerWidth: 1200, innerHeight: 800, devicePixelRatio: 1, performance: { now: () => 0 },
        matchMedia: () => ({ matches: false, addEventListener() {} }),
        document: {
            readyState: 'loading', get hidden() { return hidden; },
            addEventListener(name, fn) { listeners.set(name, fn); },
            querySelector: () => null,
        },
        requestAnimationFrame(fn) { const next = ++id; frames.set(next, fn); return next; },
        cancelAnimationFrame(key) { frames.delete(key); },
    };
    context.window = context;
    vm.createContext(context);
    vm.runInContext(readFileSync(resolve(__dirname, '..', 'src', 'PoWatch.Client', 'wwwroot', 'js', 'visuals.js'), 'utf8'), context);
    return { visual: context.powatchVisual, context, frames, element, setHidden(value) { hidden = value; } };
}

test('seeded procedural variation is repeatable and distinguishes events', () => {
    const { visual } = runtime();
    const one = visual.random('session:minute:a'), two = visual.random('session:minute:a'), other = visual.random('session:minute:b');
    const values = Array.from({ length: 10 }, one);
    assert.deepEqual(values, Array.from({ length: 10 }, two));
    assert.notDeepEqual(values, Array.from({ length: 10 }, other));
});

test('analytic spring produces the same result at different frame rates and settles', () => {
    const { visual } = runtime();
    const run = steps => {
        let value = 0, velocity = 0;
        for (let i = 0; i < steps; i++) [value, velocity] = visual.spring(value, velocity, 1, 1 / steps);
        return value;
    };
    assert.ok(Math.abs(run(30) - run(144)) < 1e-10);
    assert.ok(Math.abs(run(60) - 1) < 0.00001);
    assert.ok(Number.isFinite(visual.spring(0, 0, 1, 5)[0]));
});

test('effect budget degrades under load and recovers only after sustained healthy frames', () => {
    const { visual } = runtime();
    for (let time = 200; time <= 2400; time += 200) visual.budget(time, 40);
    assert.ok(visual.quality >= 0.35 && visual.quality < 0.5);
    visual.budget(2600, 16);
    assert.ok(visual.quality < 0.5);
    for (let time = 2800; time <= 48000; time += 200) visual.budget(time, 16);
    assert.equal(visual.quality, 1);
});

test('hidden and detached surfaces are not treated as renderable', () => {
    const { visual, element, setHidden } = runtime();
    assert.equal(visual.visible(element), true);
    setHidden(true);
    assert.equal(visual.visible(element), false);
    setHidden(false);
    element.isConnected = false;
    assert.equal(visual.visible(element), false);
});

test('particle bursts are bounded, deterministic and stop requesting frames after settling', () => {
    const host = runtime(), arcs = [];
    const ctx = { clearRect() {}, setTransform() {}, beginPath() {}, fill() {}, arc(...args) { arcs.push(args); } };
    const canvas = { setAttribute() {}, getContext: () => ctx };
    host.context.document.createElement = () => canvas;
    host.context.document.body = { append() {} };
    vm.runInContext(readFileSync(resolve(__dirname, '..', 'src', 'PoWatch.Client', 'wwwroot', 'js', 'fx.js'), 'utf8'), host.context);
    const draw = () => {
        const [key, fn] = host.frames.entries().next().value;
        host.frames.delete(key);
        fn(16.7);
    };
    host.context.powatchFx.burst(0, 'event', host.element);
    draw();
    const first = arcs.map(a => [...a]);
    host.context.powatchFx.clear();
    host.frames.clear(); arcs.length = 0;
    host.context.powatchFx.burst(0, 'event', host.element);
    draw();
    assert.deepEqual(arcs, first);
    for (let i = 0; i < 100; i++) host.context.powatchFx.burst(0, i, host.element);
    arcs.length = 0;
    draw();
    assert.ok(arcs.length <= 140);
    for (let time = 33.4; time < 3000 && host.frames.size; time += 16.7) {
        const [key, fn] = host.frames.entries().next().value;
        host.frames.delete(key);
        fn(time);
    }
    assert.equal(host.frames.size, 0);
});

test('microphone cue suppression drops contaminated audio and clears its speech pre-roll', async () => {
    let processor, report, playing = true, now = 0;
    const calls = [], workerCalls = [];
    const rpc = () => {
        const call = async payload => { workerCalls.push(payload.type); return {}; };
        call.keep = () => {};
        call.releaseAfter = () => {};
        return call;
    };
    const context = {
        console, Float32Array, Math, Date: { now: () => now },
        navigator: { mediaDevices: { getUserMedia: async () => ({ getTracks: () => [{ stop() {} }] }) } },
        setInterval(fn) { report = fn; return 1; }, clearInterval() {},
        AudioContext: class {
            createMediaStreamSource() { return { connect() {} }; }
            createScriptProcessor() { return processor = { connect() {}, disconnect() {} }; }
            close() { return Promise.resolve(); }
        },
        powatchWorkerRpc: rpc,
        powatchCues: { isPlaying: () => playing },
    };
    context.window = context;
    vm.createContext(context);
    vm.runInContext(readFileSync(resolve(__dirname, '..', 'src', 'PoWatch.Client', 'wwwroot', 'js', 'sensing', 'audio-layer.js'), 'utf8'), context);
    await context.powatchAudio.start({ invokeMethodAsync: async (...args) => { calls.push(args); } });
    const chunk = new Float32Array(4096).fill(0.1);
    const input = () => processor.onaudioprocess({ inputBuffer: { getChannelData: () => chunk } });
    input(); report();
    assert.equal(calls.length, 0);
    assert.deepEqual(workerCalls, ['warm']);
    playing = false;
    now = 10000;
    input(); report();
    assert.equal(calls[0][0], 'OnSoundLevel');
    assert.ok(calls[0][1] > 0);
    playing = true;
    input(); // Any speech begun before a cue must not survive it.
    playing = false;
    now = 12000;
    chunk.fill(0);
    input();
    assert.ok(!workerCalls.includes('transcribe'));
    context.powatchAudio.stop();
});
