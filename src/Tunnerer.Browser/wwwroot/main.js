import { GameInput } from "./input.js";
import { GameRenderer } from "./renderer.js";
import { GameAudio } from "./audio.js";
import { setupInstallation } from "./install.js";
import { PerfReadout } from "./perf.js";
import { Simulation } from "./simulation.js";
import { GuidanceView } from "./guidance.js";
import { StatusView } from "./hud.js";
import { StripView } from "./strip.js";
import { RelaySocket, defaultRelay } from "./net.js";
import { LoadProgress } from "./progress.js";

const $ = (id) => document.getElementById(id);
const canvas = $("game"),
    renderer = new GameRenderer(canvas),
    audio = new GameAudio(),
    guidance = new GuidanceView(() => (renderer.pointerTarget = guidance.target)),
    progress = new LoadProgress(
        (text) => ($("loading").textContent = text),
        (share) => {
            $("load-bar").hidden = share === null;
            if (share === null) return;
            $("load-bar").firstElementChild.style.transform = `scaleX(${share})`;
            $("load-bar").setAttribute("aria-valuenow", String(Math.round(share * 100)));
        },
    );
let worker = null,
    busy = false,
    playing = false,
    paused = false,
    state = null,
    // The thermal view (H): the ground tinted by its heat, as on the desktop. It changes only what is drawn.
    thermalView = false,
    // A network match (net.js): the socket (once the hello is made), a command for the room waiting to go with the
    // next request, when the last request was sent and when the last one that stepped was.
    net = null;
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
    if (action === "thermal" && playing) setThermalView(!thermalView);
    if (action === "enter" && net && state?.net && !state.net.started)
        netCommand("start");
    if (action === "netpause" && net && playing && state?.net?.started)
        netCommand("pause");
    if (action === "zoom")
        renderer.zoom = Math.max(
            2,
            Math.min(8, renderer.zoom + direction * 0.5),
        );
});
const statusView = new StatusView(input);
// The desktop's art strip in the bottom corners, where the screen is wide and there is a mouse (strip.js); the
// COMMAND button opens the upgrades. Elsewhere the plates show the same.
const strip = new StripView(input, () => ($("upgrades").open = true));
const wideScreen = matchMedia(
    "(pointer: fine) and (min-width: 1000px) and (min-height: 620px)",
);
function layoutHud() {
    document.body.classList.toggle("art-hud", wideScreen.matches);
    if (!wideScreen.matches) return;
    strip.load();
    const main = document.querySelector("main");
    const height = strip.layout(main.clientWidth, main.clientHeight);
    document.body.style.setProperty("--strip-h", `${Math.ceil(height)}px`);
}
// The upgrades stand between the tutorial card and the strip, so they need the card's height.
new ResizeObserver(() =>
    document.body.style.setProperty(
        "--mission-h",
        `${$("mission").offsetHeight}px`,
    ),
).observe($("mission"));
wideScreen.addEventListener("change", layoutHud);
window.addEventListener("resize", layoutHud);
layoutHud();

function setThermalView(on) {
    thermalView = on;
    $("thermal").setAttribute("aria-pressed", String(on));
    document.body.classList.toggle("thermal-view", on);
    // A paused world is drawn once; draw it again in the view just chosen.
    pausedFrameSent = false;
}
$("thermal").onclick = () => {
    if (playing) setThermalView(!thermalView);
    canvas.focus({ preventScroll: true });
};

// A network match does not stop for one player: the menu opens over a running game.
const holding = () => paused && !net;

// Whether the player's controls drive the tank now.
function canControl() {
    return (
        playing &&
        !paused &&
        state?.player.outcome === "InProgress" &&
        (!net || state?.net?.started === true)
    );
}

function netCommand(command) {
    if (!net) return;
    net.command = command;
}

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
    if (net) {
        leave();
        return;
    }
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
    input.enabled = canControl();
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
    // A network match runs on behind the menu with its sound, but a hidden page takes no steps: its loops would
    // drone on at their last values.
    if (net) audio.setPaused(document.hidden);
});

