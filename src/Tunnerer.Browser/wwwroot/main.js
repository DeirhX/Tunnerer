import { GameInput } from "./input.js";
import { GameRenderer } from "./renderer.js";
import { GameAudio } from "./audio.js";
import { setupInstallation } from "./install.js";
import { PerfReadout } from "./perf.js";
import { Simulation } from "./simulation.js";
import { GuidanceView } from "./guidance.js";
import { StatusView } from "./hud.js";

const $ = (id) => document.getElementById(id);
const canvas = $("game"),
    renderer = new GameRenderer(canvas),
    audio = new GameAudio(),
    guidance = new GuidanceView(() => (renderer.pointerTarget = guidance.target));
let worker = null,
    busy = false,
    playing = false,
    paused = false,
    state = null;
let lastTime = performance.now(),
    accumulator = 0,
    visualTime = 0,
    lastRenderTime = 0;
// The view last asked of the worker, and whether the frame shown while paused has been asked for.
let sentView = {},
    pausedFrameSent = false;
// Add ?perf to the address for a readout of frame and simulation times, and ?map=3000x1500 and ?machines=400 for a
// larger map and networks of that many machines grown from every base before play (docs/browser.md), to measure by.
const query = new URLSearchParams(location.search);
const [mapWidth, mapHeight] = (query.get("map") || "")
    .split("x")
    .map((n) => Number.parseInt(n, 10) || 0);
const machines = Number.parseInt(query.get("machines"), 10) || 0;
const perf = query.has("perf")
    ? new PerfReadout(renderer)
    : null;
$("upgrades").open = !matchMedia("(pointer: coarse)").matches;
const input = new GameInput(canvas, (action, direction) => {
    if (action === "pause") {
        if ($("menu").open) $("menu").close();
        else if (playing) openMenu();
    }
    if (action === "help") openMenu();
    if (action === "tutorial" && playing) guidance.toggleTutorial();
    if (action === "zoom")
        renderer.zoom = Math.max(
            2,
            Math.min(8, renderer.zoom + direction * 0.5),
        );
});
const statusView = new StatusView(input);

function randomSeed() {
    const values = new Int32Array(1);
    crypto.getRandomValues(values);
    $("seed").value = String(values[0]);
}
randomSeed();
$("randomize").onclick = randomSeed;
$("start").onclick = start;
$("pause").onclick = openMenu;
$("help").onclick = openMenu;
$("resume").onclick = () => $("menu").close();
$("restart").onclick = () => {
    playing = false;
    $("menu").close();
    reset();
    randomSeed();
    $("loading").textContent =
        "Ready for a fresh world. Your previous match has ended.";
};
$("sound").onclick = async () => {
    try {
        const enabled = await audio.toggle();
        $("sound").textContent = enabled ? "Sound on" : "Sound off";
        $("sound").setAttribute("aria-pressed", String(enabled));
    } catch {
        notify("Audio is unavailable in this browser.");
    }
};
$("menu").addEventListener("close", () => {
    paused = false;
    audio.setPaused(false);
    input.clear();
    input.enabled = playing && state?.player.outcome === "InProgress";
    accumulator = 0;
    lastTime = performance.now();
    if (playing) canvas.focus({ preventScroll: true });
});
window.addEventListener("blur", () => {
    input.clear();
    if (playing) openMenu();
});
document.addEventListener("visibilitychange", () => {
    if (document.hidden) {
        input.clear();
        if (playing) openMenu();
    }
});

function openMenu() {
    if ($("menu").open) return;
    paused = true;
    audio.setPaused(true);
    input.enabled = false;
    input.clear();
    $("menu-title").textContent =
        state?.player.outcome === "Victory"
            ? "Core tapped. Well done."
            : state?.player.outcome === "Defeat"
              ? "Your next dive awaits."
              : "Take a breather.";
    $("restart").hidden = !playing;
    $("session-seed").textContent = state
        ? `WORLD SEED ${state.seed} · TICK ${state.tick}`
        : "";
    $("menu").showModal();
}

function reset() {
    worker?.terminate();
    audio.quiet();
    // A sound bank asked of the ended match never comes: the next match asks again.
    audio.requested = false;
    worker = null;
    state = null;
    renderer.frame = null;
    playing = busy = paused = false;
    input.enabled = false;
    input.clear();
    accumulator = 0;
    visualTime = 0;
    for (const id of ["hud", "guide", "upgrades", "touch", "death", "hit"])
        $(id).hidden = true;
    guidance.clear();
    renderer.pointerTarget = null;
    $("welcome").hidden = false;
    $("footer").hidden = false;
    $("start").disabled = false;
    $("pause").disabled = true;
    $("start").textContent = "Enter the underground →";
    document.body.classList.remove("playing");
}

