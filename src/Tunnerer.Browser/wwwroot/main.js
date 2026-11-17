import { GameInput } from "./input.js";
import { GameRenderer } from "./renderer.js";
import { GameAudio } from "./audio.js";
import { setupInstallation } from "./install.js";
import { PerfReadout } from "./perf.js";
import { Simulation } from "./simulation.js";

const $ = (id) => document.getElementById(id);
const canvas = $("game"),
    renderer = new GameRenderer(canvas),
    audio = new GameAudio();
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
// Add ?perf to the address for a readout of frame and simulation times.
const perf = new URLSearchParams(location.search).has("perf")
    ? new PerfReadout(renderer)
    : null;
$("upgrades").open = !matchMedia("(pointer: coarse)").matches;
const input = new GameInput(canvas, (action, direction) => {
    if (action === "pause") {
        if ($("menu").open) $("menu").close();
        else if (playing) openMenu();
    }
    if (action === "help") openMenu();
    if (action === "zoom")
        renderer.zoom = Math.max(
            2,
            Math.min(8, renderer.zoom + direction * 0.5),
        );
});

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
    worker = null;
    state = null;
    renderer.frame = null;
    playing = busy = paused = false;
    input.enabled = false;
    input.clear();
    accumulator = 0;
    visualTime = 0;
    for (const id of ["hud", "mission", "upgrades", "touch", "death"])
        $(id).hidden = true;
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
            audio.play(state.events);
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
            $("hud").hidden = $("mission").hidden = false;
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
        }
    };
    worker.postMessage({
        type: "start",
        seed,
        time: 0,
        ...renderer.viewSize(),
    });
}

// `advice` follows the reason; graphics failures bring their own.
function fail(message, advice = " Check your connection and try again.") {
    console.error(message);
    reset();
    const reason = String(message).split("\n")[0].replace(/\.$/, "");
    $("loading").textContent = `Could not start the game: ${reason}.${advice}`;
}

let noticeUntil = 0,
    lastRefusal = null;
function notify(message) {
    $("notice").textContent = message;
    $("notice").classList.add("visible");
    noticeUntil = performance.now() + 3500;
}

function updateHud(f) {
    const p = f.player;
    canvas.dataset.tick = String(f.tick);
    $("temperature").textContent = `${Math.round(p.heat)}°`;
    $("temperature").style.color =
        p.heat > p.heatLimit ? "#ff8260" : p.heat < -20 ? "#80b7e8" : "";
    $("heat").max = p.heatLimit;
    $("heat").value = Math.max(0, p.heat);
    $("shield").value = p.shield;
    $("shield-text").textContent = `${Math.round(p.shield * 100)}%`;
    $("lives").textContent = `${p.lives} ${p.lives === 1 ? "LIFE" : "LIVES"}`;
    $("network").textContent = p.docked
        ? "DOCKED"
        : p.offNetwork
          ? `OFF GRID / ${Math.ceil(p.safeSeconds)}s`
          : "ON NETWORK";
    $("network").style.color = p.offNetwork ? "#ffad72" : "#6edace";
    $("position").textContent =
        `X ${p.position.x} / Y ${p.position.y} · WORLD ${f.seed}`;
    $("ore").textContent = `${p.ore}/${p.holdCapacity} ORE · BASE ${p.store}`;
    $("power").textContent =
        `BASE CHARGE ${Math.round(p.charge * 100)}% / HEAT STORE ${Math.round(p.baseHeat * 100)}%`;
    $("carrying").textContent = p.carrying
        ? `CARRYING ${p.carrying.toUpperCase()} · ${placementText(f.preview?.status)}`
        : "Build a machine, carry it, then press its key again to plant.";
    if (!$("builds").children.length) {
        for (const b of f.builds) {
            const button = document.createElement("button");
            button.dataset.key = String(b.key);
            button.onclick = () => input.queue("build", b.key);
            const key = document.createElement("kbd");
            key.textContent = ["E", "R", "T"][b.key - 1];
            const name = document.createElement("span");
            name.textContent = b.name;
            const cost = document.createElement("small");
            button.append(key, name, cost);
            $("builds").append(button);
        }
    }
    for (const b of f.builds) {
        const button = $("builds").querySelector(`[data-key="${b.key}"]`);
        button.querySelector("small").textContent = `${b.cost} ORE`;
        button.title = p.carrying
            ? `Plant carried ${p.carrying}`
            : `Build ${b.name}: ${b.cost} ore (${b.payer})`;
        button.style.opacity = b.affordable || p.carrying ? "1" : ".5";
    }
    $("upgrades").hidden = !p.docked || p.dead;
    if (!$("upgrade-list").children.length) {
        for (const u of f.upgrades) {
            const button = document.createElement("button");
            button.onclick = () => input.queue("upgrade", u.key);
            $("upgrade-list").append(button);
        }
    }
    for (const u of f.upgrades) {
        const button = $("upgrade-list").children[u.key - 1];
        button.textContent = `${u.key} · ${u.name} / L${u.level}`;
        const price = document.createElement("small");
        price.textContent = u.maxed ? "MAX LEVEL" : `${u.cost} ore`;
        button.append(price);
        button.title = u.effect;
        button.disabled = u.maxed || p.store < u.cost;
    }
    $("guidance").textContent = p.docked
        ? "Leave through the north or south door. Relays extend your cooling network; come home to repair and unload."
        : p.offNetwork
          ? "Your heat rises off the network. Return home before overheating, or plant a relay with a clear link to your base."
          : p.carrying
            ? "Plant outside the base. Linked relays move heat home; harvesters mine ore and pylons refill their batteries."
            : "Mine teal emberite. Link relays toward the hot core, then keep its power output above the goal to win.";
    $("goal").textContent = f.goal
        ? `${f.goal.output.toFixed(0)} / ${f.goal.required.toFixed(0)} POWER · ${f.goal.hold.toFixed(0)} / ${f.goal.requiredHold.toFixed(0)}s HELD`
        : "";
    $("death").hidden = !p.dead && p.outcome === "InProgress";
    $("death").textContent =
        p.outcome === "Victory"
            ? "SECTOR SECURED\nOpen the menu for a new world."
            : p.outcome === "Defeat"
              ? "DIVE ENDED\nOpen the menu to try a new world."
              : `TANK LOST\nRespawning in ${Math.ceil(p.respawnSeconds)}s`;
    if (p.refusal && p.refusal !== lastRefusal) {
        notify(
            {
                NotEnoughOre:
                    "Not enough ore. Mine emberite or return to your network.",
                WillNotLink:
                    "No clear link here. Move closer to a powered node or dig a clear tunnel.",
                NotDocked: "Dock at your base to buy upgrades.",
                Maxed: "This upgrade is already at its maximum level.",
            }[p.refusal] || p.refusal,
        );
    }
    lastRefusal = p.refusal;
}

function placementText(status) {
    return (
        {
            Live: "LINK READY · PRESS BUILD TO PLANT",
            Ready: "PRESS BUILD TO PLANT",
            InsideBase: "LEAVE BASE TO PLANT",
            Blocked: "DIG A CLEAR LINK",
            TooFar: "MOVE CLOSER TO NETWORK",
            NoNetwork: "NO NETWORK IN REACH",
        }[status] || "PRESS BUILD TO PLANT"
    );
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
