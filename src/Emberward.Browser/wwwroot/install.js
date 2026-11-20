// A new publish installs beside the running one and waits (sw.js). The page moves to it when that loses nothing: at
// once while this tab holds no game and nothing to read (`idle`), no field is being typed in and no other Emberflow
// tab is open (the worker checks that), otherwise when the player presses the Update button in the header. A game in
// progress is never reloaded from under the player.
// The footer says which publish this page runs and which is the latest on the server, so that whoever deployed can
// see an update arrive. The running one is what the worker that served the page holds (the first answer only: a
// worker that takes over later did not serve this page). The latest is read past the worker, which never keeps
// assets-manifest.js (publish.py).
function versionLine() {
    const line = document.getElementById("version");
    const short = (version) => version?.slice(0, 8);
    let running = null,
        latest = null;
    const show = () => {
        line.hidden = !running;
        line.textContent = !latest || latest === running
            ? `VERSION ${short(running)}${latest ? " · UP TO DATE" : ""}`
            : `VERSION ${short(running)} · LATEST ${short(latest)}`;
    };
    return {
        running(version) {
            running ??= version;
            show();
        },
        async refresh() {
            // Offline there is nothing to ask, and the browser would log the failed request as an error.
            if (!navigator.onLine) {
                latest = null;
                show();
                return;
            }
            try {
                const text = await (await fetch("./assets-manifest.js", { cache: "no-store" })).text();
                latest = /"version": "(\w+)"/.exec(text)?.[1] ?? null;
            } catch {
                latest = null;
            }
            show();
        },
    };
}

function updater(idle, versions) {
    const button = document.getElementById("update");
    // Whether a worker controlled the page before the latest change: the first one claiming a page on its first
    // visit is not an update.
    let controlled = !!navigator.serviceWorker.controller,
        waiting = null,
        asked = false,
        lastCheck = performance.now();
    const ask = (force) => {
        // Another tab may have moved to the new version already; then only this page is left to reload.
        if (!waiting) location.reload();
        else waiting.postMessage({ type: "skip-waiting", force });
    };
    button.onclick = () => {
        asked = true;
        button.disabled = true;
        ask(true);
    };
    const offer = () => {
        const typing = document.activeElement?.matches("input, textarea, select");
        if (idle() && !typing) {
            asked = true;
            ask(false);
        } else button.hidden = false;
    };
    navigator.serviceWorker.addEventListener("message", ({ data }) => {
        // The worker held back because another tab is open: that tab may hold a game.
        if (data?.type === "update-held") {
            asked = false;
            button.hidden = false;
        }
    });
    navigator.serviceWorker.addEventListener("controllerchange", () => {
        const update = controlled;
        controlled = true;
        if (!update && !asked) return;
        waiting = null;
        if (asked || idle()) location.reload();
        else button.hidden = false;
    });
    return {
        watch(registration) {
            const found = (worker) => {
                versions.refresh();
                if (!navigator.serviceWorker.controller) return;
                // A newer publish can replace one this page already asked for, which then never takes over.
                waiting = worker;
                asked = false;
                button.disabled = false;
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
                versions.refresh();
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
        notify("Emberflow installed.");
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
        const versions = versionLine();
        versions.refresh();
        const updates = updater(idle, versions);
        const registration = await navigator.serviceWorker.register("./sw.js", {
            updateViaCache: "none",
        });
        navigator.serviceWorker.addEventListener("message", ({ data }) => {
            if (data?.type === "offline-ready") {
                if (data.version) versions.running(data.version);
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
