// A new publish installs beside the running one and waits (sw.js). The page moves to it when that loses nothing: at
// once while this tab holds no game (`idle`) and no field is being typed in, otherwise when the player presses the
// Update button in the header. A game in progress is never reloaded from under the player.
function updater(idle) {
    const button = document.getElementById("update");
    // A first visit has no controller: the worker claiming the page then is not an update.
    const updating = !!navigator.serviceWorker.controller;
    let waiting = null,
        asked = false,
        lastCheck = performance.now();
    const apply = () => {
        asked = true;
        button.disabled = true;
        // Another tab may have moved to the new version already; then only this page is left to reload.
        if (waiting) waiting.postMessage({ type: "skip-waiting" });
        else location.reload();
    };
    const offer = () => {
        const typing = document.activeElement?.matches("input, textarea, select");
        if (idle() && !typing) apply();
        else button.hidden = false;
    };
    button.onclick = apply;
    navigator.serviceWorker.addEventListener("controllerchange", () => {
        if (!updating) return;
        waiting = null;
        if (asked || idle()) location.reload();
        else button.hidden = false;
    });
    return {
        watch(registration) {
            const found = (worker) => {
                if (!navigator.serviceWorker.controller) return;
                waiting = worker;
                offer();
            };
            // Downloaded on an earlier visit, while another copy of the page kept the old version in use.
            if (registration.waiting) found(registration.waiting);
            registration.addEventListener("updatefound", () => {
                const next = registration.installing;
                next?.addEventListener("statechange", () => {
                    if (next.state === "installed") found(next);
                });
            });
            // An installed game can stay open for days: look again when it comes back into view, at most every
            // ten minutes (the browser itself looks only when the page is loaded).
            document.addEventListener("visibilitychange", () => {
                if (document.hidden || performance.now() - lastCheck < 600_000) return;
                lastCheck = performance.now();
                registration.update().catch(() => {});
            });
        },
    };
}

export async function setupInstallation(notify, idle) {
    const button = document.getElementById("install");
    const status = document.getElementById("offline");
    let prompt = null,
        offlineReady = false,
        offlineFailed = false;
    const installed = () =>
        matchMedia("(display-mode: standalone)").matches ||
        navigator.standalone;
    // Says something only when there is something to say: a plain "ONLINE" beside the buttons read like a way to
    // play, next to the Online tab that is one. The text is set only with the state it names, which the smoke test
    // waits for before it goes offline.
    const update = () => {
        const [text, title] = !navigator.onLine
            ? ["OFFLINE · SOLO ONLY", "Without a connection only solo play works."]
            : offlineReady
              ? ["SAVED FOR OFFLINE", "The complete game is kept on this device, so solo play works without a connection."]
              : offlineFailed
                ? ["ONLINE ONLY", "Offline preparation failed. Check your connection and available device storage, then reload."]
                : ["", ""];
        status.hidden = !text;
        status.textContent = text;
        status.title = title;
    };
    window.addEventListener("online", update);
    window.addEventListener("offline", update);
    window.addEventListener("beforeinstallprompt", (event) => {
        event.preventDefault();
        prompt = event;
        button.hidden = installed();
    });
    window.addEventListener("appinstalled", () => {
        button.hidden = true;
        prompt = null;
        notify("Tunnerer installed.");
    });
    button.onclick = async () => {
        if (!prompt) return;
        const availablePrompt = prompt;
        prompt = null;
        button.hidden = true;
        await availablePrompt.prompt();
        await availablePrompt.userChoice;
    };
    update();
    if (!("serviceWorker" in navigator) || !isSecureContext) {
        document.getElementById("install-hint").textContent =
            "Installation and offline play require HTTPS (or localhost). You can still play while this page is open.";
        return;
    }
    try {
        const updates = updater(idle);
        const registration = await navigator.serviceWorker.register("./sw.js", {
            updateViaCache: "none",
        });
        navigator.serviceWorker.addEventListener("message", ({ data }) => {
            if (data?.type === "offline-ready") {
                offlineReady = true;
                update();
            }
        });
        const ready = await navigator.serviceWorker.ready;
        ready.active?.postMessage({ type: "status" });
        updates.watch(registration);
    } catch (error) {
        console.warn("Offline installation unavailable:", error);
        offlineFailed = true;
        update();
    }
}
