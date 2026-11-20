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
const context = await playwright.chromium.launchPersistentContext(await mkdtemp(join(tmpdir(), "soak-")), {
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
    if (["error", "warning"].includes(m.type()) || /\[load\]|runtime|Assert/i.test(m.text()))
        log.push(`[${m.type()}] ${m.text().slice(0, 3000)}`);
});
page.on("pageerror", (e) => log.push(`[pageerror] ${String(e).slice(0, 400)}`));
await page.addInitScript(() => {
    window.testSteps = 0;
    window.testLast = performance.now();
    window.addEventListener("emberward-step", ({ detail }) => {
        window.testSteps++;
        window.testLast = performance.now();
        window.testTick = detail.tick;
    });
});
await page.goto(origin);
await page.locator("#seed").fill(process.env.SEED || "1234");
await page.locator("#start").click();
await page.waitForFunction(() => !document.getElementById("hud").hidden, null, { timeout: 180000 });
console.log("match started; threads:", await page.evaluate(() => document.body.innerText.match(/\d+ heat threads|one thread/)?.[0]));

const keys = ["KeyW", "KeyA", "KeyS", "KeyD"];
const builds = ["KeyE", "KeyR", "KeyT", "KeyY"];
const began = Date.now();
let failure = null;
let lastReport = 0;
while (Date.now() - began < seconds * 1000 && !failure) {
    const k = keys[Math.floor(Math.random() * 4)];
    await page.keyboard.down(k);
    await page.mouse.move(200 + Math.random() * 1000, 100 + Math.random() * 700);
    if (Math.random() < 0.6) await page.keyboard.down("Space");
    await page.waitForTimeout(300 + Math.random() * 900);
    await page.keyboard.up("Space");
    await page.keyboard.up(k);
    if (Math.random() < 0.25) await page.keyboard.press(builds[Math.floor(Math.random() * 4)]);
    if (Math.random() < 0.1) await page.keyboard.press(`Digit${1 + Math.floor(Math.random() * 7)}`);
    const state = await page.evaluate(() => ({
        sinceStep: performance.now() - window.testLast,
        steps: window.testSteps,
        tick: window.testTick,
        failed: document.getElementById("loading")?.textContent,
        loadingVisible: !document.getElementById("loading")?.hidden,
    })).catch((e) => ({ dead: String(e) }));
    if (state.dead) failure = `page unreachable: ${state.dead}`;
    else if (state.sinceStep > stallMs) {
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
console.log(failure ? `FAILED after ${Math.round((Date.now() - began) / 1000)} s: ${failure}` : `ok: played ${seconds} s`);
for (const line of log.slice(-30)) console.log(line);
await context.close();
process.exit(failure ? 1 : 0);
