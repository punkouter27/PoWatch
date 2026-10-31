// Sound cues and desktop notifications. Every sound is synthesised with the Web Audio API (no audio
// files): short oscillator notes with a gain envelope, panned left or right to where in the frame the
// thing happened, plus a faint hum while a session runs. The level (off, low, mid, high) is remembered in this browser.
(function () {
    'use strict';

    const KEY = 'powatch:sound';
    const LEVELS = { off: 0, low: 0.3, mid: 0.6, high: 1 };
    const ORDER = ['off', 'low', 'mid', 'high'];

    // Each note: [frequency Hz, starts at s, lasts s, waveform, glides to Hz?, loudness ×?].
    // Start and stop are the tube switching on and off (crt.js): a thump and a glide up, a glide down and a thump.
    const CUES = {
        start: [[62, 0, 0.12, 'triangle'], [320, 0.03, 0.42, 'sine', 1900, 0.45], [880, 0.45, 0.16, 'sine']],
        stop: [[1900, 0, 0.36, 'sine', 240, 0.45], [62, 0.34, 0.14, 'triangle']],
        enter: [[880, 0, 0.06, 'sine']],
        // Something entering, by kind: a rising two-note for a person, a bark-like drop for a dog, a
        // mew that bends up and back for a cat, a quick trill for other animals, a dull tick for objects.
        'enter:person': [[660, 0, 0.06, 'sine'], [990, 0.05, 0.08, 'sine']],
        'enter:dog': [[520, 0, 0.09, 'triangle', 260]],
        'enter:cat': [[900, 0, 0.07, 'sine', 1350], [1350, 0.07, 0.09, 'sine', 820, 0.8]],
        'enter:animal': [[1500, 0, 0.035, 'sine'], [1750, 0.04, 0.035, 'sine'], [1500, 0.08, 0.04, 'sine']],
        'enter:object': [[420, 0, 0.04, 'triangle', null, 0.5]],
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

    /** Plays a cue; `pan` runs from -1 (left edge of the frame) to 1 (right edge). */
    function play(name, pan) {
        if (name === 'enter') return enterLater(pan);
        if (name === 'enter:object') {
            // Cups and chairs flicker in and out of detection: at most one tick every few seconds.
            const now = performance.now();
            if (now - lastObject < OBJECT_GAP_MS) return;
            lastObject = now;
        }
        const notes = CUES[name];
        if (notes) sound(notes, pan);
    }

    // ── Entries by kind ──────────────────────────────────────────────────────
    // The session plays "enter" with the side something came in from, and the same moment AmbientBridge
    // tells `entered` what came in; the two are paired here a few milliseconds later. An entry nobody
    // named plays the plain blip.

    const OBJECT_GAP_MS = 4000;
    let lastObject = -Infinity;
    const pendingPans = [];
    const pendingKinds = [];
    let pairTimer = 0;

    function enterLater(pan) {
        pendingPans.push(pan);
        pairTimer ||= setTimeout(pair, 60);
    }

    /** What just entered, as comma-separated labels ("person,dog"), in the order the session announced them. */
    function entered(labels) {
        pendingKinds.push(...String(labels || '').split(',').filter(Boolean));
        pairTimer ||= setTimeout(pair, 60);
    }

    function pair() {
        pairTimer = 0;
        for (const pan of pendingPans.splice(0)) {
            const label = pendingKinds.shift();
            sound(CUES[label ? kindCue(label) : 'enter'], pan);
        }
        pendingKinds.length = 0;
    }

    const kindCue = (label) => {
        const l = label.toLowerCase();
        return l === 'person' || l === 'dog' || l === 'cat' ? `enter:${l}` : 'enter:animal';
    };

    // ── Ambient hum ──────────────────────────────────────────────────────────
    // Two detuned sines under a low-pass, barely audible, that rise half a semitone and a little louder
    // for each thing in frame (up to eight). Off with the sound; stopped when the session stops.

    const HUM_HZ = 110;
    let hum = null;
    let humCount = -1;

    /** Sets the hum for `count` things in frame; a negative count (the session stopped) fades it out. */
    function ambient(count) {
        humCount = Number(count);
        if (!(humCount >= 0)) humCount = -1;
        const volume = LEVELS[level];
        if (humCount < 0 || !volume) return humStop();
        try {
            ctx ??= new AudioContext();
            if (ctx.state === 'suspended') ctx.resume();
            if (!hum) {
                const gain = ctx.createGain();
                const filter = ctx.createBiquadFilter();
                filter.type = 'lowpass';
                filter.frequency.value = 480;
                gain.gain.value = 0.0001;
                filter.connect(gain).connect(ctx.destination);
                const oscs = [0, 7].map((detune) => {
                    const osc = ctx.createOscillator();
                    osc.type = 'sine';
                    osc.frequency.value = HUM_HZ;
                    osc.detune.value = detune;
                    osc.connect(filter);
                    osc.start();
                    return osc;
                });
                hum = { gain, oscs };
            }
            const n = Math.min(8, humCount);
            const t = ctx.currentTime;
            for (const osc of hum.oscs) osc.frequency.setTargetAtTime(HUM_HZ * Math.pow(2, n / 24), t, 1.5);
            hum.gain.gain.setTargetAtTime(0.012 * volume * (0.35 + 0.65 * (n / 8)), t, 1.5);
        } catch {
            hum = null;
        }
    }

    function humStop() {
        if (!hum) return;
        const { gain, oscs } = hum;
        hum = null;
        try {
            gain.gain.setTargetAtTime(0.0001, ctx.currentTime, 0.4);
            for (const osc of oscs) osc.stop(ctx.currentTime + 2);
        } catch { /* already gone */ }
    }

    /** One soft note at `frequency` Hz (the skyline's fly-over plays the year with these). */
    function tone(frequency, seconds, pan) {
        sound([[Number(frequency) || 440, 0, Math.min(2, Number(seconds) || 0.2), 'sine', null, 0.6]], pan);
    }

    function sound(notes, pan) {
        const volume = LEVELS[level];
        if (!volume) return;
        try {
            ctx ??= new AudioContext();
            if (ctx.state === 'suspended') ctx.resume();
            const out = ctx.createStereoPanner();
            out.pan.value = Math.max(-1, Math.min(1, Number(pan) || 0));
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
                gain.gain.exponentialRampToValueAtTime(0.18 * volume * (loudness ?? 1), from + 0.012);
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
        if (humCount >= 0) ambient(humCount);
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

    window.powatchCues = { play, tone, entered, ambient, cycle, level: () => level, notify, allowNotifications };
})();
