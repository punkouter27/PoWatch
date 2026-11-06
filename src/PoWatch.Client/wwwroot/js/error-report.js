// Sends this browser's unhandled errors to the server log (POST api/diagnostics/client-error), so a crash on
// someone's screen ("An unhandled error has occurred") can be read and fixed. Blazor writes its unhandled
// component exceptions to console.error; uncaught script errors and rejected promises are caught too. At
// most five reports a page, each clipped by the server; nothing is sent before sign-in (the endpoint needs it).
(function () {
    'use strict';

    let left = 5;
    const sent = new Set();

    function report(message, stack) {
        const text = String(message || '').trim();
        if (!text || left <= 0 || sent.has(text)) return;
        sent.add(text);
        left--;
        try {
            fetch('api/diagnostics/client-error', {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ message: text.slice(0, 2000), stack: String(stack || '').slice(0, 8000), url: location.href, userAgent: navigator.userAgent }),
                keepalive: true,
            }).catch(() => { });
        } catch { /* never let reporting break the page */ }
    }

    const consoleError = console.error.bind(console);
    console.error = (...args) => {
        consoleError(...args);
        const text = args.map((a) => (a instanceof Error ? `${a.message}\n${a.stack}` : String(a))).join(' ');
        // Blazor's renderer logs "Unhandled exception rendering component" (and the .NET stack) this way.
        if (/unhandled exception|crit:/i.test(text)) report(text.split('\n')[0], text);
    };

    addEventListener('error', (e) => report(e.message, e.error?.stack ?? `${e.filename}:${e.lineno}:${e.colno}`));
    addEventListener('unhandledrejection', (e) => report(`Unhandled promise rejection: ${e.reason?.message ?? e.reason}`, e.reason?.stack));
})();
