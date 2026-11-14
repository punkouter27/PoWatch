// Standalone: node scripts/sensory-audio.test.cjs (no browser or packages required).
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const audioDirectory = path.join(__dirname, '..', 'src', 'PoWatch.Client', 'wwwroot', 'js');
const cuesSource = fs.readFileSync(path.join(audioDirectory, 'cues.js'), 'utf8');
const workletSource = fs.readFileSync(path.join(audioDirectory, 'audio-worklet.js'), 'utf8');

class Param {
    constructor(value = 0) { this.value = value; this.events = []; }
    cancelScheduledValues(time) { this.events.push(['cancel', time]); }
    setValueAtTime(value, time) { this.value = value; this.events.push(['set', value, time]); }
    linearRampToValueAtTime(value, time) { this.events.push(['linear', value, time]); }
    exponentialRampToValueAtTime(value, time) { this.events.push(['exp', value, time]); }
}

class Node {
    constructor() {
        this.gain = new Param(1);
        this.frequency = new Param(440);
        this.Q = new Param(1);
        this.pan = new Param(0);
        this.positionX = new Param(0);
        this.positionY = new Param(0);
        this.positionZ = new Param(-1);
        this.connections = [];
        this.disconnected = false;
        this.stops = [];
    }
    connect(node) { this.connections.push(node); return node; }
    disconnect() { this.disconnected = true; }
    start(time) { this.startedAt = time; }
    stop(time) { this.stops.push(time); }
    setPosition(x, y, z) { this.position = [x, y, z]; }
}

class Context {
    constructor() {
        this.state = 'suspended';
        this.currentTime = 1;
        this.sampleRate = 8000;
        this.destination = new Node();
        this.oscillators = [];
        this.buffers = [];
        this.panners = [];
        this.convolvers = [];
        this.resumeCount = 0;
    }
    resume() { this.resumeCount++; this.state = 'running'; return Promise.resolve(); }
    createGain() { return new Node(); }
    createDynamicsCompressor() {
        const node = new Node();
        for (const key of ['threshold', 'knee', 'ratio', 'attack', 'release']) node[key] = new Param();
        return node;
    }
    createBiquadFilter() { return new Node(); }
    createPanner() { const node = new Node(); this.panners.push(node); return node; }
    createStereoPanner() { return new Node(); }
    createOscillator() { const node = new Node(); this.oscillators.push(node); return node; }
    createBuffer(channels, length, rate) {
        const data = Array.from({ length: channels }, () => new Float32Array(length));
        const buffer = { numberOfChannels: channels, length, sampleRate: rate, getChannelData: index => data[index], data };
        this.buffers.push(buffer);
        return buffer;
    }
    createBufferSource() { return new Node(); }
    createConvolver() { const node = new Node(); this.convolvers.push(node); return node; }
}

function harness(initialLevel = 'low', options = {}) {
    const storage = new Map([['powatch:sound', initialLevel]]);
    const contexts = [];
    const worklets = [];
    const moduleUrls = [];
    const warnings = [];
    const timers = new Map();
    let timerId = 0;
    class AudioContext extends Context {
        constructor() {
            super();
            if (options.worklet) this.audioWorklet = {
                addModule: url => {
                    moduleUrls.push(url);
                    return options.rejectWorklet ? Promise.reject(new Error('blocked')) : Promise.resolve();
                },
            };
            contexts.push(this);
        }
    }
    class AudioWorkletNode extends Node {
        constructor(context, name, config) {
            super();
            this.name = name;
            this.config = config;
            this.messages = [];
            this.port = { postMessage: message => this.messages.push(message) };
            worklets.push(this);
        }
    }
    const document = {
        currentScript: options.noCurrentScript ? null : { src: 'https://example.test/app/js/cues.js' },
        baseURI: 'https://example.test/app/',
    };
    const window = {
        AudioContext,
        AudioWorkletNode: options.worklet ? AudioWorkletNode : undefined,
        location: { href: 'https://example.test/app/journal/day' },
        setTimeout: (callback, delay) => { timers.set(++timerId, { callback, delay }); return timerId; },
        clearTimeout: id => timers.delete(id),
    };
    const sandbox = {
        window, document, URL,
        console: { warn: (...args) => warnings.push(args) },
        localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) },
    };
    vm.runInNewContext(cuesSource, sandbox, { filename: 'cues.js' });
    // A real deferred script has finished evaluating by the time Blazor calls play.
    document.currentScript = null;
    return { cues: window.powatchCues, contexts, worklets, moduleUrls, warnings, timers };
}

