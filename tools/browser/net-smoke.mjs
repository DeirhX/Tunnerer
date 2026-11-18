// Two browser players in one room of a real relay, playing the published WebAssembly game (docs/browser.md).
//
// Start a relay first (dotnet run --project src/Tunnerer.Relay -- --urls http://127.0.0.1:8130) and serve the
// published game. RELAY names the relay (default ws://127.0.0.1:8130/play), BROWSER_URL the page, RELAY_SECRET the
// relay's secret if it has one. The relay compares the players' world hashes once a second and stops the match if
// they differ, so a match that plays on to the end of the test computed one world in both pages.
import assert from "node:assert/strict";
import { mkdir } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const modulePath =
    process.env.PLAYWRIGHT_MODULE ||
    resolve("artifacts/browser-tests/node_modules/playwright/index.mjs");
const { chromium } = await import(pathToFileURL(modulePath));
const origin = process.env.BROWSER_URL || "http://localhost:8703";
const relay = process.env.RELAY || "ws://127.0.0.1:8130/play";
const room = `smoke-${Date.now().toString(36)}`;
const out = resolve("artifacts/browser-tests");
await mkdir(out, { recursive: true });
const timeout = process.env.CI ? 120000 : 45000;

const browser = await chromium.launch({
    headless: true,
    args: process.platform === "darwin" ? ["--use-angle=metal"] : [],
});
const errors = [];

async function player(name) {
    const context = await browser.newContext({
        viewport: { width: 1280, height: 800 },
        deviceScaleFactor: 1,
    });
    await context.addInitScript(() => {
        window.addEventListener("tunnerer-step", ({ detail }) => {
            window.testFrame = detail;
        });
    });
    const page = await context.newPage();
    page.on("pageerror", (error) => errors.push(`${name}: ${error}`));
    page.on("console", (message) => {
        if (message.type() === "error") errors.push(`${name}: ${message.text()}`);
    });
    await page.goto(`${origin}/?relay=${encodeURIComponent(relay)}`);
    await page.locator("#online summary").click();
    await page.locator("#net-name").fill(name);
    await page.locator("#net-room").fill(room);
    await page.locator("#net-players").selectOption("3");
    await page.locator("#net-secret").fill(process.env.RELAY_SECRET || "");
    await page.locator("#join").click();
    await page.locator("#lobby").waitFor({ state: "visible", timeout });
    return page;
}

const net = (page) => page.evaluate(() => window.testFrame?.net);
const tick = (page) => page.evaluate(() => window.testFrame?.tick ?? 0);
const until = (page, fn, arg) =>
    page.waitForFunction(fn, arg, { timeout, polling: 50 });

let ann, bob;
try {
    ann = await player("Ann");
    bob = await player("Bob");
    await until(ann, () =>
        window.testFrame?.net?.roster.some((slot) => slot.name === "Bob"),
    );
    const lobby = await net(ann);
    assert.equal(lobby.isHost, true, "The first player in hosts the room");
    assert.equal(lobby.slots, 3);
    assert.equal((await net(bob)).isHost, false);
    assert.equal(await bob.locator("#lobby-start").isVisible(), false);
    await ann.screenshot({ path: resolve(out, "net-lobby.png") });

    // The host starts; the open slot gets a bot.
    await ann.locator("#lobby-start").click();
    for (const page of [ann, bob])
        await until(page, () => window.testFrame?.net?.started && window.testFrame.tick > 5);
    assert.deepEqual(
        (await net(bob)).roster.map((slot) => slot.name),
        ["Ann", "Bob", "Bot"],
    );
    assert.equal(await ann.locator("#lobby").isVisible(), false);

    // Both drive: the tanks are driven by the relay's bundles, so this goes round the relay and back.
    const start = await ann.evaluate(() => window.testFrame.player.position);
    await ann.locator("#game").focus();
    await ann.keyboard.down("KeyW");
    await bob.locator("#game").focus();
    await bob.keyboard.down("KeyD");
    await until(ann, (t) => window.testFrame.tick > t, (await tick(ann)) + 48);
    await ann.keyboard.up("KeyW");
    await bob.keyboard.up("KeyD");
    const moved = await ann.evaluate(() => window.testFrame.player.position);
    assert.ok(moved.y < start.y, "Ann's tank drove north");

    // A build press goes out with one tick.
    await ann.keyboard.press("KeyE");
    await until(ann, () => window.testFrame.player.carrying === "Relay");

    // P holds the match for everyone, and P again lets it run.
    await bob.keyboard.press("KeyP");
    for (const page of [ann, bob])
        await until(page, () => window.testFrame?.net?.paused);
    const held = await tick(ann);
    await ann.waitForTimeout(500);
    assert.ok((await tick(ann)) <= held + 1, "A paused match stands still");
    assert.match((await net(ann)).notice, /Paused by Bob/);
    await ann.keyboard.press("KeyP");
    await until(ann, (t) => !window.testFrame.net.paused && window.testFrame.tick > t, held + 24);

    // On past several hash checks: neither page is told the worlds went apart.
    await until(ann, (t) => window.testFrame.tick > t, (await tick(ann)) + 120);
    await until(bob, (t) => window.testFrame.tick > t, (await tick(ann)) - 24);
    for (const page of [ann, bob]) {
        const status = await net(page);
        assert.equal(status.bad, false, status.notice);
        assert.equal(status.notice, null);
        assert.ok(status.latencyMs !== null, "The round trip is measured");
    }
    assert.equal(await ann.locator("#net-latency").isVisible(), true);
    await ann.screenshot({ path: resolve(out, "net-match.png") });

    // Bob leaves and comes back under his name: his page replays the match to the present.
    await bob.keyboard.press("Escape");
    await bob.locator("#restart").click();
    await bob.locator("#welcome").waitFor({ state: "visible" });
    await bob.locator("#join").click();
    await until(bob, () => window.testFrame?.net?.started && window.testFrame.tick > 0);
    const target = await tick(ann);
    await until(bob, (t) => window.testFrame.tick >= t, target);
    await until(ann, (t) => window.testFrame.tick > t, target + 48);
    for (const page of [ann, bob]) {
        const status = await net(page);
        assert.equal(status.bad, false, status.notice);
    }
    assert.equal((await net(bob)).slot, 1, "Bob is back in his slot");

    assert.deepEqual(errors, [], "No browser runtime errors");
    console.log(
        `PASS (room ${room}, tick ${await tick(ann)}): lobby, host start, bot slot, movement and builds through ` +
            "the relay, pause for everyone, rejoin with catch-up, no desync.",
    );
} catch (error) {
    console.error("Browser errors:", errors);
    for (const [name, page] of [["ann", ann], ["bob", bob]])
        if (page && !page.isClosed()) {
            console.error(name, await page.evaluate(() => window.testFrame?.net));
            console.error(name, await page.locator("#loading").textContent());
            await page.screenshot({ path: resolve(out, `net-failure-${name}.png`) });
        }
    throw error;
} finally {
    await browser.close();
}
