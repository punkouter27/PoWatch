// Loads the Advanced-only effect scripts (fx.js, crt.js, skyline.js) when first needed instead of with
// the shell. Until a script arrives its global is a stand-in whose methods load it and then call the
// real one, so .NET callers keep invoking `powatchFx.update` and friends as before. UiPrefs preloads all
// three as soon as Advanced is on, so the first tube switch or trail does not wait for the network.
(function () {
    'use strict';

    // name -> [global, methods, { method: value returned without loading }].
    const SCRIPTS = {
        fx: ['powatchFx', ['update', 'exposure'], { exposure: null }],
        crt: ['powatchCrt', ['power', 'frame'], {}],
        skyline: ['powatchSkyline', ['mount', 'select', 'flyover', 'unmount'], { unmount: undefined }],
    };
    const loading = new Map();

    function loadOne(name) {
        if (!SCRIPTS[name]) return Promise.reject(new Error(`Unknown script: ${name}`));
        if (!loading.has(name)) {
            loading.set(name, new Promise((resolve, reject) => {
                const script = document.createElement('script');
                script.src = `js/${name}.js`;
                script.onload = () => resolve();
                script.onerror = () => { loading.delete(name); reject(new Error(`Could not load js/${name}.js`)); };
                document.head.appendChild(script);
            }));
        }
        return loading.get(name);
    }

    /** Loads the named scripts (fx, crt, skyline); resolves once all of them have run. */
    window.powatchLoad = (...names) => Promise.all(names.map(loadOne));

    for (const [name, [global, methods, idle]] of Object.entries(SCRIPTS)) {
        const standIn = {};
        for (const method of methods) {
            standIn[method] = (...args) => {
                // Asked for something that only exists once the script has run (the long exposure of a
                // session nobody drew a trail in): answer without loading it.
                if (method in idle && !loading.has(name)) return idle[method];
                return loadOne(name).then(() => {
                    const real = window[global];
                    if (real === standIn) throw new Error(`js/${name}.js did not define ${global}`);
                    return real[method](...args);
                });
            };
        }
        window[global] = standIn;
    }
})();