function turnOff(cues) {
    while (cues.level() !== 'off') cues.cycle();
}

async function tests() {
    const off = harness('off');
    assert.deepEqual(Object.keys(off.cues).sort(), ['allowNotifications', 'cycle', 'isPlaying', 'level', 'notify', 'play']);
    off.cues.play('alert', 0, 0, 0.5);
    assert.equal(off.contexts.length, 0, 'off must not create or resume audio');
    assert.equal(off.cues.cycle(), 'low', 'cycle preserves the existing level sequence');
    assert.equal(off.contexts[0].resumeCount, 1, 'a gesture-triggered play resumes the context');
    turnOff(off.cues);
    assert.equal(off.timers.size, 0, 'sound-off clears pending cleanup timers');
    off.contexts[0].state = 'suspended';
    off.cues.play('alert');
    assert.equal(off.contexts[0].resumeCount, 1, 'sound-off never resumes a suspended context');

    const fallback = harness('mid');
    fallback.cues.play('enter', -0.5, 0.7, 0.5, 'repeatable');
    fallback.cues.play('enter', -0.5, 0.7, 0.5, 'repeatable');
    fallback.cues.play('enter', -0.5, 0.7, 0.5, 'different');
    const context = fallback.contexts[0];
    assert.equal(context.oscillators.length, 6, 'unavailable worklets use resonant oscillator partials');
    const noise = context.buffers.filter(buffer => buffer.numberOfChannels === 1);
    assert.deepEqual(noise[0].data[0], noise[1].data[0], 'equal string seeds repeat the fallback excitation');
    assert.notDeepEqual(noise[0].data[0], noise[2].data[0], 'different seeds vary the fallback excitation');
    assert.equal(context.panners[0].panningModel, 'HRTF');
    assert(context.panners[0].positionX.events.some(event => event[0] === 'linear'), 'spatial trajectories use audio automation');

    fallback.cues.play('enter', 0, 1, 0.5, { seed: 'repeatable', room: true });
    assert.equal(context.convolvers.length, 1, 'room acoustics are opt-in');
    const decorationBus = context.panners[3].connections[0];
    const wet = context.convolvers[0].connections[0];
    assert.equal(wet.connections[0], decorationBus, 'decorative wet and dry signals share the duck bus');
    const room = context.buffers.find(buffer => buffer.numberOfChannels === 2);
    assert(room, 'room impulse response is generated locally');
    assert(room.data[0][Math.floor(0.013 * context.sampleRate)] > 1, 'the room includes a discrete early reflection');
    const dryTimers = [...fallback.timers.values()].filter(timer => timer.delay < 200);
    for (const timer of dryTimers) timer.callback();
    assert.equal(fallback.cues.isPlaying(), true, 'the microphone guard includes the room tail after dry cues end');

    fallback.cues.play('alert', 0, 0, 0.5, { seed: 'alert', room: true });
    const alertBus = context.panners[4].connections[0];
    assert.notEqual(alertBus, decorationBus, 'alerts have an independent bus');
    assert.equal(context.convolvers[1].connections[0].connections[0], alertBus, 'alert wet and dry signals share the alert bus');
    assert(decorationBus.gain.events.some(event => event[0] === 'linear' && event[1] === 0.28), 'alerts duck decorative dry and wet signals');
    turnOff(fallback.cues);
    assert.equal(fallback.cues.isPlaying(), false, 'sound-off clears the feedback guard');
    assert(context.convolvers.every(node => node.disconnected), 'sound-off clears acoustic tails');

    const bounded = harness('mid');
    for (let i = 0; i < 8; i++) bounded.cues.play('regular', 0, 0, 0.5, i);
    const boundedContext = bounded.contexts[0];
    const oldestSources = boundedContext.oscillators.slice(0, 4);
    bounded.cues.play('alert', 0, 0, 0.5, 99);
    assert(oldestSources.every(source => source.disconnected && source.stops.length === 2), 'alerts displace and stop an older decoration');
    assert.equal(bounded.timers.size, 8, 'admitted voices stay bounded');
    turnOff(bounded.cues);
    const alertOnly = harness();
    for (let i = 0; i < 8; i++) alertOnly.cues.play('alert');
    const before = alertOnly.contexts[0].oscillators.length;
    alertOnly.cues.play('click');
    assert.equal(alertOnly.contexts[0].oscillators.length, before, 'low-priority decoration cannot displace alerts');
    turnOff(alertOnly.cues);

    for (const noCurrentScript of [false, true]) {
        const worklet = harness('mid', { worklet: true, noCurrentScript });
        worklet.cues.play('enter', 0, 0, 0.5, { seed: 'replay', room: true });
        assert.deepEqual(worklet.moduleUrls, ['https://example.test/app/js/audio-worklet.js'], 'worklet URL uses the captured script URL or app base, never the active route');
        await Promise.resolve();
        worklet.cues.play('enter', 0, 0, 0.5, { seed: 'replay', room: true });
        worklet.cues.play('enter', 0, 0, 0.5, 'replay');
        assert.equal(worklet.worklets.length, 2, 'loaded worklets replace fallback synthesis');
        assert.equal(worklet.worklets[0].config.processorOptions.seed, worklet.worklets[1].config.processorOptions.seed, 'object and scalar seed arguments are equivalent');
        turnOff(worklet.cues);
        assert(worklet.worklets.every(node => node.disconnected && node.messages[0].type === 'stop'), 'sound-off stops and disconnects worklets');
    }
    const rejected = harness('low', { worklet: true, rejectWorklet: true });
    rejected.cues.play('enter');
    await Promise.resolve();
    await Promise.resolve();
    rejected.cues.play('enter');
    assert.equal(rejected.worklets.length, 0);
    assert.equal(rejected.contexts[0].oscillators.length, 4, 'module rejection retains procedural synthesis');
    assert.equal(rejected.warnings.length, 1, 'worklet load failures are diagnosed rather than silently ignored');
    turnOff(rejected.cues);

    let Synth;
    class WorkletBase { constructor() { this.port = { onmessage: null }; } }
    const workletSandbox = {
        AudioWorkletProcessor: WorkletBase,
        registerProcessor: (name, processor) => {
            assert.equal(name, 'powatch-modal-synth');
            Synth = processor;
        },
        sampleRate: 8000,
        currentTime: 0,
    };
    vm.runInNewContext(workletSource, workletSandbox, { filename: 'audio-worklet.js' });
    const options = { processorOptions: {
        frequency: 440, startAt: 0, duration: 0.2, amplitude: 0.6, decay: 0.1,
        attack: 0.004, noise: 0.2, modes: [[1, 0.8, 1]], seed: 42,
    } };
    const processor = new Synth(options);
    const samples = new Float32Array(128);
    assert.equal(processor.process([], [[samples]]), true);
    assert(samples.some(sample => sample !== 0), 'the modal worklet emits an enveloped voice');
    const duplicateSamples = new Float32Array(128);
    new Synth(options).process([], [[duplicateSamples]]);
    assert.deepEqual(samples, duplicateSamples, 'the same worklet seed produces identical audio samples');
    processor.port.onmessage({ data: { type: 'stop' } });
    assert.equal(processor.process([], [[samples]]), false);
    assert(samples.every(sample => sample === 0), 'stopped worklets emit no residual samples');
    workletSandbox.currentTime = 0.3;
    assert.equal(new Synth(options).process([], [[samples]]), false, 'finished worklet voices terminate themselves');
    console.log('Sensory audio tests passed (fallback, worklet, seed, URL, room routing, tails, priority, sound-off).');
}

tests().catch(error => { console.error(error); process.exitCode = 1; });
