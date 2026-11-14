class PoWatchModalSynth extends AudioWorkletProcessor {
    constructor(options) {
        super();
        this.note = options.processorOptions;
        this.randomState = (this.note.seed >>> 0) || 0x6d2b79f5;
        this.stopped = false;
        this.port.onmessage = event => {
            if (event.data?.type === 'stop') this.stopped = true;
        };
    }

    random() {
        let value = this.randomState;
        value ^= value << 13;
        value ^= value >>> 17;
        value ^= value << 5;
        this.randomState = value >>> 0;
        return this.randomState / 0x100000000 * 2 - 1;
    }

    process(_inputs, outputs) {
        const output = outputs[0][0];
        const note = this.note;
        if (this.stopped) {
            output.fill(0);
            return false;
        }

        const sampleRateInverse = 1 / sampleRate;
        const attack = Math.max(0.001, note.attack || 0.004);
        const duration = Math.max(0.01, note.duration);
        for (let i = 0; i < output.length; i++) {
            const elapsed = currentTime + i * sampleRateInverse - note.startAt;
            if (elapsed < 0 || elapsed >= duration) {
                output[i] = 0;
                continue;
            }

            const attackEnvelope = 1 - Math.exp(-elapsed / attack);
            const releaseEnvelope = Math.min(1, (duration - elapsed) / 0.018);
            let resonant = 0;
            let modeWeight = 0;
            const sweep = note.frequencyEnd ? Math.log(note.frequencyEnd / note.frequency) / duration : 0;
            const phaseTime = sweep ? Math.expm1(sweep * elapsed) / sweep : elapsed;
            for (const [ratio, amplitude, decayMultiplier] of note.modes) {
                const decay = Math.max(0.02, note.decay * decayMultiplier);
                resonant += Math.sin(phaseTime * note.frequency * ratio * 2 * Math.PI) * amplitude * Math.exp(-elapsed / decay);
                modeWeight += amplitude;
            }

            const noise = elapsed < 0.05
                ? this.random() * note.noise * Math.exp(-elapsed * 95)
                : 0;
            output[i] = note.amplitude * attackEnvelope * releaseEnvelope
                * (resonant / Math.max(1, modeWeight) * 0.42 + noise * 0.12);
        }
        return currentTime + output.length * sampleRateInverse < note.startAt + duration;
    }
}

registerProcessor('powatch-modal-synth', PoWatchModalSynth);
