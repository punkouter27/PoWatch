// Request/response over a module Web Worker, shared by the detector, caption, hearing and embedding
// bridges. Each message carries an `id` the worker echoes back; a reply with type 'ERROR' rejects.
// The worker starts on first use and is recreated after a crash or a GPU reset (a 'GPU_LOST'
// message, sent by transformers-env.js). A bridge whose session has stopped lets its worker go after
// a while (releaseAfter), which hands the model's memory back; the next call starts a fresh one.
//
// Worker URLs are built in one place (powatchWorkerRpc.url), relative to this folder and with no
// query string: the published service worker answers exact URLs from its versioned cache, so a
// stamped worker URL missed that cache while the modules it imports did not.
//
// Workers that load a model report its load as 'MODEL_STATUS' messages (transformers-env.js). The latest
// per named worker is kept in powatchModels, which the Live models strip polls (AiSelectors.razor).
(function () {
  'use strict';

  // currentScript is only set while this file first runs, so the folder is resolved now.
  const BASE = new URL('./', document.currentScript?.src ?? new URL('js/', document.baseURI));

  const models = new Map();
  window.powatchModels = {
    /** [{ name, phase, model, device, dtype, loaded, total, loadMs, error, at }], one per worker that reported. */
    snapshot: () => [...models.values()],
  };
  const setStatus = (name, status) => { if (name) models.set(name, { ...status, name, at: Date.now() }); };

  /**
   * `path` is the worker's file under js/ (e.g. 'sensing/detector-worker.js'); `name` (e.g. 'detector',
   * 'caption') files this worker's model status under that key.
   */
  window.powatchWorkerRpc = function (path, name) {
    const url = new URL(path, BASE);
    let worker = null;
    let nextId = 1;
    let idleTimer = 0;
    const pending = new Map();

    /** Fails whatever the worker still owed and lets it go; the next call starts a fresh one. */
    function drop(reason) {
      worker?.terminate();
      worker = null;
      for (const { reject } of pending.values()) reject(new Error(reason));
      pending.clear();
    }

    function ensureWorker() {
      if (worker) return worker;
      worker = new Worker(url, { type: 'module' });
      worker.onmessage = (event) => {
        if (event.data.type === 'MODEL_STATUS') {
          setStatus(name, event.data.status);
          return;
        }
        if (event.data.type === 'GPU_LOST') {
          call.gpuLosses++;
          const last = models.get(name);
          if (last) setStatus(name, { ...last, phase: 'lost', error: event.data.message });
          drop('The GPU was reset; the model reloads on the next run');
          return;
        }
        const entry = pending.get(event.data.id);
        if (!entry) return;
        pending.delete(event.data.id);
        if (event.data.type === 'ERROR') entry.reject(new Error(event.data.message));
        else entry.resolve(event.data);
      };
      worker.onerror = () => drop('Worker crashed');
      return worker;
    }

    /** Posts `message`; resolves to the worker's reply. timeoutMs = 0 waits forever (model loads are slow). */
    function call(message, transfer = [], timeoutMs = 0) {
      const id = nextId++;
      return new Promise((resolve, reject) => {
        let timer = null;
        if (timeoutMs > 0) {
          timer = setTimeout(() => {
            if (pending.delete(id)) reject(new Error(`Worker did not answer ${message.type} within ${timeoutMs}ms`));
          }, timeoutMs);
        }
        pending.set(id, {
          resolve: (v) => { clearTimeout(timer); resolve(v); },
          reject: (e) => { clearTimeout(timer); reject(e); },
        });
        try {
          ensureWorker().postMessage({ ...message, id }, transfer);
        } catch (err) {
          // The worker could not be started or the message could not be posted: nothing will answer.
          pending.delete(id);
          clearTimeout(timer);
          reject(err);
        }
      });
    }

    /** Ends the worker (and fails whatever it still owed); the next call starts a fresh one. */
    call.restart = () => drop('Worker restarted');

    /**
     * The session that used this worker has stopped: after `ms` without keep(), end the worker so its
     * model's memory is returned. A worker still answering something is given another `ms`.
     */
    call.releaseAfter = (ms) => {
      clearTimeout(idleTimer);
      idleTimer = setTimeout(() => {
        if (pending.size > 0) { call.releaseAfter(ms); return; }
        idleTimer = 0;
        if (!worker) return;
        drop('Worker released after its session stopped');
        // Its model went with it: the Live strip shows it as not loaded until the next worker reports.
        if (name) models.delete(name);
      }, ms);
    };

    /** A session is using this worker again: a pending release is called off. */
    call.keep = () => { clearTimeout(idleTimer); idleTimer = 0; };

    /** GPU resets seen on this page. Twice is a GPU that cannot hold the model: callers then ask for wasm. */
    call.gpuLosses = 0;

    return call;
  };

  /** The URL of a worker file under js/, for workers that are not request/response (the sample clocks). */
  window.powatchWorkerRpc.url = (path) => new URL(path, BASE);

  /** How long a model worker outlives the session that used it (a quick Stop and Start keeps the model). */
  window.powatchWorkerRpc.IDLE_MS = 5 * 60 * 1000;
})();
