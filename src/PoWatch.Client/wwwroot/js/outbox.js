// The outbox: every ingest batch, from every session, waits here until the server answers for it.
// It is the only send queue (SensingSession posts from it, oldest first), and it is durable: kept in
// IndexedDB, so a reload, a crash or a browser restart loses nothing. Where IndexedDB is blocked
// (private windows, locked-down storage) it falls back to memory for the life of the page. Batches
// travel as JSON text so .NET parses them with its source-generated context (trim-safe).
(function () {
  'use strict';

  const MAX = 360; // an hour of ten-second batches; past it the oldest go and their ticks count as lost
  let _memory = null; // Map key -> record, once IndexedDB has failed

  const { run } = window.powatchIdb.store('powatch-outbox', 'batches',
    (db) => db.createObjectStore('batches', { keyPath: 'key' }).createIndex('at', 'at'));

  /** Uses IndexedDB until it fails once, then memory: a batch is never dropped because storage is blocked. */
  async function store(idb, memory) {
    if (!_memory) {
      try { return await idb(); } catch { _memory = new Map(); }
    }
    return memory(_memory);
  }

  const all = () => store(() => run('readonly', (s) => s.getAll()), (m) => [...m.values()]);
  const count = () => store(() => run('readonly', (s) => s.count()), (m) => m.size);
  const add = (record) => store(() => run('readwrite', (s) => s.put(record)), (m) => { m.set(record.key, record); });
  const remove = (key) => store(() => run('readwrite', (s) => s.delete(key)), (m) => { m.delete(key); });

  const ticksIn = (json) => { try { return JSON.parse(json).ticks?.length ?? 0; } catch { return 0; } };

  /** Stores a batch; past the cap the oldest go. Resolves with how many ticks were dropped. */
  async function put(key, sessionId, json) {
    await add({ key, sessionId, json, at: Date.now() });
    // Counting is all a put needs nearly every time; the batches are only read back once past the cap.
    if (await count() <= MAX) return 0;
    const records = await all();
    let lost = 0;
    for (const old of records.sort((a, b) => a.at - b.at).slice(0, records.length - MAX)) {
      lost += ticksIn(old.json);
      await remove(old.key);
    }
    return lost;
  }

  /** Every waiting batch, oldest first, as JSON text: [{ key, sessionId, json }]. */
  async function waiting() {
    const records = await all();
    return JSON.stringify(records.sort((a, b) => a.at - b.at).map((r) => ({ key: r.key, sessionId: r.sessionId, json: r.json })));
  }

  // A tab closed or reloaded mid-session never reaches Stop, and the server would list that session as
  // running forever. End it on the way out; its unsent batches still go out from this outbox later.
  let running = null;
  addEventListener('pagehide', () => { if (running) navigator.sendBeacon(`api/sessions/${running}/stop`); });
  const guard = (sessionId) => { running = sessionId; };
  const unguard = () => { running = null; };

  window.powatchOutbox = { put, remove, waiting, guard, unguard };
})();
