import test from "node:test";
import assert from "node:assert/strict";
import vm from "node:vm";
import { readFile } from "node:fs/promises";

const webRoot = new URL("../../src/WorkoutLog.Web/wwwroot/", import.meta.url);
const workerSource = await readFile(new URL("service-worker.published.js", webRoot), "utf8");
const bootstrapSource = await readFile(new URL("js/bootstrap.js", webRoot), "utf8");
const origin = "https://example.test/workout-log/";
const oldCache = "ritm-/workout-log/-old";
const newCache = "ritm-/workout-log/-new";

// Изолированная модель Cache API: никаких настоящих браузеров и запросов Google.
function createWorker({ failInstall = false } = {}) {
    const stores = new Map([[oldCache, new Map([
        [origin + "old.wasm", new Response("old binary")]
    ])]]);
    const keyOf = value => typeof value === "string" ? value : value.url ?? value.href;
    const clients = [{ id: "one", url: origin, postMessage() {} }];
    let activated = false;
    const caches = {
        keys: async () => [...stores.keys()],
        delete: async name => stores.delete(name),
        async open(name) {
            if (!stores.has(name)) {
                stores.set(name, new Map());
            }
            const entries = stores.get(name);
            return {
                match: async key => entries.get(keyOf(key))?.clone(),
                put: async (key, response) => entries.set(keyOf(key), response),
                async addAll(requests) {
                    for (const request of requests) {
                        entries.set(request.url, new Response('<meta name="app-build" content="build-new" />'));
                    }
                    if (failInstall) {
                        throw new Error("Network interrupted");
                    }
                }
            };
        }
    };
    const self = {
        location: { href: origin + "service-worker.js" },
        assetsManifest: { version: "new", assets: [{ url: "index.html" }] },
        importScripts() {},
        addEventListener() {},
        skipWaiting: async () => { activated = true; },
        clients: { claim: async () => {}, matchAll: async () => clients }
    };
    const context = vm.createContext({ self, caches, URL, Request, Response,
        fetch: async () => { throw new Error("Offline"); } });
    vm.runInContext(workerSource, context);
    return { context, stores, clients, activated: () => activated };
}

test("incomplete download keeps the previous cache and never activates", async () => {
    const worker = createWorker({ failInstall: true });
    await assert.rejects(worker.context.installApplication(), /Network interrupted/);
    assert.equal(worker.activated(), false);
    assert.equal(worker.stores.has(oldCache), true);
    assert.equal(worker.stores.has(newCache), false);
});

test("activation keeps old assets until all open pages use the new build", async () => {
    const worker = createWorker();
    await worker.context.installApplication();
    await worker.context.activateApplication();
    assert.equal(worker.activated(), true);
    assert.equal(worker.stores.has(oldCache), true);
    worker.clients.push({ id: "two", url: origin, postMessage() {} });
    await worker.context.acceptClientBuild(worker.clients[0], "build-new");
    assert.equal(worker.stores.has(oldCache), true);
    await worker.context.acceptClientBuild(worker.clients[1], "build-old");
    assert.equal(worker.stores.has(oldCache), true);
    const response = await worker.context.serveApplication({ url: origin + "old.wasm", mode: "cors" });
    assert.equal(await response.text(), "old binary");
    await worker.context.acceptClientBuild(worker.clients[1], "build-new");
    assert.equal(worker.stores.has(oldCache), false);
    assert.equal(worker.stores.has(newCache), true);
});

test("cleanup never removes the cache of a concurrently installing future build", async () => {
    const worker = createWorker();
    await worker.context.installApplication();
    const futureCache = "ritm-/workout-log/-future";
    worker.stores.set(futureCache, new Map());
    await worker.context.acceptClientBuild(worker.clients[0], "build-new");
    assert.equal(worker.stores.has(oldCache), false);
    assert.equal(worker.stores.has(futureCache), true);
});

test("navigation uses the complete new application while offline", async () => {
    const worker = createWorker();
    await worker.context.installApplication();
    const response = await worker.context.serveApplication({ url: origin + "history", mode: "navigate" });
    assert.match(await response.text(), /build-new/);
});

function createPage() {
    const workerEvents = new Map();
    const documentEvents = new Map();
    const timers = new Map();
    const state = { active: true, editing: false, settings: false, reloads: 0, now: 1000, checks: 0 };
    const registration = { update: async () => {} };
    const navigator = {
        onLine: true,
        serviceWorker: {
            controller: { postMessage() {} },
            addEventListener: (name, callback) => workerEvents.set(name, callback),
            register: async () => registration
        }
    };
    const document = {
        visibilityState: "visible",
        activeElement: { matches: () => state.editing },
        querySelector: selector => selector.startsWith("meta") ? { content: "build-old" } : state.settings,
        addEventListener: (name, callback) => documentEvents.set(name, callback)
    };
    const window = { addEventListener() {}, location: { reload: () => state.reloads++ } };
    const context = vm.createContext({ navigator, document, window, console,
        Date: { now: () => state.now }, setInterval: (callback, delay) => timers.set(delay, callback) });
    vm.runInContext(bootstrapSource, context);
    window.ritmUpdates.connect({
        async invokeMethodAsync(method) {
            if (method === "PrepareAppReload") {
                state.checks++;
                return !state.active;
            }
        }
    });
    state.now += 5000;
    return { state, document, workerEvents, documentEvents, timers };
}

const settle = () => new Promise(resolve => setImmediate(resolve));

test("new build waits for active workout, then reloads automatically once", async () => {
    const page = createPage();
    page.workerEvents.get("message")({ data: { type: "APP_VERSION", build: "build-new" } });
    await settle();
    assert.equal(page.state.reloads, 0);
    page.state.active = false;
    page.timers.get(5000)();
    await settle();
    assert.equal(page.state.reloads, 1);
    page.timers.get(5000)();
    await settle();
    assert.equal(page.state.reloads, 1);
});

test("first installation of the same build does not create a reload loop", async () => {
    const page = createPage();
    page.state.active = false;
    page.workerEvents.get("message")({ data: { type: "APP_VERSION", build: "build-old" } });
    page.timers.get(5000)();
    await settle();
    assert.equal(page.state.reloads, 0);
});

test("focused input and unsaved settings defer automatic reload", async () => {
    const page = createPage();
    page.state.active = false;
    page.state.editing = true;
    page.workerEvents.get("message")({ data: { type: "APP_VERSION", build: "build-new" } });
    await settle();
    assert.equal(page.state.checks, 0);
    page.state.editing = false;
    page.state.settings = true;
    page.documentEvents.get("input")({ target: { closest: () => true } });
    page.state.now += 5000;
    page.timers.get(5000)();
    await settle();
    assert.equal(page.state.reloads, 0);
    page.state.settings = false;
    page.timers.get(5000)();
    await settle();
    assert.equal(page.state.reloads, 1);
});
