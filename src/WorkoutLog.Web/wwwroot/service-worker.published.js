// Кэш приложения отделён от IndexedDB: обновление PWA не удаляет тренировки.
self.importScripts("./service-worker-assets.js");

const baseUrl = new URL("./", self.location.href);
const cachePrefix = "ritm-" + baseUrl.pathname + "-";
const cacheName = cachePrefix + self.assetsManifest.version;
const assetUrls = new Set(self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href));

self.addEventListener("install", event => {
    event.waitUntil(installApplication());
});
self.addEventListener("activate", event => {
    event.waitUntil(activateApplication());
});
self.addEventListener("fetch", event => {
    const url = new URL(event.request.url);
    // Запросы Google всегда идут в сеть и никогда не сохраняются в кэше PWA.
    if (event.request.method === "GET" && url.origin === baseUrl.origin
        && url.pathname.startsWith(baseUrl.pathname)) {
        event.respondWith(serveApplication(event.request));
    }
});

async function installApplication() {
    const cache = await caches.open(cacheName);
    const requests = self.assetsManifest.assets
        .filter(asset => !asset.url.endsWith("service-worker.js"))
        .map(asset => new Request(new URL(asset.url, baseUrl), {
            integrity: asset.hash,
            cache: "no-cache"
        }));
    try {
        await cache.addAll(requests);
    } catch (error) {
        // Неполная загрузка не должна отображаться как готовность к офлайн.
        await caches.delete(cacheName);
        throw error;
    }
    // Не вызываем skipWaiting: открытая тренировка продолжает работать
    // на согласованной версии приложения до закрытия всех вкладок.
}

async function activateApplication() {
    const keys = await caches.keys();
    await Promise.all(keys.filter(key => key.startsWith(cachePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));
    await self.clients.claim();
}

async function serveApplication(request) {
    const cache = await caches.open(cacheName);
    const navigation = request.mode === "navigate" && !assetUrls.has(request.url);
    const key = navigation ? new URL("index.html", baseUrl).href : request;
    return await cache.match(key) || fetch(request);
}