async function start() {
    const seed = Number($("seed").value);
    if (
        !$("seed").value ||
        !Number.isInteger(seed) ||
        seed < -2147483648 ||
        seed > 2147483647
    ) {
        $("loading").textContent =
            "Enter a whole-number seed between −2147483648 and 2147483647.";
        return;
    }
    if (!globalThis.WebAssembly || !globalThis.Worker) {
        $("loading").textContent =
            "This game needs a browser with WebAssembly and Web Workers. Try a current Chrome, Edge, Firefox, or Safari.";
        return;
    }
    $("start").disabled = true;
    $("start").textContent = "Preparing your world…";
    $("loading").textContent =
        "Loading the game and generating terrain. The first launch may take a moment.";
    busy = true;
    try {
        await renderer.initialize();
    } catch (error) {
        fail(error.message, error.name === "GraphicsError" ? "" : undefined);
        return;
    }
    // A worker with the single-thread runtime, or the threaded runtime behind the same messages (simulation.js).
    worker = new Simulation();
    worker.onerror = (e) =>
        fail(e.message || "The game worker could not start.");
    worker.onmessage = ({ data }) => {
        if (data.type === "error") {
            fail(data.message);
            return;
        }
        if (data.type !== "frame") return;
        busy = false;
        if (data.sounds) audio.load(data.sounds);
        // The HUD's state comes only with frames that follow a simulation step.
        renderer.accept(data.state, data.packet);
        perf?.frame(
            data.timing,
            renderer.gpu.uploadedBytes,
            worker.heatWorkers,
        );
        input.aimOffset = renderer.cellSize / 2;
        if (data.state) {
            state = data.state;
            audio.play(state.sounds);
            updateHud(state);
            // For whoever follows the match from outside the page's code: the smoke test does.
            canvas.dataset.heatWorkers = String(worker.heatWorkers);
            window.dispatchEvent(
                new CustomEvent("tunnerer-step", { detail: state }),
            );
        }
        if (!playing) {
            playing = true;
            $("welcome").hidden = true;
            $("footer").hidden = true;
            $("hud").hidden = $("guide").hidden = false;
            $("touch").hidden = !matchMedia("(pointer: coarse)").matches;
            $("pause").disabled = false;
            document.body.classList.add("playing");
            input.enabled = !paused;
            lastTime = performance.now();
            accumulator = 0;
            canvas.focus({ preventScroll: true });
            if (document.hidden || $("menu").open) openMenu();
        }
        if (state.player.outcome !== "InProgress") {
            input.enabled = false;
            input.clear();
            audio.quiet();
        }
    };
    worker.postMessage({
        type: "start",
        seed,
        time: 0,
        sounds: askForSounds(),
        mapWidth: mapWidth || 0,
        mapHeight: mapHeight || 0,
        machines,
        ...renderer.viewSize(),
    });
}

// Whether this request should bring the sound bank: once, after sound is first turned on.
function askForSounds() {
    if (!audio.wantsBank) return false;
    audio.requested = true;
    return true;
}

// `advice` follows the reason; graphics failures bring their own.
function fail(message, advice = " Check your connection and try again.") {
    console.error(message);
    reset();
    const reason = String(message).split("\n")[0].replace(/\.$/, "");
    $("loading").textContent = `Could not start the game: ${reason}.${advice}`;
}

let noticeUntil = 0;
function notify(message) {
    $("notice").textContent = message;
    $("notice").classList.add("visible");
    noticeUntil = performance.now() + 3500;
}

function updateHud(f) {
    canvas.dataset.tick = String(f.tick);
    statusView.update(f);
    guidance.update(f.guidance, f.goal);
    renderer.pointerTarget = guidance.target;
}

function animate(now) {
    const dt = 1000 / (state?.tickRate || 24);
    const elapsed = Math.max(0, now - lastTime);
    if (playing && !paused && !document.hidden)
        visualTime += Math.min(elapsed, dt * 4) / 1000;
    accumulator =
        playing && !paused && !document.hidden
            ? Math.min(accumulator + elapsed, dt * 4)
            : 0;
    lastTime = now;
    renderer.governor.pace(elapsed, playing && !paused && !document.hidden);
    const view = renderer.viewSize();
    // A paused world stands still: one frame shows it, and another is only needed when the window changes size.
    const wanted =
        !paused ||
        !pausedFrameSent ||
        view.width !== sentView.width ||
        view.height !== sentView.height ||
        view.scale !== sentView.scale;
    if (playing && !busy && !document.hidden && wanted) {
        const step =
            !paused &&
            state.player.outcome === "InProgress" &&
            accumulator >= dt;
        if (step) accumulator -= dt;
        busy = true;
        worker.postMessage({
            type: "render",
            input: step ? input.read() : null,
            alpha: paused ? 1 : Math.min(1, accumulator / dt),
            delta: paused ? 0 : Math.min(0.1, (now - lastRenderTime) / 1000),
            time: visualTime,
            sounds: askForSounds(),
            ...view,
        });
        sentView = view;
        pausedFrameSent = paused;
        lastRenderTime = now;
    }
    try {
        renderer.draw(now);
        perf?.displayed(elapsed);
    } catch (error) {
        fail(error.message, error.name === "GraphicsError" ? "" : undefined);
    }
    if (now > noticeUntil) $("notice").classList.remove("visible");
    requestAnimationFrame(animate);
}
setupInstallation(notify);
requestAnimationFrame(animate);
