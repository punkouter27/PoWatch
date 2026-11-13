// Request/response over a module Web Worker, shared by the detector and caption bridges.
// Each message carries an `id` the worker echoes back; a reply with type 'ERROR' rejects.
// The worker starts on first use and is recreated after a crash or a GPU reset (a 'GPU_LOST'
// message, sent by transformers-env.js).
//
// Workers that load a model report its load as 'MODEL_STATUS' messages (transformers-env.js). The latest
// per named worker is kept in powatchModels, which the Live models strip polls (AiSelectors.razor).
(function () {
  'use strict';

  const models = new Map();
  window.powatchModels = {
    /** [{ name, phase, model, device, dtype, loaded, total, loadMs, error, at }], one per worker that reported. */
    snapshot: () => [...models.values()],
  };
  const setStatus = (name, status) => { if (name) models.set(name, { ...status, name, at: Date.now() }); };

  /** `name` (e.g. 'detector', 'caption') files this worker's model status under that key. */
  window.powatchWorkerRpc = function (url, name) {
    let worker = null;
    let nextId = 1;
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
        ensureWorker().postMessage({ ...message, id }, transfer);
      });
    }

    /** Ends the worker (and fails whatever it still owed); the next call starts a fresh one. */
    call.restart = () => drop('Worker restarted');

    /** GPU resets seen on this page. Twice is a GPU that cannot hold the model: callers then ask for wasm. */
    call.gpuLosses = 0;

    return call;
  };
})();
