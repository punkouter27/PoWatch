// Sound cues and desktop notifications. Every sound is synthesised with the Web Audio API (no audio
// files): short oscillator notes with a gain envelope, placed left or right of the listener (and a
// little in front) where in the frame the thing happened. HRTF binaural panning is used where the
// browser has it, plain stereo everywhere else. The level (off, low, mid, high) is remembered in this
// browser.
(function () {
    'use strict';

    const KEY = 'powatch:sound';
    const LEVELS = { off: 0, low: 0.3, mid: 0.6, high: 1 };
    const ORDER = ['off', 'low', 'mid', 'high'];

    // Each note: [frequency Hz, starts at s, lasts s, waveform, glides to Hz?, loudness ×?].
    // Start and stop: a thump and a glide up, a glide down and a thump.
    const CUES = {
        start: [[62, 0, 0.12, 'triangle'], [320, 0.03, 0.42, 'sine', 1900, 0.45], [880, 0.45, 0.16, 'sine']],
        stop: [[1900, 0, 0.36, 'sine', 240, 0.45], [62, 0.34, 0.14, 'triangle']],
        enter: [[880, 0, 0.06, 'sine']],
        regular: [[587, 0, 0.08, 'sine'], [880, 0.08, 0.14, 'sine']],
        alert: [[740, 0, 0.12, 'square'], [740, 0.18, 0.12, 'square']],
        error: [[196, 0, 0.28, 'sawtooth']],
        click: [[1200, 0, 0.025, 'square']],
    };

    let level = 'low';
    try {
        const stored = localStorage.getItem(KEY);
        if (stored in LEVELS) level = stored;
    } catch { /* private mode */ }

    let ctx = null;

    /**
     * Plays a cue; `pan` runs from -1 (left edge of the frame) to 1 (right edge), `depth` 0–1 is how far back.
     * `energy` 0–1 is how much is going on (motion): a busy scene lands a little harder and louder.
     */
    function play(name, pan, depth, energy) {
        const notes = CUES[name];
        if (notes) sound(notes, pan, depth, energy);
    }

    /**
     * Where the sound comes from: HRTF places a source a little in front of the listener at `pan`
     * across and `depth` back, so headphones get a real direction (and the cue sits outside the head
     * instead of in the middle of it). Without createPanner, a stereo panner is the fallback.
     */
    function spatial(ctx, pan, depth) {
        const x = Math.max(-1, Math.min(1, Number(pan) || 0));
        if (typeof ctx.createPanner !== 'function') {
            const stereo = ctx.createStereoPanner();
            stereo.pan.value = x;
            return stereo;
        }
        const panner = ctx.createPanner();
        panner.panningModel = 'HRTF';
        panner.distanceModel = 'inverse';
        panner.refDistance = 1;
        // Gentle, so the far side of the frame is quieter, not silent.
        panner.rolloffFactor = 0.12;
        panner.maxDistance = 8;
        const z = -1 - Math.max(0, Math.min(1, Number(depth) || 0));
        if (panner.positionX) {
            panner.positionX.value = x;
            panner.positionY.value = 0;
            panner.positionZ.value = z;
        } else {
            panner.setPosition(x, 0, z);
        }
        return panner;
    }

    function sound(notes, pan, depth, energy) {
        const volume = LEVELS[level];
        if (!volume) return;
        // Impact synthesis: the same cue lands harder when the room is busy. 0.75× quiet room, 1.25× busy.
        const impact = 0.75 + 0.5 * Math.max(0, Math.min(1, Number(energy) || 0.5));
        try {
            ctx ??= new AudioContext();
            if (ctx.state === 'suspended') ctx.resume();
            const out = spatial(ctx, pan, depth);
            out.connect(ctx.destination);

            for (const [frequency, at, lasts, wave, glideTo, loudness] of notes) {
                const from = ctx.currentTime + at;
                const osc = ctx.createOscillator();
                const gain = ctx.createGain();
                osc.type = wave;
                osc.frequency.setValueAtTime(frequency, from);
                if (glideTo) osc.frequency.exponentialRampToValueAtTime(glideTo, from + lasts);
                // A fast attack and an exponential decay: a pluck, not a beep.
                gain.gain.setValueAtTime(0.0001, from);
                gain.gain.exponentialRampToValueAtTime(0.18 * volume * impact * (loudness ?? 1), from + 0.012);
                gain.gain.exponentialRampToValueAtTime(0.0001, from + lasts);
                osc.connect(gain).connect(out);
                osc.start(from);
                osc.stop(from + lasts + 0.03);
            }
        } catch {
            // No audio device, or the browser has not seen a click yet: stay silent.
        }
    }

    /** Steps off → low → mid → high → off, remembers it, and returns the new level. */
    function cycle() {
        level = ORDER[(ORDER.indexOf(level) + 1) % ORDER.length];
        try { localStorage.setItem(KEY, level); } catch { /* private mode */ }
        play('click');
        return level;
    }

    /** Shows a desktop notification if the user allowed them; returns whether it was shown. */
    function notify(text) {
        try {
            if (!('Notification' in window) || Notification.permission !== 'granted') return false;
            new Notification('PoWatch', { body: text, tag: 'powatch-alert', icon: 'icon-192.png' });
            return true;
        } catch {
            return false;
        }
    }

    /** Asks for permission (must follow a click); returns "granted", "denied", "default" or "unsupported". */
    async function allowNotifications() {
        if (!('Notification' in window)) return 'unsupported';
        try { return await Notification.requestPermission(); } catch { return 'denied'; }
    }

    window.powatchCues = { play, cycle, level: () => level, notify, allowNotifications };
})();