function openMenu() {
    if ($("menu").open) return;
    paused = true;
    if (!net) audio.setPaused(true);
    input.enabled = false;
    input.clear();
    const winner = state?.net?.winner;
    $("menu-title").textContent =
        state?.player.outcome === "Victory"
            ? net
                ? "Core tapped. You won the race."
                : "Core tapped. Well done."
            : state?.player.outcome === "Defeat"
              ? winner
                  ? `${winner} won the race.`
                  : "Your next dive awaits."
              : "Take a breather.";
    $("menu-pause-note").textContent = net
        ? "The match goes on while this menu is open. P pauses it for everyone."
        : "The game pauses when this menu opens or you leave the page.";
    $("restart").textContent = net ? "Leave match" : "New world";
    $("restart").hidden = !playing;
    $("session-seed").textContent = state
        ? `WORLD SEED ${state.seed} · TICK ${state.tick}`
        : "";
    $("menu").showModal();
}

function reset() {
    worker?.terminate();
    net?.socket?.close();
    net = null;
    for (const id of ["lobby", "net-status", "net-latency"]) $(id).hidden = true;
    $("join").disabled = false;
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
    strip.reset();
    setThermalView(false);
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
    await launch({
        type: "start",
        seed,
        mapWidth: mapWidth || 0,
        mapHeight: mapHeight || 0,
        machines,
    });
}

// Joins a room on the relay (net.js): the runtime makes the hello, then the socket opens and sends it, so that a
// slow first download of the game does not keep the relay waiting for it.
async function join(event) {
    event.preventDefault();
    const name = $("net-name").value.trim(),
        room = $("net-room").value.trim(),
        secret = $("net-secret").value;
    if (!name || !room) {
        $("loading").textContent = "Enter your name and a room to join.";
        return;
    }
    if (!globalThis.WebAssembly || !globalThis.Worker || !globalThis.WebSocket) {
        $("loading").textContent =
            "Online play needs a browser with WebAssembly, Web Workers and WebSockets. Try a current Chrome, Edge, Firefox, or Safari.";
        return;
    }
    saveNetSettings({ name, room, secret, players: $("net-players").value });
    net = { socket: null, command: null, requestedAt: 0, stepAt: 0 };
    $("join").disabled = true;
    const values = new Int32Array(1);
    crypto.getRandomValues(values);
    await launch({
        type: "join",
        room,
        name,
        secret,
        players: Number($("net-players").value),
        seed: values[0],
        mapWidth: mapWidth || 0,
        mapHeight: mapHeight || 0,
    });
}

// How long joining may wait for the socket to open, and then for the relay to let the player in.
const JOIN_TIMEOUT_MS = 15000;

// The answer to a "join" request before the world exists: the hello to send, or why the room cannot be joined.
async function onNetAnswer(data) {
    if (data.failure) {
        fail(data.failure, "", "Could not join the room");
        return;
    }
    if (!net.socket) {
        $("start").textContent = "Joining the room…";
        $("loading").textContent = `Connecting to the relay at ${defaultRelay()}.`;
        const socket = new RelaySocket(defaultRelay());
        let timer;
        try {
            await Promise.race([
                socket.opened,
                new Promise((_, reject) => {
                    timer = setTimeout(
                        () => reject(new Error(`The relay at ${defaultRelay()} did not answer`)),
                        JOIN_TIMEOUT_MS,
                    );
                }),
            ]);
        } catch (error) {
            socket.close();
            if (net) fail(error.message);
            return;
        } finally {
            clearTimeout(timer);
        }
        // The join was abandoned while the socket opened.
        if (!net) {
            socket.close();
            return;
        }
        net.socket = socket;
        net.deadline = performance.now() + JOIN_TIMEOUT_MS;
        $("loading").textContent = "Waiting for the relay to let you in.";
    }
    net.socket.send(data.send);
    busy = false;
}

