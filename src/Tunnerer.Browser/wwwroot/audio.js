// Small procedural effects. Which events make a sound, for whom and how loud is decided in C# by the desktop's
// StepEventSounds table; each frame lists the cues to play by SoundEffect name. This file only gives each a tone:
// [start Hz, end Hz, seconds, oscillator].
const TONES = {
    Fire: [160, 65, 0.07, "triangle"],
    Impact: [70, 24, 0.18, "sawtooth"],
    TankHit: [120, 45, 0.1, "triangle"],
    TankDeath: [90, 20, 0.45, "sawtooth"],
    Respawn: [330, 660, 0.25, "triangle"],
    Dock: [280, 560, 0.2, "triangle"],
    BuildRelay: [260, 520, 0.12, "triangle"],
    BuildHarvester: [220, 440, 0.14, "square"],
    BuildPylon: [300, 600, 0.12, "sine"],
    Plant: [520, 780, 0.13, "triangle"],
    PlantRefused: [200, 140, 0.12, "square"],
    Pickup: [660, 900, 0.05, "triangle"],
};

// Loudness of a cue at gain 1; the C# gains are relative to the desktop's master volume.
const BASE_GAIN = 0.04;

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
            const tone = TONES[e.sound];
            if (!tone || count++ >= 5) continue;
            const [from, to, duration, type] = tone,
                ctx = this.context,
                t = ctx.currentTime;
            const oscillator = ctx.createOscillator(),
                gain = ctx.createGain();
            oscillator.type = type;
            oscillator.frequency.setValueAtTime(from, t);
            oscillator.frequency.exponentialRampToValueAtTime(to, t + duration);
            gain.gain.setValueAtTime(Math.max(0.001, BASE_GAIN * e.gain), t);
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
