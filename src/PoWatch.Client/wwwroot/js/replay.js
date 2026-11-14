// The Journal's replay: scrub or play through a day (or one session of it) minute by minute. The track shows the whole day
// (occupancy as amber bars, motion as a line, moments as ticks, captions as dots); the playhead drives
// the motion grid (heat-gl.js), the readout, the caption of the moment, and, where this device kept a
// time-lapse, the camera frame nearest that minute. The slider is a native range input, so arrow keys,
// Home/End and screen readers work without extra code, and menus.js leaves its keys alone. The grids
// arrive as raw bytes through stage() (TimelineReplay → replay-grids.js) before mount; base64 is the fallback.
// The timeline under it ([data-replay-lines]) follows along: the line the playhead is on is lit and kept in
// view, and a line (data-replay-jump) moves the playhead to its moment.
(function () {
    'use strict';

    const GRID = 144;
    const CAPTION_HOLD_MS = 15 * 60 * 1000; // a caption describes the scene for a while after it was written
    const FRAME_NEAR_MS = 5 * 60 * 1000;    // a time-lapse frame stands for the minutes around it
    const PLAY_STEP_MS = 70;
    const mounted = new WeakMap();
    const staged = new Map(); // day → Uint8Array of grids handed over by .NET before mount

    /** Keeps a day's grids (144 bytes a minute, in minute order) for the next mount of that day. */
    function stage(day, grids) {
        staged.set(day, grids);
    }

    function decode(base64) {
        const raw = atob(base64 || '');
        const bytes = new Uint8Array(raw.length);
        for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
        return bytes;
    }

    function color(el, variable, fallback) {
        return getComputedStyle(el).getPropertyValue(variable).trim() || fallback;
    }

    /** Index of the observed minute at `at`, or -1 when nothing was observed then. */
    function minuteAt(minutes, at) {
        let lo = 0, hi = minutes.length - 1;
        while (lo <= hi) {
            const mid = (lo + hi) >> 1;
            const m = minutes[mid].at;
            if (at < m) hi = mid - 1;
            else if (at >= m + 60000) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    /** `startAt` (Unix ms, optional) opens the replay on that minute — a caption search result. */
    function mount(root, json, day, startAt) {
        unmount(root);
        // Taken whether or not this mount goes ahead, so a day that never mounts does not stay staged.
        const stagedGrids = staged.get(day);
        staged.delete(day);
        if (!root) return;
        const data = JSON.parse(json);
        const $ = (name) => root.querySelector(`[data-replay="${name}"]`);
        const track = $('track'), scrub = $('scrub'), heat = $('heat'), clock = $('clock'), readout = $('stats'),
            caption = $('caption'), playButton = $('play'), frameImg = $('frame'), cameraBox = $('camera-box');
        if (!track || !scrub || !heat) return;

        const from = Date.parse(data.window.fromUtc);
        const to = Date.parse(data.window.toUtc);
        const minutes = data.minutes.map((m) => ({ at: Date.parse(m.atUtc), occupancy: m.occupancy, peak: m.peak, motion: m.motion }));
        const grids = stagedGrids ?? decode(data.grids);
        const events = data.events.map((e) => ({ kind: e.kind, text: e.text, cls: e.class, at: Date.parse(e.atUtc) }));
        const captions = events.filter((e) => e.kind === 'caption');
        const moments = events.filter((e) => e.kind === 'moment');
        const maxMotion = Math.max(0.0001, ...minutes.map((m) => m.motion));
        const empty = new Array(GRID).fill(0);

        const state = { at: startAt >= from && startAt < to ? startAt : minutes.length ? minutes[0].at : from, timer: 0, frames: [], frameUrl: null, colors: null, observer: null, theme: null, detach: null, line: null, heat };
        mounted.set(root, state);

        scrub.min = '0';
        scrub.max = String(Math.max(0, Math.round((to - from) / 60000) - 1));
        scrub.step = '1';

        const readColors = () => {
            state.colors = {
                amber: color(track, '--term-accent', '#8fdcff'),
                cyan: color(track, '--term-cyan', '#5b9cff'),
                line: color(track, '--term-line', '#262d38'),
                text: color(track, '--term-text', '#e6e9ee'),
                bg: color(track, '--term-bg', '#0d1014'),
            };
        };

        function drawTrack() {
            const dpr = Math.min(devicePixelRatio || 1, 2);
            const w = Math.max(1, Math.round(track.clientWidth * dpr));
            const h = Math.max(1, Math.round(track.clientHeight * dpr));
            if (track.width !== w || track.height !== h) { track.width = w; track.height = h; }
            const ctx = track.getContext('2d');
            const c = state.colors;
            const x = (at) => ((at - from) / (to - from)) * w;
            const base = h - 4 * dpr, top = 10 * dpr, span = base - top;

            ctx.clearRect(0, 0, w, h);
            ctx.fillStyle = c.line;
            ctx.fillRect(0, base, w, dpr);
            // Hour ticks.
            for (let t = Math.ceil(from / 3600000) * 3600000; t < to; t += 3600000) ctx.fillRect(Math.round(x(t)), base, dpr, 3 * dpr);

            const barW = Math.max(dpr, w / ((to - from) / 60000));
            ctx.fillStyle = c.amber;
            ctx.globalAlpha = 0.85;
            for (const m of minutes) {
                const bh = Math.max(dpr, m.occupancy * span);
                ctx.fillRect(x(m.at), base - bh, barW, bh);
            }
            ctx.globalAlpha = 1;

            ctx.strokeStyle = c.cyan;
            ctx.lineWidth = dpr;
            ctx.beginPath();
            let previous = null;
            for (const m of minutes) {
                const px = x(m.at) + barW / 2, py = base - (m.motion / maxMotion) * span;
                if (previous !== null && m.at - previous > 120000) ctx.moveTo(px, py); else ctx.lineTo(px, py);
                previous = m.at;
            }
            ctx.stroke();

            for (const e of events) {
                if (e.kind === 'moment') { ctx.fillStyle = c.text; ctx.fillRect(Math.round(x(e.at)), 0, 2 * dpr, 7 * dpr); }
                else if (e.kind === 'caption') { ctx.fillStyle = c.cyan; ctx.fillRect(Math.round(x(e.at)), 2 * dpr, dpr * 2, dpr * 2); }
            }

            ctx.fillStyle = c.text;
            ctx.fillRect(Math.round(x(state.at)) - dpr / 2, 0, dpr * 2, h);
        }

        // state.frames holds only when each frame was taken; the picture nearest the playhead is read
        // from the time-lapse store when it is wanted, so a day of JPEGs is never held here.
        async function showFrame() {
            if (!cameraBox || !frameImg || state.frames.length === 0) return;
            let best = null;
            for (const f of state.frames) if (!best || Math.abs(f.at - state.at) < Math.abs(best.at - state.at)) best = f;
            const near = best && Math.abs(best.at - state.at) <= FRAME_NEAR_MS;
            cameraBox.hidden = !near;
            if (!near || frameImg.dataset.at === String(best.at)) return;
            // Claimed before the read, so the minutes that pass while it loads do not ask for it again.
            frameImg.dataset.at = String(best.at);
            const blob = await window.powatchTimelapse.frame(best.key);
            // Unmounted, or the playhead has moved on to another frame, while this one was read.
            if (!blob || mounted.get(root) !== state || frameImg.dataset.at !== String(best.at)) return;
            if (state.frameUrl) URL.revokeObjectURL(state.frameUrl);
            state.frameUrl = URL.createObjectURL(blob);
            frameImg.src = state.frameUrl;
        }

        function render() {
            const i = minuteAt(minutes, state.at);
            const time = new Date(state.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
            if (clock) clock.textContent = time;
            if (readout) {
                readout.textContent = i < 0
                    ? 'Nothing observed this minute'
                    : `${Math.round(minutes[i].occupancy * 100)}% occupied · ${minutes[i].peak} at once · motion ${minutes[i].motion.toFixed(3)}`;
            }
            window.powatchHeat?.draw(heat, i < 0 ? empty : grids.subarray(i * GRID, (i + 1) * GRID), 16, 9);

            let said = null;
            for (const e of captions) { if (e.at <= state.at + 60000) said = e; else break; }
            if (caption) caption.textContent = said && state.at - said.at <= CAPTION_HOLD_MS ? `“${said.text}”` : '';
            showFrame();
            drawTrack();
            followLines();
        }

        // Lights the last timeline line at or before the playhead's minute, and scrolls only the list to it.
        function followLines() {
            const list = root.querySelector('[data-replay-lines]');
            // On a whole minute (scrub, play) the minute's lines count; on a line's own instant (a click), up to it.
            const until = state.at % 60000 === 0 ? state.at + 60000 : state.at + 1;
            let current = null;
            for (const jump of list?.querySelectorAll('[data-replay-jump]') ?? []) {
                if (Number(jump.dataset.replayJump) < until) current = jump.closest('li'); else break;
            }
            if (current === state.line && current?.isConnected) return;
            state.line?.classList.remove('is-now');
            state.line = current;
            if (!current) return;
            current.classList.add('is-now');
            const box = list.getBoundingClientRect(), row = current.getBoundingClientRect();
            if (row.top < box.top || row.bottom > box.bottom) list.scrollTop += row.top - box.top - box.height / 3;
        }

        function seek(at, fromSlider) {
            const previous = state.at;
            state.at = Math.min(Math.max(at, from), to - 60000);
            if (!fromSlider) scrub.value = String(Math.round((state.at - from) / 60000));
            render();
            if (state.at !== previous && !state.timer) {
                window.powatchVisual?.animate(heat, [{ clipPath: 'inset(0 100% 0 0)' }, { clipPath: 'inset(0 0 0 0)' }], { duration: 180, easing: 'ease-out' });
                const moment = moments.find(e => e.at >= state.at && e.at < state.at + 60000);
                if (moment) {
                    const seed = `${day}:${moment.at}:${moment.text}`;
                    window.powatchFx?.burst(0, seed, heat);
                    window.powatchCues?.play('regular', 0, 0.5, 0.4, { seed, room: true });
                }
            }
        }

        function stop() {
            clearInterval(state.timer);
            state.timer = 0;
            if (playButton) playButton.textContent = '▶ Play';
        }

        // Play walks the observed minutes only, so an empty afternoon does not take a minute of nothing.
        function play() {
            if (state.timer) { stop(); return; }
            if (minutes.length === 0) return;
            let i = minuteAt(minutes, state.at);
            if (i < 0 || i >= minutes.length - 1) i = minutes.findIndex((m) => m.at > state.at);
            if (i < 0 || i >= minutes.length - 1) i = 0;
            if (playButton) playButton.textContent = '❚❚ Pause';
            state.timer = setInterval(() => {
                if (++i >= minutes.length) { stop(); return; }
                seek(minutes[i].at, false);
            }, PLAY_STEP_MS);
        }

        // Remembered so unmount can take them off again: the root element outlives a change of day.
        const onScrub = () => { stop(); seek(from + Number(scrub.value) * 60000, true); };
        const onJump = (e) => {
            const jump = e.target.closest?.('[data-replay-jump]');
            if (jump) { stop(); seek(Number(jump.dataset.replayJump), false); scrub.focus({ preventScroll: true }); }
        };
        scrub.addEventListener('input', onScrub);
        playButton?.addEventListener('click', play);
        root.addEventListener('click', onJump);
        state.detach = () => {
            scrub.removeEventListener('input', onScrub);
            playButton?.removeEventListener('click', play);
            root.removeEventListener('click', onJump);
        };

        readColors();
        state.observer = new ResizeObserver(() => drawTrack());
        state.observer.observe(track);
        // The header's theme toggle changes the palette under an open replay.
        state.theme = new MutationObserver(() => { readColors(); drawTrack(); });
        state.theme.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
        seek(state.at, false);

        // The day's captions become searchable by meaning from the palette (in the background, once).
        window.powatchCaptionIndex?.addMany(captions.map((c) => ({ at: c.at, text: c.text })));

        // This device's time-lapse for the day, if it kept one; the replay works without it.
        window.powatchTimelapse?.stamps(day).then((frames) => {
            if (mounted.get(root) !== state) return;
            state.frames = frames;
            showFrame();
        });
    }

    function unmount(root) {
        const state = root && mounted.get(root);
        if (!state) return;
        clearInterval(state.timer);
        state.detach?.();
        state.observer?.disconnect();
        state.theme?.disconnect();
        window.powatchHeat?.release?.(state.heat);
        if (state.frameUrl) URL.revokeObjectURL(state.frameUrl);
        mounted.delete(root);
    }

    window.powatchReplay = { mount, unmount, stage };
})();