// Leaves the room and goes back to the welcome screen.
function leave() {
    playing = false;
    $("menu").close();
    reset();
    $("loading").textContent = "You left the room. Join again under the same name to come back to the match.";
}

const NET_SETTINGS = "tunnerer.net";
function loadNetSettings() {
    try {
        return JSON.parse(localStorage.getItem(NET_SETTINGS)) || {};
    } catch {
        return {};
    }
}
function saveNetSettings(settings) {
    try {
        localStorage.setItem(NET_SETTINGS, JSON.stringify(settings));
    } catch {
        // Private browsing: the form is filled in again next time.
    }
}
{
    const saved = loadNetSettings();
    $("net-name").value = saved.name || "";
    $("net-room").value = saved.room || "lobby";
    $("net-secret").value = saved.secret || "";
    if (saved.players) $("net-players").value = saved.players;
}
$("online-form").onsubmit = join;
$("lobby-start").onclick = () => {
    netCommand("start");
    canvas.focus({ preventScroll: true });
};
$("lobby-leave").onclick = leave;

// Who is in the room before the start, then what holds the match or how quick the relay answers.
function updateNet(n) {
    if (!n) return;
    const lobby = !n.started && !n.notice;
    $("lobby").hidden = !lobby;
    if (lobby) {
        $("lobby-room").textContent = n.room;
        $("lobby-roster").replaceChildren(
            ...n.roster.map((slot) => {
                const item = document.createElement("li");
                const swatch = document.createElement("i");
                swatch.style.background = slot.color;
                item.append(
                    swatch,
                    slot.open
                        ? "open – a bot plays if nobody joins"
                        : slot.you
                          ? `${slot.name} (you)`
                          : slot.name,
                );
                item.classList.toggle("open", slot.open);
                return item;
            }),
        );
        $("lobby-name").textContent = n.nameLine || "";
        $("lobby-hint").textContent = n.lobbyHint || "";
        $("lobby-start").hidden = !n.isHost;
    }
    $("net-status").hidden = !n.notice;
    $("net-status").textContent = n.notice || "";
    $("net-status").classList.toggle("bad", n.bad);
    const latency = n.started && !n.notice && n.latencyMs != null;
    $("net-latency").hidden = !latency;
    if (latency) {
        $("net-latency").textContent = `NET ${n.latencyMs} ms`;
        $("net-latency").className =
            n.latencyMs < 80 ? "good" : n.latencyMs < 160 ? "warn" : "bad";
    }
    if (!paused) input.enabled = canControl();
}

