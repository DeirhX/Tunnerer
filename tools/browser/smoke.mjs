// Run against the actual published WebAssembly game, not a JavaScript simulation or stub.
//
// BROWSER picks the engine: chromium (the default), webkit or firefox. SMOKE_THREADS=1 says the server sends the
// cross-origin isolation headers for a deployment published with --threads (serve.py --isolate): in Chromium the
// match must then run in the threaded runtime, and that runtime must compute the same world as the single-thread
// one. The other engines must still play it on one thread (wwwroot/threads.js says why).
import assert from "node:assert/strict";
import { mkdir, mkdtemp, rm } from "node:fs/promises";
import { resolve, join } from "node:path";
import { tmpdir } from "node:os";
import { pathToFileURL } from "node:url";

const modulePath =
    process.env.PLAYWRIGHT_MODULE ||
    resolve("artifacts/browser-tests/node_modules/playwright/index.mjs");
const playwright = await import(pathToFileURL(modulePath));
const engine = process.env.BROWSER || "chromium";
assert.ok(
    ["chromium", "webkit", "firefox"].includes(engine),
    `Unknown BROWSER ${engine}`,
);
const threads = process.env.SMOKE_THREADS === "1";
const origin = process.env.BROWSER_URL || "http://localhost:8703";
// A normal profile is needed to check installation; private contexts prohibit PWA installation.
const profile = await mkdtemp(join(tmpdir(), "tunnerer-browser-"));
const context = await playwright[engine].launchPersistentContext(profile, {
    headless: true,
    // Headless Chromium on macOS draws in software unless told to use Metal, and the game's shaders need a GPU.
    args:
        engine === "chromium" && process.platform === "darwin"
            ? ["--use-angle=metal"]
            : [],
    viewport: process.env.CI
        ? { width: 640, height: 480 }
        : { width: 1440, height: 960 },
    deviceScaleFactor: 1,
    ...(process.env.BROWSER_CHANNEL
        ? { channel: process.env.BROWSER_CHANNEL }
        : {}),
});
const browser = context.browser();
const out = resolve("artifacts/browser-tests");
await mkdir(out, { recursive: true });
const errors = [];
let activePage;

async function observe(context) {
    await context.addInitScript(() => {
        // The page announces the HUD's state after every simulation step, whichever runtime made it.
        window.addEventListener("tunnerer-step", ({ detail }) => {
            window.testFrame = detail;
            window.testEvents = [
                ...(window.testEvents || []),
                ...detail.events,
            ].slice(-1000);
        });
    });
    const page = await context.newPage();
    activePage = page;
    page.on("pageerror", (error) => errors.push(String(error)));
    page.on("console", (message) => {
        if (
            message.type() === "error" &&
            !message.location().url.includes("/offline-probe?")
        )
            errors.push(message.text());
    });
    return page;
}

async function ticks(page, count = 5) {
    const start = await page.evaluate(() => window.testFrame.tick);
    await page.waitForFunction(
        (target) => window.testFrame?.tick >= target,
        start + count,
        { timeout: process.env.CI ? 120000 : 30000 },
    );
}

async function start(page) {
    await page.locator("#seed").fill("1234");
    await page.locator("#start").click();
    await page.waitForFunction(
        () =>
            !document.getElementById("hud").hidden ||
            document
                .getElementById("loading")
                .textContent.startsWith("Could not start"),
        null,
        { timeout: 120000 },
    );
    assert.equal(
        await page.locator("#hud").isVisible(),
        true,
        await page.locator("#loading").textContent(),
    );
    await ticks(page);
    assert.equal(await page.locator("#welcome").isVisible(), false);
}

