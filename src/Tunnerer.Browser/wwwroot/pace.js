// The world is drawn by procedural shaders whose cost is the number of pixels. Where the graphics hardware cannot
// keep up, the world canvas steps down through these shares of the display's resolution (the page stretches it
// back; the HUD stays sharp).
export const RESOLUTION_STEPS = [1, 0.8, 0.65, 0.5];

const WINDOW_FRAMES = 90;
// The pace aimed for: frames slower than 60 a second (with a tenth to spare for jitter) are worth giving up pixels.
const TARGET_FPS = 60;
const SLOW_FRAME_MS = (1000 / TARGET_FPS) * 1.1;
// A display's own pace counts as fast enough, with the same margin: a browser that shows 30 a second (a low-power
// mode) is not a slow GPU.
const REFRESH_MARGIN = 1.1;
// The slowest share of a window's frames is left out of its pace, so hitches (a collection, a tab switch) do not count.
const HITCH_SHARE = 0.15;
// A step down must shorten frames at least this much to be kept.
const HELPED_RATIO = 0.87;

// The pace of a window: its frames' average length, leaving out the slowest. An average, not the median, because
// frames come in whole display refreshes: a GPU that keeps 45 a second on a 60 Hz display shows two frames in three
// at one refresh and the third at two, and the median of that is one refresh.
function windowPace(intervals) {
    const sorted = intervals.sort((a, b) => a - b);
    const kept = sorted.slice(0, Math.ceil(sorted.length * (1 - HITCH_SHARE)));
    return kept.reduce((sum, ms) => sum + ms, 0) / kept.length;
}

// Watches how fast frames are displayed and picks the resolution step, aiming for 60 a second (or the display's own
// pace where that is slower). It judges a window of frames at a time, so single hitches do not count. One step down
// may change nothing where the next one would: only when two steps in a row do not make frames come faster are they
// undone and the search ended, because then something other than pixels is slow. It never steps back up: a picture
// that changes sharpness while playing is worse than one that stays a little soft.
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
        const pace = windowPace(this.intervals);
        this.intervals = [];
        this.refreshMs = Math.min(this.refreshMs, pace);
        if (!playing || this.settled) return;
        if (this.skipWindows > 0) {
            this.skipWindows--;
            return;
        }
        const last = RESOLUTION_STEPS.length - 1;
        if (this.beforeStepMs !== null) {
            if (pace < this.beforeStepMs * HELPED_RATIO) {
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
        const slow = pace > Math.max(SLOW_FRAME_MS, this.refreshMs * REFRESH_MARGIN);
        if (slow && this.step < last) {
            this.beforeStepMs = pace;
            this.stepDown();
        }
    }

    stepDown() {
        this.step++;
        // The first frames at a new size include the resize.
        this.skipWindows = 1;
    }
}
