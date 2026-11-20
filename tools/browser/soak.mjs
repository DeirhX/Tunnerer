// Plays the published game like a person for a while and reports a stall (no simulation step for STALL_MS) or a
// runtime that exited. Setup is the one smoke.mjs documents (playwright in artifacts/browser-tests, serve.py).
//
// SOAK_SECONDS (default 180) how long to play; SOAK_THREADS=1 when the server sends the isolation headers
// (serve.py --isolate); ?threads=N or ?threads=0 can be put in BROWSER_URL; BROWSER_CHANNEL=chrome|msedge uses an
// installed browser; HEADED=1 shows the window; STALL_MS (default 8000).
import { mkdtemp } from "node:fs/promises";
import { resolve, join } from "node:path";
import { tmpdir } from "node:os";
import { pathToFileURL } from "node:url";

const playwright = await import(
    pathToFileURL(process.env.PLAYWRIGHT_MODULE || resolve("artifacts/browser-tests/node_modules/playwright/index.mjs"))
);
const origin = process.env.BROWSER_URL || "http://localhost:8703";
const seconds = Number(process.env.SOAK_SECONDS || 180);
const stallMs = Number(process.env.STALL_MS || 8000);
const context = await playwright[process.env.BROWSER || "chromium"].launchPersistentContext(await mkdtemp(join(tmpdir(), "soak-")), {
    headless: !process.env.HEADED,
    // Headless Chromium on macOS needs Metal for the game's shaders.
    args: process.platform === "darwin" ? ["--use-angle=metal"] : [],
    viewport: { width: 1440, height: 900 },
    deviceScaleFactor: 1,
    ...(process.env.BROWSER_CHANNEL ? { channel: process.env.BROWSER_CHANNEL } : {}),
});
const page = await context.newPage();
const log = [];
page.on("console", (m) => {
    if (["error", "warning"].includes(m.type()) || /\[load\]|\[trace\]|runtime|Assert/i.test(m.text()))
        log.push(`[${m.type()}] ${m.text().slice(0, 3000)}`);
});
page.on("crash", () => log.push("[crash] the page crashed (browser process for the page died)"));
page.on("close", () => log.push("[close] page closed"));
page.on("pageerror", (e) => log.push(`[pageerror] ${String(e).slice(0, 400)}`));
await page.addInitScript(() => {
    window.testSteps = 0;
    window.testLast = performance.now();
    window.testMaxGap = 0;
    window.addEventListener("emberward-step", ({ detail }) => {
        if (window.testSteps) window.testMaxGap = Math.max(window.testMaxGap, performance.now() - window.testLast);
        window.testSteps++;
        window.testLast = performance.now();
        window.testTick = detail.tick;
    });
});
// SOAK_ONLINE=1 plays a network match instead: this player alone in a room of two on the relay SOAK_RELAY (default
// a local one: dotnet run --project src/Emberward.Relay -- --urls http://127.0.0.1:8130), a bot in the other slot.
const relay = process.env.SOAK_RELAY || "ws://127.0.0.1:8130/play";
async function startMatch() {
    if (process.env.SOAK_ONLINE) {
        await page.goto(`${origin}${origin.includes("?") ? "&" : "?"}relay=${encodeURIComponent(relay)}`);
        await page.locator("#mode-online").click();
        await page.locator("#net-name").fill("Soak");
        await page.locator("#net-room").fill(`soak-${Date.now().toString(36)}`);
        await page.locator("#net-players").selectOption("2");
        await page.locator("#join").click();
        await page.locator("#lobby").waitFor({ state: "visible", timeout: 60000 });
        await page.locator("#lobby-start").click();
        await page.waitForFunction(() => !document.getElementById("hud").hidden, null, { timeout: 180000 });
        return;
    }
    await page.goto(origin);
    await page.locator("#seed").fill(process.env.SEED || "1234");
    await page.locator("#start").click();
    await page.waitForFunction(() => !document.getElementById("hud").hidden, null, { timeout: 180000 });
    // SOAK_SOUND=1 turns sound on, which makes .NET synthesise the sound bank and the page play the match's sounds.
    if (process.env.SOAK_SOUND) await page.locator("#sound").click();
}
await startMatch();
let matches = 1;
console.log("match started");

