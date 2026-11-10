// The microphone, for a camera session. Every second it reports how loud the room was (for the
// 10-second ticks); a stretch of speech (loud, then quiet for most of a second) goes to Whisper and comes
// back as words; a loud moment that is not speech goes to the sound classifier and comes back as a label.
// All of it runs in audio-worker.js on this machine. The audio itself is never stored or sent: only the
// loudness, the words (with a voice embedding, matched to a regular in the browser) and the labels reach
// SensingSession (OnSoundLevel, OnHeard, OnSound).
(function () {
  'use strict';

  const RATE = 16000;
  const SPEECH_ON = 0.42;         // about −35 dBFS on the 0..1 scale below
  const SPEECH_END_MS = 800;      // this much quiet ends a stretch of speech
  const SPEECH_MIN_MS = 600;
  const SPEECH_MAX_MS = 15000;
  const PRE_ROLL_MS = 300;        // the start of a word comes before it is loud enough to notice
  const SOUND_EVERY_MS = 3000;    // at most one sound label every three seconds
  const SOUND_WINDOW_MS = 1500;
  const SOUND_MIN_SCORE = 0.3;
  const SOUND_REPEAT_MS = 120000; // the same label again within two minutes is the same sound going on
  // AudioSet's labels for speech, silence and the room itself say nothing a story needs; generic tones
  // ("sound effect", "sine wave") say nothing either, and body sounds read as medical, which stories avoid.
  const IGNORED = /^(speech|male speech|female speech|child speech|conversation|narration|silence|inside, small room|inside, large room|inside, public space|outside|white noise|pink noise|hum|static|noise|environmental noise|mains hum|sound effect|sine wave|tone|heart murmur|heart sounds|breathing|wheeze|gasp|cough|sneeze|snoring)/i;

  const call = window.powatchWorkerRpc('/js/audio-worker.js?v=20261109-loadstatus', 'hearing');
  let state = null;

  /** 0 at −60 dBFS or below, 1 at full scale. */
  const level = (rms) => Math.max(0, Math.min(1, (20 * Math.log10(Math.max(rms, 1e-6)) + 60) / 60));

  function concat(chunks) {
    const total = chunks.reduce((n, c) => n + c.length, 0);
    const out = new Float32Array(total);
    let at = 0;
    for (const c of chunks) { out.set(c, at); at += c.length; }
    return out;
  }

  async function transcribe(samples, atMs) {
    try {
      const { text, voice } = await call({ type: 'transcribe', samples }, [samples.buffer], 60000);
      if (text && state) await state.dotnet.invokeMethodAsync('OnHeard', new Date(atMs).toISOString(), text, voice ? JSON.stringify(voice) : null);
    } catch { /* a missed line is better than a stalled microphone */ }
  }

  async function classify(samples, atMs) {
    state.classifying = true;
    try {
      const { labels } = await call({ type: 'classify', samples }, [samples.buffer], 30000);
      const best = (labels ?? []).find((l) => l.score >= SOUND_MIN_SCORE && !IGNORED.test(l.label));
      if (best && state && (state.lastLabel !== best.label || atMs - state.lastLabelAt > SOUND_REPEAT_MS)) {
        state.lastLabel = best.label;
        state.lastLabelAt = atMs;
        await state.dotnet.invokeMethodAsync('OnSound', new Date(atMs).toISOString(), best.label, best.score);
      }
    } catch { /* the classifier is optional */ }
    finally { if (state) state.classifying = false; }
  }

  function onChunk(chunk) {
    const s = state;
    if (!s) return;
    const now = Date.now();
    let sum = 0;
    for (let i = 0; i < chunk.length; i++) sum += chunk[i] * chunk[i];
    const loud = level(Math.sqrt(sum / chunk.length));
    s.levels.push(loud);

    // The last second and a half, for the sound classifier and the speech pre-roll.
    s.recent.push(chunk);
    s.recentSamples += chunk.length;
    while (s.recentSamples - s.recent[0].length >= RATE * SOUND_WINDOW_MS / 1000) s.recentSamples -= s.recent.shift().length;

    if (s.speech) {
      s.speech.chunks.push(chunk);
      if (loud >= SPEECH_ON) s.speech.lastLoud = now;
      const length = now - s.speech.startMs;
      if (now - s.speech.lastLoud >= SPEECH_END_MS || length >= SPEECH_MAX_MS) {
        const done = s.speech;
        s.speech = null;
        if (done.lastLoud - done.startMs >= SPEECH_MIN_MS) transcribe(concat(done.chunks), done.startMs);
      }
    } else if (loud >= SPEECH_ON) {
      const preRoll = Math.ceil(PRE_ROLL_MS / (1000 * chunk.length / RATE));
      s.speech = { startMs: now, lastLoud: now, chunks: s.recent.slice(-preRoll) };
    }

    if (loud >= SPEECH_ON && !s.classifying && now - s.lastClassify >= SOUND_EVERY_MS) {
      s.lastClassify = now;
      classify(concat(s.recent), now);
    }
  }

  function report() {
    const s = state;
    if (!s || s.levels.length === 0) return;
    const mean = s.levels.reduce((a, b) => a + b, 0) / s.levels.length;
    const max = Math.max(...s.levels);
    s.levels = [];
    s.dotnet.invokeMethodAsync('OnSoundLevel', mean, max).catch(() => { });
  }

  /** Opens the microphone; resolves to 'OK' or a reason it could not listen. */
  async function start(dotnet) {
    if (state) return 'OK';
    let stream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } });
    } catch (err) {
      return err?.name === 'NotAllowedError' ? 'Microphone access was not allowed.' : 'No microphone could be opened.';
    }
    const context = new AudioContext({ sampleRate: RATE });
    const source = context.createMediaStreamSource(stream);
    // A ScriptProcessor keeps this one file; 4096 samples is a quarter of a second at 16 kHz.
    const processor = context.createScriptProcessor(4096, 1, 1);
    processor.onaudioprocess = (e) => onChunk(new Float32Array(e.inputBuffer.getChannelData(0)));
    source.connect(processor);
    processor.connect(context.destination);
    state = {
      dotnet, stream, context, processor, levels: [], recent: [], recentSamples: 0, speech: null,
      classifying: false, lastClassify: 0, lastLabel: null, lastLabelAt: 0,
      timer: setInterval(report, 1000),
    };
    // Load both models now, while the room is quiet, rather than on the first word.
    call({ type: 'warm' }).catch(() => { });
    return 'OK';
  }

  function stop() {
    const s = state;
    if (!s) return;
    state = null;
    clearInterval(s.timer);
    s.processor.disconnect();
    s.stream.getTracks().forEach((t) => t.stop());
    s.context.close().catch(() => { });
  }

  window.powatchAudio = { start, stop };
})();
