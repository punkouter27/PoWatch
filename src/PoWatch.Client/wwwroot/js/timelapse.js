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
  let _db = null;
  let _playing = false;
  let _videoUrl = null;

  function open() {
    return _db ??= new Promise((resolve, reject) => {
      const req = indexedDB.open('powatch-timelapse', 1);
      req.onupgradeneeded = () => req.result.createObjectStore('frames', { autoIncrement: true }).createIndex('day', 'day');
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => { _db = null; reject(req.error); };
    });
  }

  async function run(mode, work) {
    const db = await open();
    return new Promise((resolve, reject) => {
      const tx = db.transaction('frames', mode);
      const req = work(tx.objectStore('frames'));
      tx.oncomplete = () => resolve(req?.result);
      tx.onerror = () => reject(tx.error);
    });
  }

  /** The local calendar day as yyyy-MM-dd, the same key the Journal uses. */
  function localDay(date) {
    const pad = (n) => String(n).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
  }

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

  /** A day's frames, oldest first, as { at (ms since epoch), blob }; empty when none or storage is blocked. */
  async function frames(day) {
    try {
      return (await run('readonly', (store) => store.index('day').getAll(day))).sort((a, b) => a.at - b.at);
    } catch {
      return [];
    }
  }

  /**
   * Plays a day's frames onto `canvas` at `fps`, stamping each with its time, and records the
   * playback. Resolves with an object URL for the WebM (null when nothing to play, already
   * playing, or MediaRecorder is unavailable — the flipbook still plays in that case).
   */
  async function play(canvas, day, fps) {
    if (_playing || !canvas) return null;
    const shots = await frames(day);
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
    const shots = await frames(day);
    if (shots.length === 0) return false;
    // In the document but out of sight: some browsers only capture a canvas that is attached.
    return offscreen(async (canvas) => download(await record(canvas, shots, fps), `powatch-${day}.webm`));
  }

  /** When each of a day's frames was taken, as a JSON array of Unix ms (ReelPlanner.cs reads it). */
  async function times(day) {
    return JSON.stringify((await frames(day)).map((f) => f.at));
  }

  /**
   * Paints a highlight reel (a ReelPlan as JSON, see ReelPlanner.cs) from the day's frames and
   * downloads it as powatch-reel-yyyy-MM-dd.webm. Resolves true when a file was offered.
   */
  async function saveReel(day, planJson) {
    if (_playing) return false;
    const plan = JSON.parse(planJson);
    const byTime = new Map((await frames(day)).map((f) => [f.at, f.blob]));
    const shots = plan.shots.filter((s) => byTime.has(s.frameAt));
    if (shots.length === 0) return false;

    return offscreen(async (canvas) => {
      const first = await createImageBitmap(byTime.get(shots[0].frameAt));
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
          const image = await createImageBitmap(byTime.get(shot.frameAt));
          ctx.drawImage(image, 0, 0, width, height);
          image.close();

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

  /** Draws `shots` onto `canvas` one by one at `fps` and records it; resolves with the WebM blob, or null. */
  async function record(canvas, shots, fps) {
    const first = await createImageBitmap(shots[0].blob);
    canvas.width = first.width;
    canvas.height = first.height;
    first.close();
    const ctx = canvas.getContext('2d');

    return encode(canvas, fps, async (wait) => {
      const stampSize = Math.max(12, Math.round(canvas.height / 18));
      for (const frame of shots) {
        const image = await createImageBitmap(frame.blob);
        ctx.drawImage(image, 0, 0, canvas.width, canvas.height);
        image.close();
        const stamp = new Date(frame.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
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
    try {
      const type = ['video/webm;codecs=vp9', 'video/webm'].find((t) => window.MediaRecorder?.isTypeSupported(t));
      const recorder = type ? new MediaRecorder(canvas.captureStream(fps), { mimeType: type }) : null;
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
      _playing = false;
    }
  }

  /** Whether this browser can record a canvas into a WebM (the "Save video" button needs it). */
  const canRecord = () => !!(window.MediaRecorder && HTMLCanvasElement.prototype.captureStream);

  // frames() is for replay.js; .NET calls capture, count, times, play, saveVideo, saveReel and canRecord.
  window.powatchTimelapse = { capture, count, frames, times, play, saveVideo, saveReel, canRecord };
})();
