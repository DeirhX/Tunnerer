// Small procedural effects, driven only by Core's step events. Audio is opt-in.
export class GameAudio {
    enabled = false;
    context = null;

    async toggle() {
        if (!this.context) {
            const Audio =
                globalThis.AudioContext || globalThis.webkitAudioContext;
            if (!Audio) return false;
            this.context = new Audio();
        }
        await this.context.resume();
        this.enabled = !this.enabled;
        return this.enabled;
    }

    play(events) {
        if (!this.enabled || this.context?.state !== "running") return;
        let count = 0;
        for (const e of events) {
            const sound = {
                ShotFired: [160, 65, 0.07],
                Explosion: [70, 24, 0.18],
                MachineBuilt: [260, 520, 0.12],
                MachinePlanted: [520, 780, 0.13],
                OreCollected: [660, 900, 0.05],
                TankHit: [120, 45, 0.1],
                Docked: [280, 560, 0.2],
            }[e.kind];
            if (!sound || count++ >= 5) continue;
            const [from, to, duration] = sound,
                ctx = this.context,
                t = ctx.currentTime;
            const oscillator = ctx.createOscillator(),
                gain = ctx.createGain();
            oscillator.type = e.kind === "Explosion" ? "sawtooth" : "triangle";
            oscillator.frequency.setValueAtTime(from, t);
            oscillator.frequency.exponentialRampToValueAtTime(to, t + duration);
            gain.gain.setValueAtTime(0.025, t);
            gain.gain.exponentialRampToValueAtTime(0.001, t + duration);
            oscillator.connect(gain);
            gain.connect(ctx.destination);
            oscillator.start(t);
            oscillator.stop(t + duration);
            oscillator.onended = () => {
                oscillator.disconnect();
                gain.disconnect();
            };
        }
    }
}
