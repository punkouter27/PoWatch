// On-device time-lapse. While a camera session runs, the pixel layer hands over one frame a
// minute; it is stored as a small JPEG in IndexedDB under its local day and never leaves the
// browser. A day plays back as a flipbook on a canvas, recorded as it plays with the native
// MediaRecorder into a WebM the viewer can save. Frames older than KEEP_DAYS are pruned.
//
// The highlight reel is the same frames cut to about half a minute: ReelPlanner.cs chooses which
// frames stay, how long each is held and what is written under it; saveReel() only paints that plan
// (a title card, the frames with their time, caption and an activity strip, a closing card of the
// day's numbers) and records it the same way.
(function () {
  'use strict';

  const WIDTH = 480;
  const REEL_WIDTH = 960;      // frames are drawn at twice their stored width so the lettering stays sharp
  const TITLE_SECONDS = 2;
  const FACTS_SECONDS = 3;
  const KEEP_DAYS = 7;
  let _playing = false;
  let _videoUrl = null;

  const { run } = window.powatchIdb.store('powatch-timelapse', 'frames',
    (db) => db.createObjectStore('frames', { autoIncrement: true }).createIndex('day', 'day'));

  /** The local calendar day as yyyy-MM-dd, the same key the Journal uses. */
  const localDay = window.powatchLocalDay;

  /** Saves the current frame of `video` and prunes days past the keep window. Best-effort. */
  async function capture(video) {
    if (!video || video.readyState < 2 || !video.videoWidth) return;
    const canvas = document.createElement('canvas');
    canvas.width = Math.min(WIDTH, video.videoWidth);
    canvas.height = Math.round(canvas.width * video.videoHeight / video.videoWidth);
    canvas.getContext('2d').drawImage(video, 0, 0, canvas.width, canvas.height);
    const blob = await new Promise((resolve) => canvas.toBlob(resolve, 'image/jpeg', 0.7));
    if (!blob) return;

    const now = new Date();
    const cutoff = localDay(new Date(now.getFullYear(), now.getMonth(), now.getDate() - KEEP_DAYS + 1));
    try {
      await run('readwrite', (store) => store.add({ day: localDay(now), at: now.getTime(), blob }));
      await run('readwrite', (store) => {
        store.index('day').openCursor(IDBKeyRange.upperBound(cutoff, true)).onsuccess = (e) => {
          const cursor = e.target.result;
          if (cursor) { cursor.delete(); cursor.continue(); }
        };
      });
    } catch { /* storage full or blocked: the time-lapse is an extra, sensing carries on */ }
  }

  /** How many frames this device holds for a day (yyyy-MM-dd). */
  async function count(day) {
    try {
      return await run('readonly', (store) => store.index('day').count(day));
    } catch {
      return 0;
    }
  }

  /**
   * When a day's frames were taken, oldest first, as { at (ms since epoch), key }; empty when none or
   * storage is blocked. The pictures stay in the store: frame(key) reads one when it is needed, so a
   * day of them is never held at once.
   */
  async function stamps(day) {
    const found = [];
    try {
      await run('readonly', (store) => {
        store.index('day').openCursor(IDBKeyRange.only(day)).onsuccess = (e) => {
          const cursor = e.target.result;
          if (!cursor) return;
          found.push({ at: cursor.value.at, key: cursor.primaryKey });
          cursor.continue();
        };
      });
    } catch {
      return [];
    }
    return found.sort((a, b) => a.at - b.at);
  }

  /** One frame's JPEG (a Blob) by its key from stamps(); null when it is gone or storage is blocked. */
  async function frame(key) {
    try {
      return (await run('readonly', (store) => store.get(key)))?.blob ?? null;
    } catch {
      return null;
    }
  }

  /** A frame decoded for drawing (the caller closes it); null when it is gone. */
  async function bitmap(key) {
    const blob = await frame(key);
    return blob ? createImageBitmap(blob) : null;
  }

  /**
   * Plays a day's frames onto `canvas` at `fps`, stamping each with its time, and records the
   * playback. Resolves with an object URL for the WebM (null when nothing to play, already
   * playing, or MediaRecorder is unavailable — the flipbook still plays in that case).
   */
  async function play(canvas, day, fps) {
    if (_playing || !canvas) return null;
    const shots = await stamps(day);
    if (shots.length === 0) return null;
    const video = await record(canvas, shots, fps);
    if (!video) return null;
    if (_videoUrl) URL.revokeObjectURL(_videoUrl);
    _videoUrl = URL.createObjectURL(video);
    return _videoUrl;
  }

  /**
   * Encodes a day's frames into a WebM and downloads it as powatch-yyyy-MM-dd.webm, without
   * showing the flipbook (an off-screen canvas). Resolves true when a file was offered.
   */
  async function saveVideo(day, fps) {
    if (_playing) return false;
    const shots = await stamps(day);
    if (shots.length === 0) return false;
    // In the document but out of sight: some browsers only capture a canvas that is attached.
    return offscreen(async (canvas) => download(await record(canvas, shots, fps), `powatch-${day}.webm`));
  }

  /** When each of a day's frames was taken, as a JSON array of Unix ms (ReelPlanner.cs reads it). */
  async function times(day) {
    return JSON.stringify((await stamps(day)).map((f) => f.at));
  }

  /**
   * Paints a highlight reel (a ReelPlan as JSON, see ReelPlanner.cs) from the day's frames and
   * downloads it as powatch-reel-yyyy-MM-dd.webm. Resolves true when a file was offered.
   */
  async function saveReel(day, planJson) {
    if (_playing) return false;
    const plan = JSON.parse(planJson);
    const byTime = new Map((await stamps(day)).map((f) => [f.at, f.key]));
    const shots = plan.shots.filter((s) => byTime.has(s.frameAt));
    if (shots.length === 0) return false;

    return offscreen(async (canvas) => {
      const first = await bitmap(byTime.get(shots[0].frameAt));
      if (!first) return false;
      canvas.width = REEL_WIDTH;
      canvas.height = Math.round(REEL_WIDTH * first.height / first.width / 2) * 2;
      first.close();
      const ctx = canvas.getContext('2d');
      const { width, height } = canvas;
      const unit = height / 18;
      const mono = (size, weight = 600) => `${weight} ${Math.round(size)}px ui-monospace, monospace`;

      const card = (heading, lines) => {
        ctx.fillStyle = '#0b1220';
        ctx.fillRect(0, 0, width, height);
        ctx.textAlign = 'center';
        ctx.fillStyle = '#f5a524';
        ctx.font = mono(unit * 0.8);
        ctx.fillText('POWATCH', width / 2, height * 0.3);
        ctx.fillStyle = '#e8eef7';
        ctx.font = mono(unit * 1.3);
        ctx.fillText(heading, width / 2, height * 0.3 + unit * 2.2, width - unit * 2);
        ctx.fillStyle = '#9fb0c6';
        ctx.font = mono(unit * 0.85, 400);
        lines.forEach((line, i) => ctx.fillText(line, width / 2, height * 0.3 + unit * (4.2 + i * 1.4), width - unit * 2));
        ctx.textAlign = 'left';
      };

      const video = await encode(canvas, plan.fps, async (wait) => {
        card(plan.title, ['the highlights']);
        await wait(TITLE_SECONDS * plan.fps);

        const strip = unit * 1.2;       // the activity strip along the bottom edge
        for (let i = 0; i < shots.length; i++) {
          const shot = shots[i];
          // A frame pruned since the plan was made leaves the one before it on screen under this shot's text.
          const image = await bitmap(byTime.get(shot.frameAt));
          if (image) {
            ctx.drawImage(image, 0, 0, width, height);
            image.close();
          }

          const stamp = (shot.star ? '\u2605 ' : '') + new Date(shot.frameAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
          ctx.font = mono(unit);
          ctx.fillStyle = 'rgba(0, 0, 0, 0.55)';
          ctx.fillRect(unit / 2, unit / 2, ctx.measureText(stamp).width + unit, unit * 1.7);
          ctx.fillStyle = '#f5a524';
          ctx.fillText(stamp, unit, unit * 1.75);

          if (shot.text) {
            ctx.font = mono(unit * 0.8, shot.star ? 600 : 400);
            const lines = wrap(ctx, shot.text, width - unit * 2).slice(0, 2);
            const top = height - strip - unit * (0.6 + lines.length * 1.15);
            ctx.fillStyle = 'rgba(0, 0, 0, 0.6)';
            ctx.fillRect(0, top, width, height - strip - top);
            ctx.fillStyle = shot.star ? '#f5a524' : '#e8eef7';
            lines.forEach((line, n) => ctx.fillText(line, unit, top + unit * (1.05 + n * 1.15)));
          }

          // Every shot as a bar as tall as its minute was busy; the ones already shown are lit.
          ctx.fillStyle = 'rgba(0, 0, 0, 0.7)';
          ctx.fillRect(0, height - strip, width, strip);
          const bar = width / shots.length;
          for (let n = 0; n < shots.length; n++) {
            const tall = Math.max(2, shots[n].level * (strip - 4));
            ctx.fillStyle = n <= i ? '#f5a524' : 'rgba(159, 176, 198, 0.45)';
            ctx.fillRect(n * bar, height - tall, Math.max(1, bar - 1), tall);
          }

          await wait(shot.hold);
        }

        if (plan.facts.length) {
          card(plan.title, plan.facts);
          await wait(FACTS_SECONDS * plan.fps);
        }
      });
      return download(video, `powatch-reel-${day}.webm`);
    });
  }

  /** Breaks `text` into lines no wider than `max` in the context's current font. */
  function wrap(ctx, text, max) {
    const lines = [];
    let line = '';
    for (const word of String(text).split(/\s+/)) {
      const next = line ? `${line} ${word}` : word;
      if (line && ctx.measureText(next).width > max) { lines.push(line); line = word; }
      else line = next;
    }
    if (line) lines.push(line);
    return lines;
  }

  /** Runs `work` with a canvas that is in the document but out of sight: some browsers only capture an attached one. */
  async function offscreen(work) {
    const canvas = document.createElement('canvas');
    canvas.style.cssText = 'position:fixed;left:-10000px;top:0;pointer-events:none';
    document.body.appendChild(canvas);
    try {
      return await work(canvas);
    } finally {
      canvas.remove();
    }
  }

  /** Offers `video` (a Blob, or null) as a download; true when there was one. */
  function download(video, name) {
    if (!video) return false;
    const url = URL.createObjectURL(video);
    const link = document.createElement('a');
    link.href = url;
    link.download = name;
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 60000);
    return true;
  }

  /** Draws `shots` (from stamps()) onto `canvas` one by one at `fps` and records it; resolves with the WebM blob, or null. */
  async function record(canvas, shots, fps) {
    const first = await bitmap(shots[0].key);
    if (!first) return null;
    canvas.width = first.width;
    canvas.height = first.height;
    first.close();
    const ctx = canvas.getContext('2d');

    return encode(canvas, fps, async (wait) => {
      const stampSize = Math.max(12, Math.round(canvas.height / 18));
      for (const shot of shots) {
        const image = await bitmap(shot.key);
        if (!image) continue; // pruned while this played
        ctx.drawImage(image, 0, 0, canvas.width, canvas.height);
        image.close();
        const stamp = new Date(shot.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        ctx.font = `600 ${stampSize}px ui-monospace, monospace`;
        ctx.fillStyle = 'rgba(0, 0, 0, 0.55)';
        ctx.fillRect(8, 8, ctx.measureText(stamp).width + 16, stampSize + 12);
        ctx.fillStyle = '#f5a524';
        ctx.fillText(stamp, 16, 8 + stampSize);
        await wait(1);
      }
    });
  }

  /**
   * Records `canvas` (already sized) while `paint` draws on it; `paint` is handed wait(n), which holds
   * what is drawn for n video frames. Resolves with the WebM blob, or null without MediaRecorder
   * (the drawing still plays).
   */
  async function encode(canvas, fps, paint) {
    _playing = true;
    let stream = null;
    let recorder = null;
    try {
      const type = ['video/webm;codecs=vp9', 'video/webm'].find((t) => window.MediaRecorder?.isTypeSupported(t));
      stream = type ? canvas.captureStream(fps) : null;
      recorder = stream ? new MediaRecorder(stream, { mimeType: type }) : null;
      const chunks = [];
      if (recorder) {
        recorder.ondataavailable = (e) => { if (e.data.size) chunks.push(e.data); };
        recorder.start();
      }

      await paint((n) => new Promise((resolve) => setTimeout(resolve, n * 1000 / fps)));

      if (!recorder) return null;
      await new Promise((resolve) => { recorder.onstop = resolve; recorder.stop(); });
      return new Blob(chunks, { type: 'video/webm' });
    } finally {
      // A paint that threw leaves the recorder running; either way the canvas's capture ends here.
      if (recorder && recorder.state !== 'inactive') recorder.stop();
      stream?.getTracks().forEach((track) => track.stop());
      _playing = false;
    }
  }

  /** Whether this browser can record a canvas into a WebM (the "Save video" button needs it). */
  const canRecord = () => !!(window.MediaRecorder && HTMLCanvasElement.prototype.captureStream);

  // stamps() and frame() are for replay.js; .NET calls capture, count, times, play, saveVideo, saveReel and canRecord.
  window.powatchTimelapse = { capture, count, stamps, frame, times, play, saveVideo, saveReel, canRecord };
})();
