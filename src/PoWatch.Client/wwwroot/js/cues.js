// Sound cues and desktop notifications. All sound is synthesised locally; the public powatchCues
// methods and their original argument order are kept stable for the Blazor callers.
(function () {
    'use strict';

    const KEY = 'powatch:sound';
    const LEVELS = { off: 0, low: 0.3, mid: 0.6, high: 1 };
    const ORDER = ['off', 'low', 'mid', 'high'];
    const MAX_VOICES = 8;
    const WORKLET_URL = new URL('audio-worklet.js',
        document.currentScript?.src || new URL('js/cues.js', document.baseURI).href).href;

    // Modes are [frequency ratio, relative amplitude, decay multiplier].
    const CUES = {
        start: [
            { frequency: 62, at: 0, duration: 0.13, amplitude: 0.72, decay: 0.085, attack: 0.004, noise: 0.34, modes: [[1, 0.7, 0.7], [2.12, 0.23, 0.6], [3.9, 0.08, 0.55]] },
            { frequency: 320, at: 0.03, duration: 0.42, amplitude: 0.66, decay: 0.2, attack: 0.012, noise: 0.08, modes: [[1, 0.72, 1], [2.76, 0.2, 0.78], [4.2, 0.07, 0.66]] },
            { frequency: 880, at: 0.45, duration: 0.16, amplitude: 0.42, decay: 0.09, attack: 0.006, noise: 0.04, modes: [[1, 0.76, 0.8], [2.01, 0.16, 0.65]] },
        ],
        stop: [
            { frequency: 1900, frequencyEnd: 240, at: 0, duration: 0.36, amplitude: 0.58, decay: 0.24, attack: 0.008, noise: 0.025, modes: [[1, 0.78, 1], [2.4, 0.13, 0.7]] },
            { frequency: 62, at: 0.34, duration: 0.14, amplitude: 0.7, decay: 0.09, attack: 0.004, noise: 0.3, modes: [[1, 0.72, 0.7], [2.1, 0.2, 0.6], [3.8, 0.08, 0.55]] },
        ],
        enter: [
            { frequency: 880, at: 0, duration: 0.09, amplitude: 0.5, decay: 0.05, attack: 0.003, noise: 0.025, modes: [[1, 0.8, 0.8], [2.03, 0.16, 0.6]] },
        ],
        regular: [
            { frequency: 587, at: 0, duration: 0.09, amplitude: 0.42, decay: 0.055, attack: 0.003, noise: 0.03, modes: [[1, 0.76, 0.8], [2.02, 0.17, 0.6]] },
            { frequency: 880, at: 0.08, duration: 0.15, amplitude: 0.42, decay: 0.09, attack: 0.005, noise: 0.02, modes: [[1, 0.78, 0.8], [2.01, 0.15, 0.6]] },
        ],
        alert: [
            { frequency: 740, at: 0, duration: 0.13, amplitude: 0.7, decay: 0.075, attack: 0.002, noise: 0.13, modes: [[1, 0.76, 0.9], [2.68, 0.24, 0.7], [4.1, 0.08, 0.5]] },
            { frequency: 740, at: 0.18, duration: 0.13, amplitude: 0.7, decay: 0.075, attack: 0.002, noise: 0.13, modes: [[1, 0.76, 0.9], [2.68, 0.24, 0.7], [4.1, 0.08, 0.5]] },
        ],
        error: [
            { frequency: 196, at: 0, duration: 0.29, amplitude: 0.68, decay: 0.2, attack: 0.006, noise: 0.22, modes: [[1, 0.72, 1], [1.48, 0.28, 0.82], [2.1, 0.12, 0.7]] },
        ],
        click: [
            { frequency: 1200, at: 0, duration: 0.035, amplitude: 0.3, decay: 0.018, attack: 0.001, noise: 0.06, modes: [[1, 0.82, 0.7], [2.7, 0.13, 0.5]] },
        ],
    };

    const PRIORITY = { click: 0, enter: 1, regular: 1, start: 2, stop: 2, alert: 4, error: 4 };
    let level = 'low';
    try {
        const stored = localStorage.getItem(KEY);
        if (Object.prototype.hasOwnProperty.call(LEVELS, stored)) level = stored;
    } catch (error) {
        console.warn('PoWatch could not read the sound preference.', error);
    }

    let ctx;
    let graph;
    let workletPromise;
    let workletReady = false;
    let workletWarningShown = false;
    let audioWarningShown = false;
    let activeAlertCount = 0;
    let roomBuffer;
    const voices = [];

    function warnOnce(kind, message, error) {
        if (kind === 'worklet') {
            if (workletWarningShown) return;
            workletWarningShown = true;
        } else {
            if (audioWarningShown) return;
            audioWarningShown = true;
        }
        console.warn(message, error);
    }

    function ensureContext() {
        if (ctx) return ctx;
        const AudioContextType = window.AudioContext || window.webkitAudioContext;
        if (!AudioContextType) {
            warnOnce('audio', 'PoWatch sound is unavailable because Web Audio is not supported.');
            return null;
        }
        try {
            ctx = new AudioContextType();
            graph = createGraph(ctx);
            loadWorklet(ctx);
            return ctx;
        } catch (error) {
            ctx = null;
            graph = null;
            warnOnce('audio', 'PoWatch could not initialise Web Audio.', error);
            return null;
        }
    }

    function createGraph(context) {
        const master = context.createGain();
        const compressor = context.createDynamicsCompressor();
        compressor.threshold.value = -20;
        compressor.knee.value = 18;
        compressor.ratio.value = 8;
        compressor.attack.value = 0.004;
        compressor.release.value = 0.16;
        master.gain.value = 0.72;
        master.connect(compressor).connect(context.destination);

        const decoration = context.createGain();
        const alert = context.createGain();
        decoration.gain.value = 1;
        alert.gain.value = 1;
        decoration.connect(master);
        alert.connect(master);
        return { master, decoration, alert };
    }

    function loadWorklet(context) {
        if (workletPromise || !context.audioWorklet || typeof window.AudioWorkletNode !== 'function') return;
        try {
            workletPromise = context.audioWorklet.addModule(WORKLET_URL).then(() => {
                workletReady = true;
            }).catch(error => {
                warnOnce('worklet', 'PoWatch is using procedural oscillator sound because AudioWorklet could not load.', error);
            });
        } catch (error) {
            workletPromise = Promise.resolve();
            warnOnce('worklet', 'PoWatch is using procedural oscillator sound because AudioWorklet is unavailable.', error);
        }
    }

    function normalize(value, fallback = 0) {
        const number = Number(value);
        return Number.isFinite(number) ? number : fallback;
    }

    function xorshift(seed) {
        let state = (seed >>> 0) || 0x6d2b79f5;
        return () => {
            state ^= state << 13;
            state ^= state >>> 17;
            state ^= state << 5;
            return (state >>> 0) / 0x100000000;
        };
    }

    function hashSeed(value) {
        if (typeof value === 'number' && Number.isFinite(value)) return value >>> 0;
        const text = String(value);
        let hash = 2166136261;
        for (let i = 0; i < text.length; i++) {
            hash ^= text.charCodeAt(i);
            hash = Math.imul(hash, 16777619);
        }
        return hash >>> 0;
    }

    function parseOptions(options) {
        if (options && typeof options === 'object') {
            const seed = Object.prototype.hasOwnProperty.call(options, 'seed')
                ? hashSeed(options.seed)
                : Math.floor(Math.random() * 0xffffffff);
            return { seed, room: options.room === true };
        }
        return {
            seed: options === undefined ? Math.floor(Math.random() * 0xffffffff) : hashSeed(options),
            room: false,
        };
    }

    function setParam(param, value, time, duration = 0.06) {
        if (!param) return;
        param.cancelScheduledValues(time);
        param.setValueAtTime(param.value, time);
        param.linearRampToValueAtTime(value, time + duration);
    }

    function setDuck() {
        const time = ctx.currentTime;
        setParam(graph.decoration.gain, activeAlertCount ? 0.28 : 1, time, 0.045);
    }

    function makePanner(context, pan, depth, time, duration) {
        const x = Math.max(-1, Math.min(1, pan));
        const z = -1 - depth * 1.15;
        let panner;
        if (typeof context.createPanner === 'function') {
            panner = context.createPanner();
            panner.panningModel = 'HRTF';
            panner.distanceModel = 'inverse';
            panner.refDistance = 1;
            panner.rolloffFactor = 0.12;
            panner.maxDistance = 8;
            if (panner.positionX) {
                panner.positionX.setValueAtTime(x, time);
                panner.positionX.linearRampToValueAtTime(x + (x < 0.75 ? 0.045 : -0.045), time + duration);
                panner.positionY.setValueAtTime(0, time);
                panner.positionY.linearRampToValueAtTime(0.015, time + duration);
                panner.positionZ.setValueAtTime(z, time);
                panner.positionZ.linearRampToValueAtTime(z - 0.06, time + duration);
            } else {
                panner.setPosition(x, 0, z);
            }
        } else if (typeof context.createStereoPanner === 'function') {
            panner = context.createStereoPanner();
            panner.pan.setValueAtTime(x, time);
            panner.pan.linearRampToValueAtTime(x + (x < 0.75 ? 0.045 : -0.045), time + duration);
        } else {
            panner = context.createGain();
        }
        return panner;
    }

    function makeRoomBuffer(context) {
        if (roomBuffer) return roomBuffer;
        const length = Math.max(1, Math.floor(context.sampleRate * 0.22));
        roomBuffer = context.createBuffer(2, length, context.sampleRate);
        const random = xorshift(0x50a7c4);
        for (let channel = 0; channel < 2; channel++) {
            const data = roomBuffer.getChannelData(channel);
            for (let i = 0; i < length; i++) {
                const t = i / length;
                data[i] = (random() * 2 - 1) * Math.pow(1 - t, 3.2) * (channel ? 0.82 : 1);
            }
            // Sparse wall reflections lead into the diffuse tail, with a tiny stereo offset.
            for (const [seconds, amplitude] of [[0.013, 2.4], [0.029, 1.65], [0.047, 1.1]]) {
                const index = Math.floor((seconds + channel * 0.0015) * context.sampleRate);
                if (index < length) data[index] += amplitude;
            }
        }
        return roomBuffer;
    }

    function makeRoomSend(context, source, bus) {
        const send = context.createGain();
        const convolver = context.createConvolver();
        const wet = context.createGain();
        send.gain.value = 0.14;
        wet.gain.value = 0.22;
        convolver.buffer = makeRoomBuffer(context);
        source.connect(send).connect(convolver).connect(wet).connect(bus);
        return [send, convolver, wet];
    }

    function makeVoice(name, pan, depth, energy, options) {
        const context = ensureContext();
        if (!context) return;
        if (context.state === 'suspended') {
            try {
                const resume = context.resume();
                if (resume && typeof resume.catch === 'function') {
                    resume.catch(error => warnOnce('audio', 'PoWatch could not resume Web Audio after the user gesture.', error));
                }
            } catch (error) {
                warnOnce('audio', 'PoWatch could not resume Web Audio after the user gesture.', error);
            }
        }

        const notes = CUES[name];
        const duration = Math.max(...notes.map(note => note.at + note.duration))
            + 0.055 + (options.room ? 0.22 : 0);
        const priority = PRIORITY[name] ?? 1;
        if (!reserveVoice(priority)) return;

        const time = context.currentTime;
        const safePan = Math.max(-1, Math.min(1, normalize(pan)));
        const safeDepth = Math.max(0, Math.min(1, normalize(depth)));
        const impact = 0.75 + 0.5 * Math.max(0, Math.min(1, normalize(energy, 0.5)));
        const voiceGain = context.createGain();
        const filter = context.createBiquadFilter();
        const panner = makePanner(context, safePan, safeDepth, time, duration);
        filter.type = 'lowpass';
        filter.frequency.setValueAtTime(17000 * Math.pow(0.38, safeDepth), time);
        filter.Q.value = 0.55;
        voiceGain.gain.value = LEVELS[level] * impact * 0.58 * (1 - safeDepth * 0.13);
        voiceGain.connect(filter).connect(panner);
        const decoration = name !== 'alert' && name !== 'error';
        const bus = decoration ? graph.decoration : graph.alert;
        panner.connect(bus);
        const voice = { priority, startedAt: time, nodes: [voiceGain, filter, panner], sources: [], timer: null, alert: !decoration };
        voices.push(voice);
        if (voice.alert) {
            activeAlertCount++;
            setDuck();
        }
        if (options.room) voice.nodes.push(...makeRoomSend(context, panner, bus));

        const random = xorshift(options.seed);
        for (let index = 0; index < notes.length; index++) {
            const note = notes[index];
            const seed = (options.seed + Math.imul(index + 1, 0x9e3779b9)) >>> 0;
            if (workletReady) {
                try {
                    createWorkletNote(context, note, time + note.at, seed, voiceGain, voice);
                    continue;
                } catch (error) {
                    warnOnce('worklet', 'PoWatch is using procedural oscillator sound because an AudioWorklet voice could not start.', error);
                }
            }
            createOscillatorNote(context, note, time + note.at, random, voiceGain, voice);
        }
        voice.timer = window.setTimeout(() => finishVoice(voice, false), Math.ceil(duration * 1000));
    }

    function reserveVoice(priority) {
        while (voices.length >= MAX_VOICES) {
            let victim = voices[0];
            for (const voice of voices) {
                if (voice.priority < victim.priority
                    || (voice.priority === victim.priority && voice.startedAt < victim.startedAt)) victim = voice;
            }
            if (victim.priority > priority) return false;
            finishVoice(victim, true);
        }
        return true;
    }

    function createWorkletNote(context, note, startAt, seed, output, voice) {
        const node = new window.AudioWorkletNode(context, 'powatch-modal-synth', {
            numberOfInputs: 0,
            numberOfOutputs: 1,
            outputChannelCount: [1],
            processorOptions: { ...note, startAt, seed },
        });
        node.connect(output);
        voice.nodes.push(node);
    }

    function createOscillatorNote(context, note, startAt, random, output, voice) {
        const endAt = startAt + note.duration;
        const noteGain = context.createGain();
        noteGain.gain.value = 0.2;
        noteGain.connect(output);
        voice.nodes.push(noteGain);
        for (const [ratio, amplitude, decayMultiplier] of note.modes) {
            const oscillator = context.createOscillator();
            const envelope = context.createGain();
            oscillator.type = 'sine';
            const firstFrequency = Math.max(20, note.frequency * ratio);
            oscillator.frequency.setValueAtTime(firstFrequency, startAt);
            if (note.frequencyEnd) {
                oscillator.frequency.exponentialRampToValueAtTime(Math.max(20, note.frequencyEnd * ratio), endAt);
            }
            const attack = Math.min(note.attack, note.duration * 0.3);
            const peak = Math.max(0.0001, note.amplitude * amplitude * 0.33);
            const decayAt = Math.min(endAt, Math.max(startAt + attack + 0.001, startAt + note.decay * decayMultiplier));
            envelope.gain.setValueAtTime(0.0001, startAt);
            envelope.gain.exponentialRampToValueAtTime(peak, startAt + attack);
            envelope.gain.exponentialRampToValueAtTime(Math.max(0.0001, peak * 0.12), decayAt);
            envelope.gain.exponentialRampToValueAtTime(0.0001, endAt);
            oscillator.connect(envelope).connect(noteGain);
            oscillator.start(startAt);
            oscillator.stop(endAt + 0.02);
            voice.nodes.push(oscillator, envelope);
            voice.sources.push(oscillator);
        }
        if (note.noise > 0) {
            const length = Math.max(1, Math.floor(context.sampleRate * Math.min(note.duration, 0.05)));
            const buffer = context.createBuffer(1, length, context.sampleRate);
            const samples = buffer.getChannelData(0);
            for (let i = 0; i < length; i++) samples[i] = random() * 2 - 1;
            const source = context.createBufferSource();
            const highpass = context.createBiquadFilter();
            const envelope = context.createGain();
            source.buffer = buffer;
            highpass.type = 'highpass';
            highpass.frequency.value = 700;
            envelope.gain.setValueAtTime(Math.max(0.0001, note.noise * note.amplitude * 0.16), startAt);
            envelope.gain.exponentialRampToValueAtTime(0.0001, Math.min(endAt, startAt + 0.04));
            source.connect(highpass).connect(envelope).connect(noteGain);
            source.start(startAt);
            source.stop(Math.min(endAt, startAt + 0.05) + 0.005);
            voice.nodes.push(source, highpass, envelope);
            voice.sources.push(source);
        }
    }

    function finishVoice(voice, stop) {
        const index = voices.indexOf(voice);
        if (index < 0) return;
        voices.splice(index, 1);
        if (voice.timer !== null) window.clearTimeout(voice.timer);
        if (voice.alert) {
            activeAlertCount = Math.max(0, activeAlertCount - 1);
            setDuck();
        }
        for (const node of voice.nodes) {
            if (stop && node.port) node.port.postMessage({ type: 'stop' });
            node.disconnect();
        }
        if (stop) {
            for (const source of voice.sources) {
                try {
                    source.stop(ctx.currentTime);
                } catch (error) {
                    if (error.name !== 'InvalidStateError') throw error;
                }
            }
        }
    }

    /**
     * Plays a cue. Existing arguments remain `name, pan, depth, energy`; optional fifth argument
     * may be a deterministic seed or `{ seed, room: true }`.
     */
    function play(name, pan, depth, energy, options) {
        if (level === 'off') return;
        const notes = CUES[name];
        if (notes) makeVoice(name, pan, depth, energy, parseOptions(options));
    }

    /** True while a cue or its short acoustic tail may still be audible. */
    function isPlaying() {
        return voices.length > 0;
    }

    /** Steps off → low → mid → high → off, remembers it, and returns the new level. */
    function cycle() {
        level = ORDER[(ORDER.indexOf(level) + 1) % ORDER.length];
        try {
            localStorage.setItem(KEY, level);
        } catch (error) {
            console.warn('PoWatch could not save the sound preference.', error);
        }
        if (level === 'off') {
            for (const voice of [...voices]) finishVoice(voice, true);
        } else {
            play('click');
        }
        return level;
    }

    /** Shows a desktop notification if the user allowed them; returns whether it was shown. */
    function notify(text) {
        try {
            if (!('Notification' in window) || Notification.permission !== 'granted') return false;
            new Notification('PoWatch', { body: text, tag: 'powatch-alert', icon: 'icon-192.png' });
            return true;
        } catch (error) {
            console.warn('PoWatch could not show a desktop notification.', error);
            return false;
        }
    }

    /** Asks for permission (must follow a click); returns "granted", "denied", "default" or "unsupported". */
    async function allowNotifications() {
        if (!('Notification' in window)) return 'unsupported';
        try {
            return await Notification.requestPermission();
        } catch (error) {
            console.warn('PoWatch could not request notification permission.', error);
            return 'denied';
        }
    }

    window.powatchCues = { play, cycle, level: () => level, isPlaying, notify, allowNotifications };
})();
