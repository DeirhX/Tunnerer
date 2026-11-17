// The world is drawn by procedural shaders whose cost is the number of pixels. Where the graphics hardware cannot
// keep up, the world canvas steps down through these shares of the display's resolution (the page stretches it
// back; the HUD stays sharp).
export const RESOLUTION_STEPS = [1, 0.8, 0.65, 0.5];

const WINDOW_FRAMES = 90;
// Slower than this is worth giving up pixels for.
const SLOW_FRAME_MS = 1000 / 45;
// A step down must shorten frames at least this much to be kept.
const HELPED_RATIO = 0.87;

// Watches how fast frames are displayed and picks the resolution step. It judges by the median of a window of
// frames, so single hitches do not count. Frames come in whole display refreshes, so one step down may change
// nothing where the next one would: only when two steps in a row do not make frames come faster are they undone
// and the search ended, because then something other than pixels is slow. It never steps back up: a picture that
// changes sharpness while playing is worse than one that stays a little soft.
export class ResolutionGovernor {
    constructor() {
        this.step = 0;
        // The last step that was worth taking.
        this.heldStep = 0;
        this.settled = false;
        this.intervals = [];
        // The fastest pace seen: the display's own, or the browser's cap (a low-power mode shows 30 a second).
        this.refreshMs = Infinity;
        this.skipWindows = 2;
        this.beforeStepMs = null;
        this.playing = false;
    }

    get scale() {
        return RESOLUTION_STEPS[this.step];
    }

    // Call once per displayed frame with the milliseconds since the one before; `playing` while the world is drawn.
    pace(elapsed, playing) {
        if (playing !== this.playing) {
            // Frames from before a pause or a start say nothing about the ones after it.
            this.playing = playing;
            this.intervals = [];
            this.skipWindows = Math.max(this.skipWindows, 1);
        }
        this.intervals.push(elapsed);
        if (this.intervals.length < WINDOW_FRAMES) return;
        const sorted = this.intervals.sort((a, b) => a - b);
        const median = sorted[sorted.length >> 1];
        this.intervals = [];
        this.refreshMs = Math.min(this.refreshMs, median);
        if (!playing || this.settled) return;
        if (this.skipWindows > 0) {
            this.skipWindows--;
            return;
        }
        const last = RESOLUTION_STEPS.length - 1;
        if (this.beforeStepMs !== null) {
            if (median < this.beforeStepMs * HELPED_RATIO) {
                this.heldStep = this.step;
                this.beforeStepMs = null;
            } else if (this.step - this.heldStep >= 2 || this.step === last) {
                this.step = this.heldStep;
                this.settled = true;
                return;
            } else {
                this.stepDown();
                return;
            }
        }
        const slow = median > Math.max(SLOW_FRAME_MS, this.refreshMs * 1.3);
        if (slow && this.step < last) {
            this.beforeStepMs = median;
            this.stepDown();
        }
    }

    stepDown() {
        this.step++;
        // The first frames at a new size include the resize.
        this.skipWindows = 1;
    }
}
