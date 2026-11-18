// A small readout for checking performance on a device without developer tools: open the game with ?perf.
// It shows, averaged over the last second: the display's frames per second and how many of them drew a new
// picture, the time a simulation step and building a frame take inside .NET and how many threads step the heat,
// the texture bytes uploaded per second, and the size the world is drawn at.
export class PerfReadout {
    constructor(renderer) {
        this.renderer = renderer;
        this.element = document.createElement("pre");
        this.element.id = "perf";
        this.element.style.cssText =
            "position:fixed;left:8px;bottom:var(--perf-bottom,8px);margin:0;padding:6px 8px;z-index:100;pointer-events:none;" +
            "font:11px/1.4 monospace;color:#cfe;background:#000a;border-radius:4px";
        document.body.append(this.element);
        this.reset(performance.now());
    }

    reset(now) {
        this.since = now;
        this.frames = this.steps = this.renders = this.uploaded = 0;
        this.stepMs = this.renderMs = this.worstMs = 0;
    }

    // A frame arrived from the simulation.
    frame(timing, uploadedBytes, heatWorkers) {
        this.heatWorkers = heatWorkers;
        if (timing?.step != null) {
            this.steps++;
            this.stepMs += timing.step;
        }
        this.renders++;
        this.renderMs += timing?.render || 0;
        this.uploaded += uploadedBytes || 0;
    }

    // The page drew; `elapsed` is the time since it last did.
    displayed(elapsed) {
        const now = performance.now();
        this.frames++;
        this.worstMs = Math.max(this.worstMs, elapsed);
        const seconds = (now - this.since) / 1000;
        if (seconds < 1) return;
        const gpu = this.renderer.gpu;
        // Kept on the window too, so a test can read the same numbers.
        window.tunnererPerf = {
            fps: this.frames / seconds,
            drawnPerSecond: this.renders / seconds,
            worstFrameMs: this.worstMs,
            stepMs: this.steps ? this.stepMs / this.steps : 0,
            stepsPerSecond: this.steps / seconds,
            renderMs: this.renders ? this.renderMs / this.renders : 0,
            heatWorkers: this.heatWorkers || 0,
            uploadedBytesPerSecond: this.uploaded / seconds,
            width: gpu?.width || 0,
            height: gpu?.height || 0,
            resolution: this.renderer.governor.scale,
        };
        const p = window.tunnererPerf;
        this.element.textContent =
            `${p.fps.toFixed(0)} fps, ${p.drawnPerSecond.toFixed(0)} drawn (worst ${p.worstFrameMs.toFixed(0)} ms)\n` +
            `step ${p.stepMs.toFixed(1)} ms x ${p.stepsPerSecond.toFixed(0)}/s  frame ${p.renderMs.toFixed(2)} ms  ` +
            `heat on ${p.heatWorkers} thread${p.heatWorkers === 1 ? "" : "s"}\n` +
            `upload ${(p.uploadedBytesPerSecond / 1024).toFixed(0)} KB/s  ` +
            `${p.width}x${p.height} (${Math.round(p.resolution * 100)}%)`;
        this.reset(now);
    }
}
