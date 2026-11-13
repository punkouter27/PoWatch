// The caption index: every caption this browser wrote or replayed, with its sentence embedding, so the
// command palette can find "the cat on the couch" by meaning rather than by exact words. It lives in
// IndexedDB on this device only — captions are already on the server, but their vectors never leave
// here. The caption worker embeds new captions as it writes them; replayed days are embedded in the
// background by embed-worker.js. Searching is a brute-force dot product (vectors are unit length),
// which over the capped store takes a few milliseconds.
(function () {
  'use strict';

  const MAX = 20000;           // about a month of captions; past it the oldest go
  const MIN_SCORE = 0.3;       // MiniLM cosine below this is unrelated text
  const CHUNK = 32;            // texts per embedding call
  const call = window.powatchWorkerRpc('embed-worker.js');
  let _puts = 0;

  const { run } = window.powatchIdb.store('powatch-captions', 'captions',
    (db) => db.createObjectStore('captions', { keyPath: 'key' }).createIndex('at', 'at'));

  const keyOf = (at, text) => `${at}|${text}`;

  async function prune() {
    const count = await run('readonly', (s) => s.count());
    if (count <= MAX) return;
    let left = count - MAX;
    await run('readwrite', (s) => {
      s.index('at').openCursor().onsuccess = (e) => {
        const cursor = e.target.result;
        if (!cursor || left-- <= 0) return;
        cursor.delete();
        cursor.continue();
      };
    });
  }

  /** Stores one caption with its vector (from the caption worker). Never throws. */
  async function put(at, text, vector) {
    if (!text || !vector?.length) return;
    try {
      await run('readwrite', (s) => s.put({ key: keyOf(at, text), at, text, vec: Float32Array.from(vector) }));
      if (++_puts % 100 === 0) await prune();
    } catch {
      // Blocked storage: search simply knows less.
    }
  }

  /** Indexes captions not yet in the store ([{ at, text }], e.g. a replayed day). Never throws. */
  async function addMany(items) {
    try {
      const have = new Set(await run('readonly', (s) => s.getAllKeys()));
      const missing = items.filter((c) => c.text && !have.has(keyOf(c.at, c.text)));
      for (let i = 0; i < missing.length; i += CHUNK) {
        const chunk = missing.slice(i, i + CHUNK);
        const { vectors } = await call({ texts: chunk.map((c) => c.text) });
        await run('readwrite', (s) => {
          let last = null;
          chunk.forEach((c, j) => { last = s.put({ key: keyOf(c.at, c.text), at: c.at, text: c.text, vec: Float32Array.from(vectors[j]) }); });
          return last;
        });
      }
      if (missing.length > 0) await prune();
      return missing.length;
    } catch {
      return 0;
    }
  }

  /**
   * The captions closest in meaning to `query`, best first: [{ at, text, score, times }]. The same
   * sentence written many times is one result (its newest time, and how often it was written).
   */
  async function search(query, k = 12) {
    const { vectors } = await call({ texts: [query] });
    const q = vectors[0];
    const rows = await run('readonly', (s) => s.getAll());
    const best = new Map();
    for (const row of rows) {
      let dot = 0;
      for (let i = 0; i < q.length; i++) dot += q[i] * row.vec[i];
      if (dot < MIN_SCORE) continue;
      const seen = best.get(row.text);
      if (!seen) best.set(row.text, { at: row.at, text: row.text, score: dot, times: 1 });
      else { seen.times++; seen.at = Math.max(seen.at, row.at); }
    }
    return [...best.values()].sort((a, b) => b.score - a.score).slice(0, k);
  }

  window.powatchCaptionIndex = { put, addMany, search };
})();