// A page that stops answering input or queries (a hung runtime blocks it) would leave Playwright waiting for ever.
const within = (promise, what, ms = 25000) =>
    Promise.race([promise, new Promise((_, reject) => setTimeout(() => reject(new Error(`${what}: no answer in ${ms / 1000} s`)), ms))]);
const keys = ["KeyW", "KeyA", "KeyS", "KeyD"];
const builds = ["KeyE", "KeyR", "KeyT", "KeyY"];
const began = Date.now();
let failure = null;
let lastReport = 0;
while (Date.now() - began < seconds * 1000 && !failure) {
    const k = keys[Math.floor(Math.random() * 4)];
    try {
        await within(
            (async () => {
                await page.keyboard.down(k);
                await page.mouse.move(200 + Math.random() * 1000, 100 + Math.random() * 700);
                if (Math.random() < 0.6) await page.keyboard.down("Space");
                await page.waitForTimeout(300 + Math.random() * 900);
                await page.keyboard.up("Space");
                await page.keyboard.up(k);
                if (Math.random() < 0.25) await page.keyboard.press(builds[Math.floor(Math.random() * 4)]);
                if (Math.random() < 0.1) await page.keyboard.press(`Digit${1 + Math.floor(Math.random() * 7)}`);
            })(),
            "input",
        );
    } catch (e) {
        failure = `HANG: ${e.message}`;
        break;
    }
    const state = await within(page.evaluate(() => ({
        sinceStep: performance.now() - window.testLast,
        steps: window.testSteps,
        tick: window.testTick,
        failed: document.getElementById("loading")?.textContent,
        loadingVisible: !document.getElementById("loading")?.hidden,
    })), "state query").catch((e) => ({ dead: String(e) }));
    if (state.dead) failure = `page unreachable: ${state.dead}`;
    else if (state.sinceStep > stallMs && (await page.evaluate(() => /BASE DESTROYED|Open the menu to try a new world|VICTORY|YOU WIN/i.test(document.body.innerText)))) {
        // Random play lost (or won) the match, and the game stops stepping then: not a stall. Play another.
        matches++;
        console.log(`match over at tick ${state.tick}; starting match ${matches}`);
        await startMatch();
    } else if (state.sinceStep > stallMs) {
        const info = await page.evaluate(() => new Promise((done) => {
            let frames = 0;
            const count = () => frames++;
            const id = setInterval(() => {}, 1000);
            const tick = () => { count(); requestAnimationFrame(tick); };
            requestAnimationFrame(tick);
            setTimeout(() => { clearInterval(id); done({ rafFramesIn1s: frames, text: document.body.innerText.replace(/\s+/g, " ").slice(0, 600), paused: document.hidden, perf: window.emberwardPerf }); }, 1000);
        })).catch((e) => ({ evaluateFailed: String(e) }));
        console.log("at stall:", JSON.stringify(info));
        await page.screenshot({ path: `soak-stall-${Date.now()}.png` }).catch(() => {});
        // Does it come back? A pause that ends is a hitch; one that does not is a hang.
        const waited = await page
            .waitForFunction((from) => window.testLast > from, await page.evaluate(() => window.testLast), { timeout: 60000 })
            .then(() => "and then recovered", () => "and did not recover in 60 s");
        failure = `STALL: no step for ${Math.round(state.sinceStep)} ms at tick ${state.tick}, ${waited}`;
    }
    else if (/exited|Assert|Could not|failed/i.test(state.failed || "") && state.loadingVisible) failure = `error shown: ${state.failed}`;
    if (Date.now() - lastReport > 15000) {
        lastReport = Date.now();
        console.log(`${Math.round((Date.now() - began) / 1000)} s: tick ${state.tick}, ${state.steps} steps`);
    }
}
console.log(`matches played: ${matches}`);
console.log(`longest gap between steps: ${Math.round(await within(page.evaluate(() => window.testMaxGap), "gap", 5000).catch(() => -1))} ms`);
console.log(failure ? `FAILED after ${Math.round((Date.now() - began) / 1000)} s: ${failure}` : `ok: played ${seconds} s`);
for (const line of log.slice(-(Number(process.env.SOAK_LOG_TAIL) || 30))) console.log(line);
if (failure && process.env.SOAK_HOLD) await new Promise((r) => setTimeout(r, Number(process.env.SOAK_HOLD) * 1000));
await within(context.close(), "close", 15000).catch(() => {});
process.exit(failure ? 1 : 0);
