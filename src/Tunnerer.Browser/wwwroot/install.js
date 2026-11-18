export async function setupInstallation(notify) {
    const button = document.getElementById("install");
    const status = document.getElementById("offline");
    let prompt = null,
        offlineReady = false;
    const installed = () =>
        matchMedia("(display-mode: standalone)").matches ||
        navigator.standalone;
    // Says something only when there is something to say: a plain "ONLINE" beside the buttons read like a way to
    // play, next to the Online tab that is one.
    const update = () => {
        status.hidden = navigator.onLine && !offlineReady;
        status.textContent = !navigator.onLine
            ? "OFFLINE · SOLO ONLY"
            : "SAVED FOR OFFLINE";
        status.title = offlineReady
            ? "The complete game is kept on this device, so solo play works without a connection."
            : "Without a connection only solo play works.";
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
        registration.addEventListener("updatefound", () => {
            const next = registration.installing;
            next?.addEventListener("statechange", () => {
                if (
                    next.state === "installed" &&
                    navigator.serviceWorker.controller
                )
                    notify(
                        "An update is ready. Close all Tunnerer tabs and reopen to apply it.",
                    );
            });
        });
    } catch (error) {
        console.warn("Offline installation unavailable:", error);
        status.textContent = "ONLINE ONLY";
        status.title =
            "Offline preparation failed. Check your connection and available device storage, then reload.";
    }
}
