// Run with: node --test tools/browser
import assert from "node:assert/strict";
import { test } from "node:test";
import {
    RESOLUTION_STEPS,
    ResolutionGovernor,
} from "../../src/Tunnerer.Browser/wwwroot/pace.js";

// Feeds `seconds` of frames whose length `frameMs` works out from the governor's current scale.
function play(governor, seconds, frameMs, playing = true) {
    for (let t = 0; t < seconds * 1000; ) {
        const elapsed = frameMs(governor.scale);
        governor.pace(elapsed, playing);
        t += elapsed;
    }
}

// A display that shows a frame every `refresh` ms, and a GPU that needs `fullMs` for a full-resolution one.
const gpuBound = (refresh, fullMs) => (scale) =>
    Math.max(refresh, Math.ceil((fullMs * scale * scale) / refresh) * refresh);

test("a fast GPU keeps full resolution", () => {
    const governor = new ResolutionGovernor();
    play(governor, 5, () => 16.7, false);
    play(governor, 120, gpuBound(16.7, 6));
    assert.equal(governor.scale, 1);
});

test("a slow GPU steps down until frames are fast enough, then holds", () => {
    const governor = new ResolutionGovernor();
    play(governor, 5, () => 16.7, false);
    // 36 ms at full resolution: 0.65 of the pixels each way is the first step that fits one refresh.
    play(governor, 120, gpuBound(16.7, 36));
    assert.equal(governor.scale, 0.65);
    play(governor, 120, gpuBound(16.7, 36));
    assert.equal(governor.scale, 0.65);
});

test("a step that changes nothing is followed by one more before giving up", () => {
    const governor = new ResolutionGovernor();
    play(governor, 5, () => 16.7, false);
    // 40 ms at full resolution: two refreshes at 0.8 and still two at 0.65, one at 0.5.
    play(governor, 120, gpuBound(16.7, 40));
    assert.equal(governor.scale, 0.5);
    assert.equal(governor.settled, false);
});

test("a very slow GPU ends at the lowest step", () => {
    const governor = new ResolutionGovernor();
    play(governor, 5, () => 16.7, false);
    play(governor, 240, gpuBound(16.7, 400));
    assert.equal(governor.scale, RESOLUTION_STEPS.at(-1));
});

test("slowness that fewer pixels do not cure costs no resolution", () => {
    const governor = new ResolutionGovernor();
    play(governor, 5, () => 16.7, false);
    play(governor, 120, () => 50);
    assert.equal(governor.scale, 1);
    assert.equal(governor.settled, true);
});

test("a display capped at 30 frames a second is not taken for a slow GPU", () => {
    const governor = new ResolutionGovernor();
    play(governor, 5, () => 33.3, false);
    play(governor, 120, gpuBound(33.3, 10));
    assert.equal(governor.scale, 1);
    assert.equal(governor.settled, false);
});

test("hitches and pauses do not count", () => {
    const governor = new ResolutionGovernor();
    play(governor, 5, () => 16.7, false);
    let frame = 0;
    for (let round = 0; round < 20; round++) {
        // One long frame in ten, and a pause every few seconds.
        play(governor, 5, () => (frame++ % 10 === 0 ? 120 : 16.7));
        play(governor, 1, () => 16.7, false);
    }
    assert.equal(governor.scale, 1);
});
