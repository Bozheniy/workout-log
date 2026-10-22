// Кэш приложения отделён от IndexedDB: обновление PWA не удаляет тренировки.
self.importScripts("./service-worker-assets.js");

const baseUrl = new URL("./", self.location.href);
const cachePrefix = "ritm-" + baseUrl.pathname + "-";
const cacheName = cachePrefix + self.assetsManifest.version;
const previousCachesKey = new URL(".ritm-previous-caches", baseUrl);
const assetUrls = new Set(self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href));
const clientBuilds = new Map();
let buildPromise;

self.addEventListener("message", event => {
    if (event.data?.type === "CLIENT_BUILD" && event.source?.id) {
        event.waitUntil(acceptClientBuild(event.source, event.data.build));
    }
});

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
    const previousCaches = (await caches.keys()).filter(name => name.startsWith(cachePrefix) && name !== cacheName);
    const cache = await caches.open(cacheName);
    const requests = self.assetsManifest.assets
        .filter(asset => !asset.url.endsWith("service-worker.js"))
        .map(asset => new Request(new URL(asset.url, baseUrl), {
            integrity: asset.hash,
            cache: "no-cache"
        }));
    try {
        await cache.addAll(requests);
        // Запоминаем только предшествующие сборки. Иначе работающая старая версия
        // могла бы удалить кэш следующего обновления, пока оно ещё устанавливается.
        await cache.put(previousCachesKey, new Response(JSON.stringify(previousCaches)));
    } catch (error) {
        // Неполная загрузка не должна отображаться как готовность к офлайн.
        await caches.delete(cacheName);
        throw error;
    }
    // Активируем только полностью загруженную версию. Открытые страницы сами
    // выбирают безопасный момент перезагрузки, а старые файлы пока остаются в кэше.
    await self.skipWaiting();
}

async function activateApplication() {
    await self.clients.claim();
    for (const client of await applicationClients()) {
        client.postMessage({ type: "REQUEST_BUILD" });
    }
}

async function applicationClients() {
    const clients = await self.clients.matchAll({ type: "window" });
    return clients.filter(client => client.url.startsWith(baseUrl.href));
}

function readBuild() {
    buildPromise ??= caches.open(cacheName)
        .then(cache => cache.match(new URL("index.html", baseUrl)))
        .then(response => response.text())
        .then(html => html.match(/name="app-build"\s+content="([^"]+)"/)?.[1] ?? self.assetsManifest.version);
    return buildPromise;
}

async function acceptClientBuild(source, build) {
    const currentBuild = await readBuild();
    clientBuilds.set(source.id, build);
    source.postMessage({ type: "APP_VERSION", build: currentBuild });
    const clients = await applicationClients();
    if (clients.length > 0 && clients.every(client => clientBuilds.get(client.id) === currentBuild)) {
        // Старые вкладки больше не используют предыдущие файлы. IndexedDB не трогаем.
        const cache = await caches.open(cacheName);
        const metadata = await cache.match(previousCachesKey);
        const previousCaches = metadata ? await metadata.json() : [];
        await Promise.all(previousCaches.map(key => caches.delete(key)));
    }
}

async function serveApplication(request) {
    const cache = await caches.open(cacheName);
    const navigation = request.mode === "navigate" && !assetUrls.has(request.url);
    const key = navigation ? new URL("index.html", baseUrl).href : request;
    const current = await cache.match(key);
    if (current) {
        return current;
    }
    // Уже открытая старая версия Blazor может запросить свой файл с прежним хэшем.
    // Сохраняем возможность закончить тренировку даже без сети во время обновления.
    if (request.mode !== "navigate") {
        for (const name of (await caches.keys()).filter(name => name.startsWith(cachePrefix))) {
            const previous = await (await caches.open(name)).match(request);
            if (previous) {
                return previous;
            }
        }
    }
    return fetch(request);
}
