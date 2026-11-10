// Offline shell: the app's own files are cached at install (verified against the build's hashes) and
// served cache-first, so PoWatch opens without a network and a session's outbox can replay later.
// Everything the server answers itself — the API, sign-in, the hub, health — always goes to the network.
self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall()));
self.addEventListener('activate', event => event.waitUntil(onActivate()));
const serverRoutes = /^\/(api|auth|hubs|health|diag|scalar|openapi|signin-oidc|signout-callback-oidc)(\/|$)/;
// Only the app's own files are answered here. Everything else is left to the browser untouched: a
// request relayed through this worker loses its page (so Chrome cannot ask for local-network access
// when captions call Ollama on localhost), and its failure surfaced here as an uncaught rejection.
self.addEventListener('fetch', event => {
    const url = new URL(event.request.url);
    if (event.request.method !== 'GET' || url.origin !== self.origin || serverRoutes.test(url.pathname)) return;
    event.respondWith(onFetch(event));
});

const cacheNamePrefix = 'offline-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff2?$/, /\.png$/, /\.svg$/, /\.ico$/, /\.blat$/, /\.dat$/, /\.webmanifest$/];
// The vision model's runtime is ~20 MB and only needed with captions on: fetched on first use, not at install.
const offlineAssetsExclude = [/^service-worker\.js$/, /^lib\/transformers/];

const baseUrl = new URL('/', self.origin);
const manifestUrlList = self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href);

async function onInstall() {
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
    await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));
    // An always-on wall display never closes its tab: take over on the next reload instead of waiting.
    await self.skipWaiting();
}

async function onActivate() {
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));
    await self.clients.claim();
}

async function onFetch(event) {
    // Any in-app address is the one page; a file from the manifest is itself.
    const shouldServeIndexHtml = event.request.mode === 'navigate' && !manifestUrlList.some(known => known === event.request.url);
    const cache = await caches.open(cacheName);
    const cachedResponse = await cache.match(shouldServeIndexHtml ? 'index.html' : event.request);
    return cachedResponse || fetch(event.request);
}
