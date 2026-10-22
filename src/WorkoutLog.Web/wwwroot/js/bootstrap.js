// Обновление загружается целиком, но открытая тренировка не перезагружается.
(() => {
    "use strict";

    if (!("serviceWorker" in navigator)) {
        return;
    }
    const pageBuild = document.querySelector('meta[name="app-build"]')?.content;
    let registration;
    let callback;
    let updatePending = false;
    let checkingReload = false;
    let lastInteraction = Date.now();
    let settingsEdited = false;

    function reportBuild() {
        navigator.serviceWorker.controller?.postMessage({ type: "CLIENT_BUILD", build: pageBuild });
    }

    async function maybeReload() {
        const editing = document.activeElement?.matches("input, textarea, select, [contenteditable]");
        const settingsOpen = document.querySelector(".settings-grid");
        if (!settingsOpen) {
            settingsEdited = false;
        }
        if (!updatePending || !callback || checkingReload || document.visibilityState !== "visible"
            || editing || (settingsEdited && settingsOpen) || Date.now() - lastInteraction < 3000) {
            return;
        }
        checkingReload = true;
        try {
            // C# дожидается IndexedDB и запрещает перезагрузку при активной тренировке.
            if (await callback.invokeMethodAsync("PrepareAppReload")) {
                window.location.reload();
                return;
            }
        } catch (error) {
            console.warn("Обновление отложено до готовности приложения.", error);
        }
        checkingReload = false;
    }

    async function checkUpdate() {
        if (!navigator.onLine || !registration) {
            return false;
        }
        try {
            await registration.update();
            reportBuild();
            return true;
        } catch {
            // Плохая сеть не мешает тренировке на сохранённой версии.
            return false;
        }
    }

    navigator.serviceWorker.addEventListener("controllerchange", reportBuild);
    navigator.serviceWorker.addEventListener("message", event => {
        if (event.data?.type === "REQUEST_BUILD") {
            reportBuild();
        }
        if (event.data?.type === "APP_VERSION" && event.data.build !== pageBuild) {
            updatePending = true;
            callback?.invokeMethodAsync("AppUpdateReady").catch(() => {});
            void maybeReload();
        }
    });

    for (const eventName of ["pointerdown", "keydown", "input", "change"]) {
        document.addEventListener(eventName, event => {
            lastInteraction = Date.now();
            if (eventName === "input" && event.target?.closest(".settings-grid")) {
                settingsEdited = true;
            }
        }, { passive: true });
    }
    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "visible") {
            void checkUpdate();
            void maybeReload();
        }
    });
    window.addEventListener("online", () => void checkUpdate());
    setInterval(() => void maybeReload(), 5000);
    setInterval(() => {
        if (document.visibilityState === "visible") {
            void checkUpdate();
        }
    }, 5 * 60 * 1000);

    window.ritmUpdates = {
        connect(reference) {
            callback = reference;
            if (updatePending) {
                callback.invokeMethodAsync("AppUpdateReady").catch(() => {});
            }
            reportBuild();
        },
        disconnect() {
            callback = null;
        },
        check: checkUpdate
    };

    navigator.serviceWorker.register("service-worker.js", { updateViaCache: "none" })
        .then(value => {
            registration = value;
            reportBuild();
            void checkUpdate();
        })
        .catch(error => console.warn("Не удалось подготовить офлайн-режим.", error));
})();
