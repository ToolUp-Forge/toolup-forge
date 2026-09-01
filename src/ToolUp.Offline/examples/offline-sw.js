// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)
//
// ToolUp.Offline — reference service worker (Phase 24).
//
// Copy this file to the root of your client's public assets (so it is
// served from `/offline-sw.js` and can claim the whole origin) and
// point `OfflineConfig.ServiceWorkerUrl` at it. It is a TEMPLATE: it is
// deliberately small enough to read end to end and adjust, rather than
// a framework you configure.
//
// ── Strategy ────────────────────────────────────────────────────────
//
//   static assets  → CACHE-FIRST. The app shell must boot with no
//                    network at all, so a cache hit is served without
//                    even attempting a request. Misses are fetched and
//                    cached.
//   GET /api/*     → NETWORK-FIRST, falling back to the cache. Data
//                    should be fresh when it can be; stale data is far
//                    better than an error page when it cannot.
//   write /api/*   → PASS THROUGH, and on failure return a 503 tagged
//                    `x-toolup-offline: queued` so the client knows to
//                    queue rather than to show an error.
//
// ── The write path is the subtle one ────────────────────────────────
//
// This worker does NOT queue mutations itself. A service worker that
// replayed writes on its own would be a second, invisible writer racing
// the page's own `SyncCoordinator`, with its own copy of the queue and
// no way to show the user a conflict. Queueing lives in the page, in
// IndexedDB, under `IOfflineQueue`; the worker's only job on a failed
// write is to answer in a way the page can recognise without having to
// distinguish a network error from a server error.
//
// ── Cache versioning ────────────────────────────────────────────────
//
// The worker reads its cache prefix and version from its OWN script
// URL (`?cache=<prefix>&v=<version>`), which `ServiceWorkerRegistration`
// appends from `OfflineConfig`. Two consequences, both intended:
// bumping `CacheVersion` changes this script's URL, so the browser
// installs a new worker rather than reusing the byte-identical old one;
// and `activate` deletes every cache whose name carries the same prefix
// but a different version, so the previous release's assets are evicted
// rather than accumulating.

'use strict';

const params = new URL(self.location.href).searchParams;
const CACHE_PREFIX = params.get('cache') || 'toolup-offline';
const CACHE_VERSION = params.get('v') || 'v1';
const CACHE_NAME = CACHE_PREFIX + '-' + CACHE_VERSION;

// Shell assets fetched and cached at install time. Keep this list
// SHORT — install fails atomically if any entry 404s, and a worker that
// cannot install leaves the app with no offline support at all rather
// than partial support. Everything else is cached lazily on first use.
const PRECACHE_URLS = ['/', '/index.html'];

// Requests under this prefix are the offline companion's OWN sync
// endpoints. They must never be cached and never be treated as
// queueable: they are what runs when the network comes back, and a
// cached response to a replay would report a phantom success.
const SYNC_PREFIX = '/api/IOfflineSyncApi/';

function isApiRequest(url) {
    return url.pathname.startsWith('/api/');
}

function isSyncRequest(url) {
    return url.pathname.startsWith(SYNC_PREFIX);
}

self.addEventListener('install', (event) => {
    event.waitUntil(
        caches
            .open(CACHE_NAME)
            .then((cache) => cache.addAll(PRECACHE_URLS))
            // Take over on the next load rather than waiting for every
            // tab to close. Safe here because the cache name is
            // version-scoped, so a new worker never reads the old
            // worker's entries.
            .then(() => self.skipWaiting())
    );
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches
            .keys()
            .then((names) =>
                Promise.all(
                    names
                        .filter((name) => name.startsWith(CACHE_PREFIX + '-') && name !== CACHE_NAME)
                        .map((name) => caches.delete(name))
                )
            )
            .then(() => self.clients.claim())
    );
});

// Network-first: try the server, fall back to whatever was cached last.
// Only successful, basic/cors responses are cached — caching an error
// response would serve that error offline for the life of the cache.
function networkFirst(request) {
    return fetch(request)
        .then((response) => {
            if (response && response.ok) {
                const copy = response.clone();
                caches.open(CACHE_NAME).then((cache) => cache.put(request, copy));
            }
            return response;
        })
        .catch(() =>
            caches.match(request).then(
                (cached) =>
                    cached ||
                    new Response(JSON.stringify({ error: 'offline', cached: false }), {
                        status: 503,
                        headers: { 'Content-Type': 'application/json', 'x-toolup-offline': 'unavailable' }
                    })
            )
        );
}

// Cache-first: serve the cached copy if there is one, otherwise fetch
// and cache. The app shell path.
function cacheFirst(request) {
    return caches.match(request).then((cached) => {
        if (cached) {
            return cached;
        }

        return fetch(request)
            .then((response) => {
                if (response && response.ok && (response.type === 'basic' || response.type === 'cors')) {
                    const copy = response.clone();
                    caches.open(CACHE_NAME).then((cache) => cache.put(request, copy));
                }
                return response;
            })
            .catch(
                () =>
                    new Response('', {
                        status: 503,
                        headers: { 'x-toolup-offline': 'unavailable' }
                    })
            );
    });
}

// A write that could not reach the server. The 503 body is machine-
// readable and the header is the signal the page keys off — the page
// enqueues the mutation and reports "saved on this device" rather than
// an error.
function queueOnFail(request) {
    return fetch(request).catch(
        () =>
            new Response(JSON.stringify({ error: 'offline', queued: true }), {
                status: 503,
                headers: { 'Content-Type': 'application/json', 'x-toolup-offline': 'queued' }
            })
    );
}

self.addEventListener('fetch', (event) => {
    const request = event.request;
    const url = new URL(request.url);

    // Cross-origin requests are none of this worker's business —
    // intercepting them breaks CDN fonts, analytics and auth redirects
    // in ways that are hard to attribute back to here.
    if (url.origin !== self.location.origin) {
        return;
    }

    // The sync endpoints go straight to the network, always. See the
    // note on SYNC_PREFIX.
    if (isSyncRequest(url)) {
        return;
    }

    if (isApiRequest(url)) {
        if (request.method === 'GET') {
            event.respondWith(networkFirst(request));
        } else {
            event.respondWith(queueOnFail(request));
        }
        return;
    }

    // Everything else is a static asset.
    if (request.method === 'GET') {
        event.respondWith(cacheFirst(request));
    }
});
