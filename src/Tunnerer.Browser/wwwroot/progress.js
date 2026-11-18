// What the page is doing while a match loads: each stage (the shaders, the runtime, the world, the first frame) as
// it begins and ends, with its time. A stage shows on the loading line, goes to the console as "[load] ...", and is
// sent as a `tunnerer-load` event on window ({ stage, label, state, ms }, the state "begin", "end", "failed" or
// "unfinished": still under way when another stage failed); the list so
// far is window.tunnererLoad, to copy into a bug report. A stage that runs long says so on the loading line, so a
// load that hangs shows where.
//
// The loading bar fills through the load's phases in turn, each as wide as it took on this device last time (or a
// guess the first time). The download fills by the bytes that have arrived. Nothing else says how far along it is
// (a shader compile, for one, is only done or not), so those fill by the time they have taken against the time
// they took last time, and slow down short of the end instead of stopping there.
const SLOW_MS = 10000;
const TICK_MS = 100;
const SAVED = "tunnerer.load-times";
// The phases in order, with the time each is guessed to take on a first load: the shaders take a moment on a Mac and
// several seconds on Windows, the runtime starts as soon as it has downloaded, and making a world takes a few seconds.
const PHASES = { shaders: 2000, download: 6000, runtime: 300, world: 2500 };
const phaseOf = (stage) => (stage.startsWith("shaders") ? "shaders" : stage);

export class LoadProgress {
    // `show` puts a line of text on the loading line; `bar` sets the loading bar to a share from 0 to 1, or hides it
    // with null.
    constructor(show, bar = () => {}) {
        this.show = show;
        this.bar = bar;
        this.open = new Map();
        this.log = [];
        this.timer = 0;
        this.phases = {};
    }

    // Starts over for a new load.
    reset() {
        this.open.clear();
        this.culprit = null;
        this.log = [];
        window.tunnererLoad = this.log;
        this.started = performance.now();
        this.stop();
        const saved = loadTimes();
        this.phases = Object.fromEntries(
            Object.entries(PHASES).map(([name, guess]) => [name, { expected: saved[name] || guess }]),
        );
        this.shown = 0;
        this.bar(0);
    }

    begin(stage, label) {
        const at = performance.now();
        this.open.set(stage, { label, at });
        this.record(stage, label, "begin", 0);
        this.show(`${label}…`);
        // A phase begun means those before it are over, or were not needed this time (a runtime already started).
        const names = Object.keys(this.phases),
            phase = phaseOf(stage);
        for (const name of names.slice(0, names.indexOf(phase))) this.phases[name].done = true;
        if (this.phases[phase]) {
            // The shaders' phase is a download and then three compiles: it is over when the last of them is.
            this.phases[phase].at ??= at;
            this.phases[phase].done = false;
        }
        this.timer ||= setInterval(() => this.watch(), TICK_MS);
    }

    // How much of a download has arrived.
    bytes(stage, loaded, total) {
        const phase = this.phases[phaseOf(stage)];
        if (phase && total > 0) phase.share = Math.min(1, loaded / total);
    }

    end(stage, detail = "") {
        const entry = this.open.get(stage);
        if (!entry) return;
        this.open.delete(stage);
        const ms = Math.round(performance.now() - entry.at);
        this.record(stage, entry.label, "end", ms, detail);
        console.info(`[load] ${entry.label}: ${ms} ms${detail ? ` (${detail})` : ""}`);
        const name = phaseOf(stage),
            phase = this.phases[name];
        if (phase && ![...this.open.keys()].some((open) => phaseOf(open) === name)) {
            phase.done = true;
            phase.took = performance.now() - phase.at;
        }
        this.watch();
        const next = this.current();
        if (next) this.show(`${next.label}…`);
        else this.stop();
    }

    // The first frame is on screen.
    finish() {
        if (!this.log.length) return;
        const ms = Math.round(performance.now() - this.started);
        this.record("ready", "Ready to play", "end", ms);
        console.info(`[load] ready to play: ${ms} ms in all`);
        this.open.clear();
        this.stop();
        this.bar(null);
        // What each phase took, for the bar next time. A phase not needed this time keeps what it took before.
        const times = loadTimes();
        for (const [name, phase] of Object.entries(this.phases))
            if (phase.took !== undefined) times[name] = Math.max(1, Math.round(phase.took));
        saveTimes(times);
    }

    // The stage that went wrong says so, before the error reaches the page.
    failed(stage) {
        const entry = this.open.get(stage);
        if (!entry) return;
        this.open.delete(stage);
        this.culprit = { ...entry, ms: performance.now() - entry.at };
        this.record(stage, entry.label, "failed", Math.round(this.culprit.ms));
    }

    // The load stopped: returns what it was doing, as "while compiling the terrain shaders, 12 s in": the stage that
    // said it failed, else the one open longest, else "". Stages still open are recorded as unfinished, and the list
    // goes to the console.
    fail() {
        const open = this.current();
        const entry = this.culprit || (open && { ...open, ms: performance.now() - open.at });
        for (const [stage, rest] of this.open)
            this.record(stage, rest.label, "unfinished", Math.round(performance.now() - rest.at));
        this.open.clear();
        this.stop();
        this.bar(null);
        if (this.log.length) console.table(this.log);
        if (!entry) return "";
        const seconds = Math.round(entry.ms / 1000);
        return ` while ${lower(entry.label)}${seconds >= 1 ? `, ${seconds} s in` : ""}`;
    }

    // The stage open longest: the one the others wait for.
    current() {
        let first = null;
        for (const entry of this.open.values()) if (!first || entry.at < first.at) first = entry;
        return first;
    }

    watch() {
        this.shown = Math.max(this.shown, this.share());
        this.bar(this.shown);
        const entry = this.current();
        if (!entry) return this.stop();
        const ms = performance.now() - entry.at;
        if (ms >= SLOW_MS) this.show(`${entry.label}… still working after ${Math.round(ms / 1000)} s.`);
    }

    // How far the whole load is, from 0 to 1.
    share() {
        const now = performance.now();
        let done = 0,
            all = 0;
        for (const phase of Object.values(this.phases)) {
            all += phase.expected;
            if (phase.done) done += phase.expected;
            else if (phase.share !== undefined) done += phase.expected * phase.share;
            else if (phase.at !== undefined) done += phase.expected * estimate(now - phase.at, phase.expected);
        }
        return all > 0 ? done / all : 0;
    }

    stop() {
        clearInterval(this.timer);
        this.timer = 0;
    }

    record(stage, label, state, ms, detail = "") {
        const entry = { stage, label, state, ms, at: Math.round(performance.now() - this.started) };
        if (detail) entry.detail = detail;
        this.log.push(entry);
        window.dispatchEvent(new CustomEvent("tunnerer-load", { detail: entry }));
    }
}

const lower = (label) => label.charAt(0).toLowerCase() + label.slice(1);

// The share of a phase done after `ms`, when it took `expected` last time: steady to nine tenths at that time, then
// ever slower towards the end, so that a slower load than last time still moves and never looks finished.
function estimate(ms, expected) {
    const t = ms / expected;
    return t <= 1 ? 0.9 * t : 0.9 + 0.09 * (1 - Math.exp(1 - t));
}

function loadTimes() {
    try {
        return JSON.parse(localStorage.getItem(SAVED)) || {};
    } catch {
        return {};
    }
}

function saveTimes(times) {
    try {
        localStorage.setItem(SAVED, JSON.stringify(times));
    } catch {
        // Private browsing: the bar guesses again next time.
    }
}
