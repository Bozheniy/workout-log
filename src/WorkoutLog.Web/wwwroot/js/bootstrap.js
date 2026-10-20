// PWA не мешает первому запуску: ошибка регистрации не останавливает Blazor.
if ("serviceWorker" in navigator) {
    navigator.serviceWorker.register("service-worker.js", { updateViaCache: "none" })
        .catch(error => console.warn("Не удалось подготовить офлайн-режим.", error));
}