// Starts the runtime with `request` ("start" or "join") once the graphics are ready.
async function launch(request) {
    $("start").disabled = true;
    $("start").textContent = "Preparing graphics…";
    busy = true;
    progress.reset();
    try {
        await renderer.initialize(progress);
    } catch (error) {
        fail(error.message, error.name === "GraphicsError" ? "" : undefined);
        return;
    }
    $("start").textContent = "Preparing your world…";
    // A worker with the single-thread runtime, or the threaded runtime behind the same messages (simulation.js).
    worker = new Simulation();
    worker.onerror = (e) =>
        fail(e.message || "The game worker could not start.");
    worker.onmessage = ({ data }) => {
        if (data.type === "error") {
            fail(data.message);
            return;
        }
        if (data.type === "progress") {
            if (data.state === "begin") progress.begin(data.stage, data.label);
            else if (data.state === "bytes") progress.bytes(data.stage, data.loaded, data.total);
            else progress.end(data.stage, data.detail);
            return;
        }
        if (data.type === "net" && net) {
            onNetAnswer(data);
            return;
        }
        if (data.type !== "frame") return;
        busy = false;
        if (net) {
            net.socket?.send(data.send);
            if (data.steps > 0) net.stepAt = net.requestedAt;
        }
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
            progress.finish();
            $("welcome").hidden = true;
            $("footer").hidden = true;
            $("hud").hidden = $("guide").hidden = false;
            $("touch").hidden = !matchMedia("(pointer: coarse)").matches;
            $("pause").disabled = false;
            document.body.classList.add("playing");
            input.enabled = canControl();
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
        ...request,
        time: 0,
        now: performance.now(),
        sounds: askForSounds(),
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
function fail(
    message,
    advice = " Check your connection and try again.",
    what = "Could not start the game",
) {
    console.error(message);
    // Where the load was, for the message and the console: "while compiling the terrain shaders, 12 s in".
    const during = progress.fail();
    reset();
    const reason = String(message).split("\n")[0].replace(/\.$/, "");
    $("loading").textContent = `${what}${during}: ${reason}.${advice}`;
}

let noticeUntil = 0;
function notify(message) {
    $("notice").textContent = message;
    $("notice").classList.add("visible");
    noticeUntil = performance.now() + 3500;
}

function updateHud(f) {
    canvas.dataset.tick = String(f.tick);
    const began = performance.now();
    statusView.update(f);
    const stripBegan = performance.now();
    strip.update(f, visualTime, wideScreen.matches);
    const stripMs = performance.now() - stripBegan;
    guidance.update(f.guidance, f.goal);
    updateNet(f.net);
    renderer.pointerTarget = guidance.target;
    perf?.hud(performance.now() - began, stripMs);
}

function animate(now) {
    const dt = 1000 / (state?.tickRate || 24);
    const elapsed = Math.max(0, now - lastTime);
    const running = playing && !holding() && !document.hidden;
    if (running) visualTime += Math.min(elapsed, dt * 4) / 1000;
    accumulator = running ? Math.min(accumulator + elapsed, dt * 4) : 0;
    lastTime = now;
    renderer.governor.pace(elapsed, running);
    const view = renderer.viewSize();
    // Joining a room: until the relay lets the player in there is no world to draw, only messages to hand over.
    if (net?.deadline && !playing && !busy && now > net.deadline) {
        fail("the relay did not let you in", " Check the room and try again.", "Could not join the room");
    } else if (net?.socket?.pending && !playing && !busy && worker) {
        busy = true;
        worker.postMessage({
            type: "render",
            ...net.socket.take(),
            now,
            thermalView,
            ...view,
        });
    }
    // A paused world stands still: one frame shows it, and another is only needed when the window changes size.
    const wanted =
        !holding() ||
        !pausedFrameSent ||
        view.width !== sentView.width ||
        view.height !== sentView.height ||
        view.scale !== sentView.scale;
    if (playing && !busy && !document.hidden && wanted) {
        let request;
        if (net) {
            // A network match steps as the relay's ticks arrive: the controls go with every request, and a step
            // is drawn from the time the request that took it was sent.
            const due = accumulator >= dt;
            if (due) accumulator -= dt;
            request = {
                ...net.socket.take(),
                command: net.command,
                input: input.read(),
                due,
                now,
                alpha: Math.min(1, (now - net.stepAt) / dt),
            };
            net.command = null;
            net.requestedAt = now;
        } else {
            const step =
                !paused &&
                state.player.outcome === "InProgress" &&
                accumulator >= dt;
            if (step) accumulator -= dt;
            request = {
                input: step ? input.read() : null,
                alpha: paused ? 1 : Math.min(1, accumulator / dt),
            };
        }
        busy = true;
        worker.postMessage({
            type: "render",
            ...request,
            delta: holding() ? 0 : Math.min(0.1, (now - lastRenderTime) / 1000),
            time: visualTime,
            thermalView,
            sounds: askForSounds(),
            ...view,
        });
        sentView = view;
        pausedFrameSent = holding();
        lastRenderTime = now;
    }
    try {
        renderer.draw(now, input.pointer);
        perf?.displayed(elapsed);
    } catch (error) {
        fail(error.message, error.name === "GraphicsError" ? "" : undefined);
    }
    if (now > noticeUntil) $("notice").classList.remove("visible");
    requestAnimationFrame(animate);
}
setupInstallation(notify);
requestAnimationFrame(animate);
