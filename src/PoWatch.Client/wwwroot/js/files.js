// Downloads that can fail (PDFs are made on the server, and the server may not be able to): fetch first,
// save only what came back OK, and otherwise say why in a toast, so a failure never lands on disk as a
// ".pdf" that is really an error message. The link keeps its href and download attribute, so without
// this script it still downloads the plain way.
(function () {
    'use strict';

    function toast(text) {
        const el = document.createElement('div');
        el.className = 'term-toast';
        el.setAttribute('role', 'alert');
        el.textContent = text;
        document.body.appendChild(el);
        setTimeout(() => el.remove(), 7000);
    }

    async function fetchAndSave(link) {
        const name = link.getAttribute('download') || 'the file';
        link.setAttribute('aria-busy', 'true');
        try {
            const response = await fetch(link.href, { credentials: 'same-origin' });
            if (!response.ok) {
                let why = `the server answered ${response.status}`;
                try {
                    const problem = await response.json();
                    why = problem.title || problem.detail || why;
                } catch { /* not a problem document */ }
                toast(`Couldn't save ${name}: ${why}`);
                return;
            }
            const url = URL.createObjectURL(await response.blob());
            const a = document.createElement('a');
            a.href = url;
            a.download = link.getAttribute('download') || '';
            document.body.appendChild(a);
            a.click();
            a.remove();
            setTimeout(() => URL.revokeObjectURL(url), 10000);
        } catch {
            toast(`Couldn't save ${name}: the server could not be reached.`);
        } finally {
            link.removeAttribute('aria-busy');
        }
    }

    window.powatchFiles = {
        /** For onclick="return powatchFiles.save(event, this)" on an <a href download>. */
        save(event, link) {
            event.preventDefault();
            fetchAndSave(link);
            return false;
        }
    };
})();
