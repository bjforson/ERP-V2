// NickHR Service Worker
// Strategy: Network-first for Blazor/SignalR, Cache-first for static assets

const CACHE_NAME = 'nickhr-cache-v1';
const STATIC_ASSETS = [
    '/',
    '/offline.html',
    '/app.css',
    '/mobile.css',
    '/app.js',
    '/_content/MudBlazor/MudBlazor.min.css',
    '/_content/MudBlazor/MudBlazor.min.js',
    '/favicon.png',
    '/icons/icon-192.png',
    '/icons/icon-512.png'
];

// SignalR / Blazor endpoints — always network-first
const NETWORK_FIRST_PATTERNS = [
    '/_blazor',
    '/api/',
    '/_framework/',
    '/connect/token',
    '/Account/'
];

// Install: cache static assets
self.addEventListener('install', event => {
    event.waitUntil(
        caches.open(CACHE_NAME).then(cache => {
            return cache.addAll(STATIC_ASSETS.filter(url => !url.startsWith('/_content')));
        }).catch(err => {
            console.warn('[SW] Pre-cache failed:', err);
        })
    );
    self.skipWaiting();
});

// Activate: clean old caches
self.addEventListener('activate', event => {
    event.waitUntil(
        caches.keys().then(keys =>
            Promise.all(
                keys.filter(key => key !== CACHE_NAME)
                    .map(key => caches.delete(key))
            )
        )
    );
    self.clients.claim();
});

// Fetch: routing strategy
self.addEventListener('fetch', event => {
    const url = new URL(event.request.url);

    // Skip non-GET requests
    if (event.request.method !== 'GET') return;

    // Always network-first for Blazor SignalR and API
    const isNetworkFirst = NETWORK_FIRST_PATTERNS.some(p => url.pathname.startsWith(p));
    if (isNetworkFirst) {
        event.respondWith(networkFirst(event.request));
        return;
    }

    // Cache-first for static assets
    if (isStaticAsset(url)) {
        event.respondWith(cacheFirst(event.request));
        return;
    }

    // Network-first with offline fallback for navigation
    if (event.request.mode === 'navigate') {
        event.respondWith(networkFirstWithOfflineFallback(event.request));
        return;
    }

    // Default: network first
    event.respondWith(networkFirst(event.request));
});

function isStaticAsset(url) {
    return url.pathname.match(/\.(css|js|png|jpg|jpeg|gif|svg|ico|woff|woff2|ttf|eot)$/) !== null;
}

async function cacheFirst(request) {
    const cached = await caches.match(request);
    if (cached) return cached;
    try {
        const response = await fetch(request);
        if (response.ok) {
            const cache = await caches.open(CACHE_NAME);
            cache.put(request, response.clone());
        }
        return response;
    } catch {
        return new Response('', { status: 503 });
    }
}

async function networkFirst(request) {
    try {
        const response = await fetch(request);
        if (response.ok && request.method === 'GET') {
            const cache = await caches.open(CACHE_NAME);
            cache.put(request, response.clone());
        }
        return response;
    } catch {
        const cached = await caches.match(request);
        return cached || new Response('', { status: 503 });
    }
}

async function networkFirstWithOfflineFallback(request) {
    try {
        return await fetch(request);
    } catch {
        const cached = await caches.match(request);
        if (cached) return cached;
        // Return offline page
        const offlinePage = await caches.match('/offline.html');
        return offlinePage || new Response('<h1>Offline</h1>', {
            headers: { 'Content-Type': 'text/html' }
        });
    }
}
