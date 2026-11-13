// One IndexedDB object store, opened once and used a transaction at a time. Shared by the outbox, the
// time-lapse and the caption index, which each keep a single store in a database of their own.
(function () {
  'use strict';

  /**
   * `upgrade(db)` creates the store and its indexes the first time. Returns { run }, where
   * run(mode, work) calls work(store) in one transaction and resolves with the result of the request
   * it returns (undefined when it returns none) once the transaction has committed.
   */
  function store(database, name, upgrade) {
    let _db = null;

    function open() {
      return _db ??= new Promise((resolve, reject) => {
        const fail = (error) => { _db = null; reject(error); };
        const req = indexedDB.open(database, 1);
        req.onupgradeneeded = () => upgrade(req.result);
        req.onsuccess = () => {
          const db = req.result;
          // Another tab needs a newer version, or the browser closed the database under us (storage
          // cleared): let go, and the next run opens it again.
          db.onversionchange = () => { db.close(); _db = null; };
          db.onclose = () => { _db = null; };
          resolve(db);
        };
        req.onerror = () => fail(req.error);
        req.onblocked = () => fail(new Error(`${database} is blocked by another tab`));
      });
    }

    async function run(mode, work) {
      const db = await open();
      return new Promise((resolve, reject) => {
        let tx;
        try {
          tx = db.transaction(name, mode);
        } catch (err) {
          // The connection was closed between open() and here: the next run opens a new one.
          _db = null;
          reject(err);
          return;
        }
        const req = work(tx.objectStore(name));
        tx.oncomplete = () => resolve(req?.result);
        tx.onerror = () => reject(tx.error);
        tx.onabort = () => reject(tx.error ?? new Error(`${database} transaction aborted`));
      });
    }

    return { run };
  }

  window.powatchIdb = { store };
})();
