// theme-controller.js — runs once at app boot, before any Blazor paint.
// Reads the persisted theme from localStorage and applies it to <html data-theme> BEFORE the
// layout renders, so there is no flash. The Terminal look is dark by design, so dark is the
// default whatever the OS prefers; the header's theme toggle switches and remembers light.

(function () {
    'use strict';
    try {
        var stored = null;
        try { stored = localStorage.getItem('powatch:theme'); } catch (_) { /* private mode */ }
        if (stored !== 'light' && stored !== 'dark') stored = 'dark';
        document.documentElement.setAttribute('data-theme', stored);
    } catch (_) {
        // Old browser, very strict CSP, or no localStorage — leave default (dark).
    }

    // Expose a tiny API for the theme toggle button to call.
    window.powatchTheme = {
        get: function () {
            return document.documentElement.getAttribute('data-theme') || 'dark';
        },
        toggle: function () {
            var next = window.powatchTheme.get() === 'dark' ? 'light' : 'dark';
            document.documentElement.setAttribute('data-theme', next);
            try { localStorage.setItem('powatch:theme', next); } catch (_) { /* private mode */ }
            return next;
        }
    };
})();
