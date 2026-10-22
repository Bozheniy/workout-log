// JavaScript нужен только для возможностей браузера. Правила тренировки находятся в C#.
(() => {
    "use strict";

    const databaseName = "ritm-workouts-v1";
    let databasePromise;
    let callback;
    let interval;
    let installPrompt;
    let tokenClient;
    let accessToken = null;
    let tokenExpires = 0;
    let authResolve;
    let authReject;
    let googleScriptPromise;
    let restDeadline = 0;
    let restInterval;

    function openDatabase() {
        databasePromise ??= new Promise((resolve, reject) => {
            const request = indexedDB.open(databaseName, 1);
            request.onupgradeneeded = () => request.result.createObjectStore("state");
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
            request.onblocked = () => reject(new Error("Закройте другую вкладку приложения."));
        });
        return databasePromise;
    }

    async function readState() {
        const database = await openDatabase();
        return new Promise((resolve, reject) => {
            const transaction = database.transaction("state", "readonly");
            const request = transaction.objectStore("state").get("current");
            request.onsuccess = () => resolve(request.result ?? null);
            request.onerror = () => reject(request.error);
        });
    }

    async function writeState(state, expectedRevision) {
        const database = await openDatabase();
        return new Promise((resolve, reject) => {
            const transaction = database.transaction("state", "readwrite");
            const store = transaction.objectStore("state");
            let reason;
            const request = store.get("current");
            request.onsuccess = () => {
                const currentRevision = request.result?.revision ?? 0;
                if (currentRevision !== expectedRevision) {
                    reason = new Error("STALE_REVISION");
                    transaction.abort();
                    return;
                }
                // План, тренировки и очередь отправки фиксируются атомарно.
                state.revision = expectedRevision + 1;
                store.put(state, "current");
            };
            transaction.oncomplete = () => resolve(expectedRevision + 1);
            transaction.onabort = () => reject(reason ?? transaction.error);
            transaction.onerror = () => reject(transaction.error);
        });
    }

    function notifyConnection() {
        callback?.invokeMethodAsync("BrowserChanged", navigator.onLine).catch(() => {});
    }

    function onVisibility() {
        if (document.visibilityState === "visible") {
            notifyConnection();
            updateTimer();
        }
    }

    window.addEventListener("beforeinstallprompt", event => {
        event.preventDefault();
        installPrompt = event;
    });

    async function loadGoogleScript() {
        if (window.google?.accounts?.oauth2) {
            return;
        }
        googleScriptPromise ??= new Promise((resolve, reject) => {
            const script = document.createElement("script");
            script.src = "https://accounts.google.com/gsi/client";
            script.async = true;
            const timer = setTimeout(() => {
                script.remove();
                googleScriptPromise = null;
                reject(new Error("Google timeout"));
            }, 15000);
            script.onload = () => {
                clearTimeout(timer);
                resolve();
            };
            script.onerror = () => {
                clearTimeout(timer);
                googleScriptPromise = null;
                reject(new Error("Google unavailable"));
            };
            document.head.appendChild(script);
        });
        await googleScriptPromise;
    }

    function updateTimer() {
        const remaining = Math.max(0, Math.ceil((restDeadline - Date.now()) / 1000));
        for (const element of document.querySelectorAll("[data-rest-countdown]")) {
            element.textContent = `${Math.floor(remaining / 60)}:${String(remaining % 60).padStart(2, "0")}`;
        }
        document.querySelector(".mobile-rest-bar")?.classList.toggle("timer-running", remaining > 0);
        if (remaining === 0 && restDeadline > 0) {
            restDeadline = 0;
            localStorage.removeItem("ritm-rest-deadline");
            navigator.vibrate?.([100, 70, 100]);
        }
    }

    window.workout = {
        initialize(reference) {
            callback = reference;
            window.ritmUpdates?.connect(reference);
            window.addEventListener("online", notifyConnection);
            window.addEventListener("offline", notifyConnection);
            document.addEventListener("visibilitychange", onVisibility);
            interval = setInterval(notifyConnection, 30000);
            restDeadline = Number(localStorage.getItem("ritm-rest-deadline") ?? 0);
            restInterval = setInterval(updateTimer, 250);
            return navigator.onLine;
        },
        dispose() {
            window.ritmUpdates?.disconnect();
            clearInterval(interval);
            clearInterval(restInterval);
            window.removeEventListener("online", notifyConnection);
            window.removeEventListener("offline", notifyConnection);
            document.removeEventListener("visibilitychange", onVisibility);
        },
        scrollToExercise(row) {
            document.getElementById(`exercise-${row}`)?.scrollIntoView({
                behavior: window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "instant" : "smooth",
                block: "start"
            });
        },
        storage: {
            read: readState,
            write: writeState,
            isPersistent: () => navigator.storage?.persisted?.() ?? false,
            persist: () => navigator.storage?.persist?.() ?? false
        },
        auth: {
            async prepare(clientId) {
                await loadGoogleScript();
                tokenClient = google.accounts.oauth2.initTokenClient({
                    client_id: clientId,
                    scope: "https://www.googleapis.com/auth/spreadsheets",
                    callback(response) {
                        if (response.error || !google.accounts.oauth2.hasGrantedAllScopes(
                            response, "https://www.googleapis.com/auth/spreadsheets")) {
                            authReject?.(new Error("Доступ к таблицам не предоставлен."));
                            return;
                        }
                        // Токен живёт только в памяти. В резервную копию и IndexedDB он не попадает.
                        accessToken = response.access_token;
                        tokenExpires = Date.now() + (Number(response.expires_in) - 60) * 1000;
                        authResolve?.();
                    },
                    error_callback(error) {
                        authReject?.(new Error(error.type ?? "Google sign-in cancelled"));
                    }
                });
            },
            signIn() {
                return new Promise((resolve, reject) => {
                    if (!tokenClient) {
                        reject(new Error("Google ещё не готов."));
                        return;
                    }
                    authResolve = resolve;
                    authReject = reject;
                    // Вызов происходит непосредственно из нажатия пользователя.
                    tokenClient.requestAccessToken({ prompt: "" });
                });
            },
            token() {
                return Date.now() < tokenExpires ? accessToken : null;
            },
            signOut() {
                accessToken = null;
                tokenExpires = 0;
            }
        },
        pwa: {
            checkUpdate: () => window.ritmUpdates?.check() ?? false,
            async ready() {
                if (!("serviceWorker" in navigator) || !("caches" in window)) {
                    return false;
                }
                const registration = await navigator.serviceWorker.getRegistration();
                const keys = await caches.keys();
                return Boolean(registration?.active)
                    && keys.some(key => key.startsWith("ritm-" + new URL(document.baseURI).pathname + "-"));
            },
            async install() {
                if (!installPrompt) {
                    return false;
                }
                await installPrompt.prompt();
                const result = await installPrompt.userChoice;
                installPrompt = null;
                return result.outcome === "accepted";
            }
        },
        timer: {
            start(seconds) {
                restDeadline = Date.now() + seconds * 1000;
                localStorage.setItem("ritm-rest-deadline", String(restDeadline));
                updateTimer();
            },
            stop() {
                restDeadline = 0;
                localStorage.removeItem("ritm-rest-deadline");
                updateTimer();
            }
        },
        download(name, content) {
            const url = URL.createObjectURL(new Blob([content], { type: "application/json" }));
            const anchor = document.createElement("a");
            anchor.href = url;
            anchor.download = name;
            anchor.click();
            setTimeout(() => URL.revokeObjectURL(url), 1000);
        }
    };
})();