try {
    const page = await observe(context);
    await page.goto(origin);
    await page.screenshot({ path: resolve(out, "welcome.png") });
    await start(page);
    assert.equal(
        await page
            .locator("#world")
            .evaluate((canvas) => canvas.getContext("webgl2").getError()),
        0,
        "The shared shaders must render without WebGL errors",
    );
    const initial = await page.evaluate(() => window.testFrame.player);
    assert.equal(initial.docked, true);
    assert.equal(
        await page.evaluate(() => crossOriginIsolated),
        threads,
        threads
            ? "The server must send the isolation headers (serve.py --isolate)"
            : "No isolation headers should be needed",
    );
    const heatWorkers = Number(
        await page.locator("#game").getAttribute("data-heat-workers"),
    );
    if (threads && engine === "chromium")
        assert.ok(heatWorkers > 1, "The threaded runtime must run the match");
    else assert.equal(heatWorkers, 1, "One thread here");
    await page.keyboard.press("Digit1");
    await page.waitForFunction(() => window.testFrame.upgrades[0].level === 1);
    await page.keyboard.press("KeyE");
    await page.waitForFunction(
        () => window.testFrame.player.carrying === "Relay",
    );
    await page.keyboard.down("KeyW");
    await ticks(page, 40);
    await page.keyboard.up("KeyW");
    const moved = await page.evaluate(() => window.testFrame.player);
    assert.ok(
        moved.position.y < initial.position.y,
        "Keyboard movement must advance the real tank",
    );
    assert.equal(moved.docked, false);
    await page.keyboard.press("KeyE");
    await page.waitForFunction(() => !window.testFrame.player.carrying);
    assert.ok(
        await page.evaluate(() =>
            window.testEvents.some((e) => e.sound === "Plant"),
        ),
        "A relay must plant",
    );
    await page.keyboard.down("Space");
    await ticks(page, 8);
    await page.keyboard.up("Space");
    assert.ok(
        await page.evaluate(() =>
            window.testEvents.some((e) => e.sound === "Fire"),
        ),
        "Firing must produce Core events",
    );
    await page.screenshot({ path: resolve(out, "game.png") });
    await page.keyboard.press("Escape");
    await page.locator("#menu").waitFor({ state: "visible" });
    // Let an already-in-flight worker tick finish before checking the pause.
    await page.waitForTimeout(150);
    const pausedTick = await page.evaluate(() => window.testFrame.tick);
    await page.waitForTimeout(250);
    assert.equal(
        await page.evaluate(() => window.testFrame.tick),
        pausedTick,
        "Pause must stop simulation ticks",
    );
    await page.locator("#resume").click();
    await ticks(page);
    await page.waitForFunction(
        () =>
            document.getElementById("offline").textContent === "OFFLINE READY",
        null,
        { timeout: 120000 },
    );
    if (engine === "chromium") {
        const cdp = await context.newCDPSession(page);
        const install = await cdp.send("Page.getInstallabilityErrors");
        assert.deepEqual(
            install.installabilityErrors,
            [],
            "The PWA must meet Chromium installation requirements",
        );
        await cdp.detach();
    }
    await context.setOffline(true);
    // A real network probe is stronger than navigator.onLine, which Chromium may keep true on localhost.
    assert.equal(
        await page.evaluate(async () => {
            try {
                await fetch(`./offline-probe?${Math.random()}`, {
                    cache: "no-store",
                });
                return false;
            } catch {
                return true;
            }
        }),
        true,
        "Uncached requests must fail while offline",
    );
    await page.reload();
    await start(page);
    await page.screenshot({ path: resolve(out, "offline.png") });
    await page.close();
    await context.setOffline(false);

    // The same world from every runtime, and from one heat worker as from several: each plays the same short
    // match that nobody watches and names what it ended with (BrowserApi.Fingerprint).
    const fingerprint = async (framework, heatWorkers) => {
        const blank = await observe(context);
        await blank.goto(origin);
        const value = await blank.evaluate(
            async ([framework, heatWorkers]) => {
                const { startRuntime } = await import("./frames.js");
                const { dotnet } = await import(`./${framework}/dotnet.js`);
                const runtime = await startRuntime(dotnet, {
                    pthreadPoolInitialSize: heatWorkers + 6,
                });
                return runtime.fingerprint(1234, heatWorkers, 240);
            },
            [framework, heatWorkers],
        );
        await blank.close();
        return value;
    };
    const world = await fingerprint("_framework", 1);
    if (process.env.SMOKE_FINGERPRINT)
        assert.equal(
            world,
            process.env.SMOKE_FINGERPRINT,
            "The world must be the one another machine computed",
        );
    if (heatWorkers > 1)
        assert.equal(
            await fingerprint("_framework-threads", heatWorkers),
            world,
            "The threaded runtime must compute the single-thread runtime's world",
        );

    const touch = await browser.newContext({
        viewport: { width: 390, height: 844 },
        // Firefox has no mobile emulation; a touch screen is what the layout asks about.
        ...(engine === "firefox" ? {} : { isMobile: true }),
        hasTouch: true,
        deviceScaleFactor: 1,
    });
    const mobile = await observe(touch);
    await mobile.goto(origin);
    await start(mobile);
    assert.equal(await mobile.locator("#touch").isVisible(), true);
    assert.equal(
        await mobile.locator("#upgrades").evaluate((e) => e.open),
        false,
    );
    await mobile.locator("#upgrades summary").click();
    assert.equal(
        await mobile.locator("#upgrade-list button").first().isVisible(),
        true,
    );
    await mobile.locator("#upgrades summary").click();
    const before = await mobile.evaluate(
        () => window.testFrame.player.position.y,
    );
    const up = await mobile.locator('[data-move="0,-1"]').boundingBox();
    await mobile.mouse.move(up.x + up.width / 2, up.y + up.height / 2);
    await mobile.mouse.down();
    await ticks(mobile, 8);
    await mobile.mouse.up();
    assert.ok(
        await mobile.evaluate(
            (y) => window.testFrame.player.position.y < y,
            before,
        ),
        "On-screen controls must drive the tank",
    );
    await mobile.screenshot({ path: resolve(out, "touch.png") });
    assert.equal(
        await mobile.evaluate(
            () => document.documentElement.scrollWidth > innerWidth,
        ),
        false,
        "Mobile layout must fit the viewport",
    );
    await touch.close();
    assert.deepEqual(errors, [], "No browser runtime errors");
    console.log(
        `PASS (${engine}, heat on ${heatWorkers} thread${heatWorkers === 1 ? "" : "s"}, world ${world}): ` +
            "published WASM, movement, building, planting, upgrades, firing, pause, " +
            `${engine === "chromium" ? "PWA installability, " : ""}offline reload, touch controls.`,
    );
} catch (error) {
    console.error("Browser errors:", errors);
    if (activePage && !activePage.isClosed()) {
        console.error(
            "Last frame:",
            await activePage.evaluate(() => window.testFrame),
        );
        console.error(
            "Loading:",
            await activePage.locator("#loading").textContent(),
        );
        await activePage.screenshot({ path: resolve(out, "failure.png") });
    }
    throw error;
} finally {
    await browser.close();
    await rm(profile, { recursive: true, force: true });
}
