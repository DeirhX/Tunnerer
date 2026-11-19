// Run with: node --test tools/browser
import assert from "node:assert/strict";
import { test } from "node:test";
import { detectPlatform } from "../../src/Emberward.Browser/wwwroot/downloads.js";

const CHROME_WIN = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
const FIREFOX_WIN = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0";
const SAFARI_MAC = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15";
const CHROME_MAC = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
const FIREFOX_LINUX = "Mozilla/5.0 (X11; Linux x86_64; rv:128.0) Gecko/20100101 Firefox/128.0";
const CHROME_CROS = "Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
const CHROME_ANDROID = "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36";
const SAFARI_IPHONE = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";
const SAFARI_IPAD_OLD = "Mozilla/5.0 (iPad; CPU OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 Mobile/15E148 Safari/604.1";
const SAFARI_IPAD_DESKTOP = SAFARI_MAC; // iPadOS 13+ asks for the desktop site and reports a Mac.

test("client hints name the platform", () => {
    assert.equal(detectPlatform({ userAgent: CHROME_WIN, userAgentData: { platform: "Windows", mobile: false } }), "windows");
    assert.equal(detectPlatform({ userAgent: CHROME_MAC, userAgentData: { platform: "macOS", mobile: false } }), "macos");
    assert.equal(detectPlatform({ userAgent: FIREFOX_LINUX, userAgentData: { platform: "Linux", mobile: false } }), "linux");
});

test("client hints mark phones and tablets as mobile, even on Linux-based Android", () => {
    assert.equal(detectPlatform({ userAgent: CHROME_ANDROID, userAgentData: { platform: "Android", mobile: true } }), "mobile");
    assert.equal(detectPlatform({ userAgent: CHROME_ANDROID, userAgentData: { platform: "Linux", mobile: true } }), "mobile");
});

test("without client hints the user agent and platform decide", () => {
    assert.equal(detectPlatform({ userAgent: CHROME_WIN, platform: "Win32" }), "windows");
    assert.equal(detectPlatform({ userAgent: FIREFOX_WIN, platform: "Win32" }), "windows");
    assert.equal(detectPlatform({ userAgent: SAFARI_MAC, platform: "MacIntel", maxTouchPoints: 0 }), "macos");
    assert.equal(detectPlatform({ userAgent: FIREFOX_LINUX, platform: "Linux x86_64" }), "linux");
});

test("Apple silicon reports the same macOS string as Intel, so it is macOS too", () => {
    assert.equal(detectPlatform({ userAgent: CHROME_MAC, platform: "MacIntel", maxTouchPoints: 0 }), "macos");
});

test("iPhones and iPads are mobile, including iPadOS asking for the desktop site", () => {
    assert.equal(detectPlatform({ userAgent: SAFARI_IPHONE, platform: "iPhone", maxTouchPoints: 5 }), "mobile");
    assert.equal(detectPlatform({ userAgent: SAFARI_IPAD_OLD, platform: "iPad", maxTouchPoints: 5 }), "mobile");
    assert.equal(detectPlatform({ userAgent: SAFARI_IPAD_DESKTOP, platform: "MacIntel", maxTouchPoints: 5 }), "mobile");
});

test("Android without client hints is mobile, not Linux", () => {
    assert.equal(detectPlatform({ userAgent: CHROME_ANDROID, platform: "Linux armv8l" }), "mobile");
});

test("Chrome OS has no build", () => {
    assert.equal(detectPlatform({ userAgent: CHROME_CROS, platform: "Linux x86_64" }), "unknown");
    assert.equal(detectPlatform({ userAgent: CHROME_CROS, userAgentData: { platform: "Chrome OS", mobile: false } }), "unknown");
});

test("an empty or unknown navigator is unknown", () => {
    assert.equal(detectPlatform({}), "unknown");
    assert.equal(detectPlatform({ userAgent: "curl/8.0", platform: "" }), "unknown");
});
