// What the page is doing while a match loads: each stage (the shaders, the runtime, the world, the first frame) as
// it begins and ends, with its time. A stage shows on the loading line, goes to the console as "[load] ...", and is
// sent as a `tunnerer-load` event on window ({ stage, label, state, ms }, the state "begin", "end", "failed" or
// "unfinished": still under way when another stage failed); the list so
// far is window.tunnererLoad, to copy into a bug report. A stage that runs long says so on the loading line, so a
// load that hangs shows where.
const SLOW_MS = 10000;

export class LoadProgress {
    constructor(show) {
        this.show = show;
        this.open = new Map();
        this.log = [];
        this.timer = 0;
    }

    // Starts over for a new load.
    reset() {
        this.open.clear();
        this.culprit = null;
        this.log = [];
        window.tunnererLoad = this.log;
        this.started = performance.now();
        clearInterval(this.timer);
        this.timer = 0;
    }

    begin(stage, label) {
        const at = performance.now();
        this.open.set(stage, { label, at });
        this.record(stage, label, "begin", 0);
        this.show(`${label}…`);
        this.timer ||= setInterval(() => this.watch(), 1000);
    }

    end(stage, detail = "") {
        const entry = this.open.get(stage);
        if (!entry) return;
        this.open.delete(stage);
        const ms = Math.round(performance.now() - entry.at);
        this.record(stage, entry.label, "end", ms, detail);
        console.info(`[load] ${entry.label}: ${ms} ms${detail ? ` (${detail})` : ""}`);
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
        const entry = this.current();
        if (!entry) return this.stop();
        const ms = performance.now() - entry.at;
        if (ms >= SLOW_MS) this.show(`${entry.label}… still working after ${Math.round(ms / 1000)} s.`);
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
