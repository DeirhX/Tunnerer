// publish.py creates this manifest from the complete published output, including all .NET runtime assets.
importScripts("./assets-manifest.js", "./threads.js");
const root = new URL("./", self.location.href);
const cachePrefix = `emberward-${encodeURIComponent(root.pathname)}-`;
// Before the game was renamed, its caches began "tunnerer-". Nothing else clears them, so activation does.
const legacyCachePrefix = `tunnerer-${encodeURIComponent(root.pathname)}-`;
const cacheName = cachePrefix + self.emberwardAssets.version;
const absolute = (list) => list.map((url) => new URL(url, root).href);
const urls = new Set(absolute(self.emberwardAssets.urls));
urls.add(root.href);
// A deployment with two .NET runtimes lists each apart from the rest (publish.py --threads). A browser plays in
// one of them, so only that one is fetched and kept ahead of time. Should the page come to a different answer
// than this worker did (threads.js), the other runtime is kept from the moment the page first loads it.
const runtimes = self.emberwardAssets.runtimes || {};
const mine = self.emberwardThreads.use ? "threads" : "single";
const onDemand = new Set();
for (const [name, list] of Object.entries(runtimes)) {
    const kept = name === mine ? urls : onDemand;
    for (const url of absolute(list)) kept.add(url);
}

self.addEventListener("install", (event) => {
    // Installation is atomic: a failed fetch leaves the previous working app active.
    // No skipWaiting here: the page asks for it ("skip-waiting" below) when it holds no game and no other tab is
    // open, or when the player presses its Update button, and then reloads.
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
                if (
                    (key.startsWith(cachePrefix) && key !== cacheName) ||
                    key.startsWith(legacyCachePrefix)
                )
                    await caches.delete(key);
            await self.clients.claim();
            for (const client of await self.clients.matchAll())
                client.postMessage({ type: "offline-ready", version: self.emberwardAssets.version });
        })(),
    );
});

self.addEventListener("message", (event) => {
    if (event.data?.type === "status")
        event.source?.postMessage({ type: "offline-ready", version: self.emberwardAssets.version });
    // The Update button forces it. Otherwise the page asks only while it holds no game, and another open tab might
    // hold one, so the worker waits for the player while any other Emberward tab is open.
    if (event.data?.type === "skip-waiting")
        event.waitUntil(
            (async () => {
                const tabs = await self.clients.matchAll({
                    type: "window",
                    includeUncontrolled: true,
                });
                if (event.data.force || tabs.length <= 1) await self.skipWaiting();
                else event.source?.postMessage({ type: "update-held" });
            })(),
        );
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
    } else if (onDemand.has(url.href)) {
        event.respondWith(
            (async () => {
                const cache = await caches.open(cacheName);
                const cached = await cache.match(request);
                if (cached) return cached;
                // As at install: never a copy the browser kept from an older publish, whose runtime files do
                // not go with this one's.
                const response = await fetch(request, { cache: "reload" });
                if (response.ok) await cache.put(request, response.clone());
                return response;
            })(),
        );
    }
});
