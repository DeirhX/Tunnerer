// publish.py creates this manifest from the complete published output, including all .NET runtime assets.
importScripts("./assets-manifest.js");
const root = new URL("./", self.location.href);
const cachePrefix = `tunnerer-${encodeURIComponent(root.pathname)}-`;
const cacheName = cachePrefix + self.tunnererAssets.version;
const urls = new Set(
    self.tunnererAssets.urls.map((url) => new URL(url, root).href),
);
urls.add(root.href);

self.addEventListener("install", (event) => {
    // Installation is atomic: a failed fetch leaves the previous working app active.
    // Do not skipWaiting: open games keep using the runtime they loaded with.
    event.waitUntil(
        (async () => {
            const cache = await caches.open(cacheName);
            const pending = [...urls];
            // Bound concurrent requests on phones and validate every asset before the worker installs.
            await Promise.all(
                Array.from({ length: 6 }, async () => {
                    while (pending.length) {
                        const url = pending.pop();
                        const response = await fetch(
                            new Request(url, { cache: "reload" }),
                        );
                        if (!response.ok)
                            throw new Error(
                                `Cannot cache ${url}: ${response.status}`,
                            );
                        await cache.put(url, response);
                    }
                }),
            );
        })(),
    );
});

self.addEventListener("activate", (event) => {
    event.waitUntil(
        (async () => {
            for (const key of await caches.keys())
                if (key.startsWith(cachePrefix) && key !== cacheName)
                    await caches.delete(key);
            await self.clients.claim();
            for (const client of await self.clients.matchAll())
                client.postMessage({ type: "offline-ready" });
        })(),
    );
});

self.addEventListener("message", (event) => {
    if (event.data?.type === "status")
        event.source?.postMessage({ type: "offline-ready" });
});

self.addEventListener("fetch", (event) => {
    const request = event.request;
    if (request.method !== "GET") return;
    const url = new URL(request.url);
    if (url.origin !== root.origin || !url.pathname.startsWith(root.pathname))
        return;
    if (request.mode === "navigate") {
        event.respondWith(
            caches
                .open(cacheName)
                .then((cache) =>
                    cache
                        .match(root.href)
                        .then((cached) => cached || fetch(request)),
                ),
        );
    } else if (urls.has(url.href)) {
        event.respondWith(
            caches
                .open(cacheName)
                .then((cache) =>
                    cache
                        .match(request)
                        .then((cached) => cached || fetch(request)),
                ),
        );
    }
});
