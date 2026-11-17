// The game's sound, as on the desktop. What plays after each step, how loud, where and at what pitch is decided
// in C# by the desktop's SoundDirector, and the samples are the desktop's SoundBank, synthesised in .NET when sound
// is first turned on (BrowserSoundBank). This file plays them: each frame after a step lists the one-shots to
// start and where every loop should be (`sounds`: { play, loops }).

// The desktop's default master volume (AudioOptions.DefaultVolume). Its mixer gives a centred sound gain 1 in both
// speakers; a Web Audio stereo panner gives it 1/√2, which √2 makes up.
const MASTER_GAIN = 0.7 * Math.SQRT2;
// As the desktop's mixer (AudioTweaks.MaxVoices, MaxVoicesPerEffect): a further copy of an effect replaces the
// oldest, and beyond the total a new one-shot is dropped.
const MAX_VOICES = 32,
    MAX_VOICES_PER_EFFECT = 4;
// Loops glide to new values over about 25 ms, as the desktop's mixer does, so nothing clicks.
const GLIDE_SECONDS = 0.008;

export class GameAudio {
    enabled = false;
    context = null;
    // AudioBuffers by effect name, once the bank has arrived; `requested` while it is on its way.
    buffers = null;
    requested = false;
    voices = [];
    loops = [];
    paused = false;

    // True when the next request to .NET should ask for the sound bank.
    get wantsBank() {
        return this.enabled && !this.buffers && !this.requested;
    }

    async toggle() {
        if (!this.context) {
            const Audio =
                globalThis.AudioContext || globalThis.webkitAudioContext;
            if (!Audio) return false;
            this.context = new Audio();
            this.master = this.context.createGain();
            this.master.gain.value = MASTER_GAIN;
            // Bends loud peaks rather than clipping them, like the desktop's limiter.
            const limiter = this.context.createDynamicsCompressor();
            limiter.threshold.value = -3;
            limiter.knee.value = 6;
            limiter.ratio.value = 12;
            limiter.attack.value = 0.002;
            limiter.release.value = 0.1;
            this.master.connect(limiter);
            limiter.connect(this.context.destination);
        }
        this.enabled = !this.enabled;
        await this.applyState();
        return this.enabled;
    }

    // Sound runs while it is on and the game is not paused; otherwise the context stops and everything holds still.
    async applyState() {
        if (!this.context) return;
        if (this.enabled && !this.paused) await this.context.resume();
        else await this.context.suspend();
    }

    setPaused(paused) {
        this.paused = paused;
        this.applyState().catch(() => {});
    }

    // The bank from .NET: { samples: Float32Array, layout: { sampleRate, effects: [{ effect, offset, length }] } }.
    load(bank) {
        const ctx = this.context,
            buffers = new Map();
        for (const e of bank.layout.effects) {
            const buffer = ctx.createBuffer(
                1,
                Math.max(1, e.length),
                bank.layout.sampleRate,
            );
            buffer.copyToChannel(
                bank.samples.subarray(e.offset, e.offset + e.length),
                0,
            );
            buffers.set(e.effect, buffer);
        }
        this.buffers = buffers;
        this.requested = false;
    }

    play(sounds) {
        if (!this.enabled || !this.buffers || !sounds) return;
        const now = this.context.currentTime;
        for (const s of sounds.play) this.start(s, now);
        sounds.loops.forEach((l, i) => this.steer(i, l, now));
    }

    start(s, now) {
        const buffer = this.buffers.get(s.effect);
        if (!buffer) return;
        this.voices = this.voices.filter((v) => !v.ended);
        const same = this.voices.filter((v) => v.effect === s.effect);
        if (same.length >= MAX_VOICES_PER_EFFECT) this.release(same[0], now);
        if (this.voices.length >= MAX_VOICES) return;
        const voice = this.chain(buffer, s.gain, s.pan, s.rate, now);
        voice.effect = s.effect;
        voice.source.onended = () => {
            voice.ended = true;
            voice.source.disconnect();
            voice.gain.disconnect();
            voice.panner.disconnect();
        };
        voice.source.start(now);
        this.voices.push(voice);
    }

    // A one-shot replaced by a newer copy fades out quickly instead of stopping with a click.
    release(voice, now) {
        voice.gain.gain.setTargetAtTime(0, now, 0.002);
        voice.source.stop(now + 0.02);
        voice.ended = true;
    }

    steer(index, l, now) {
        let loop = this.loops[index];
        if (loop?.effect !== l.effect) {
            if (loop) this.release(loop, now);
            const buffer = this.buffers.get(l.effect);
            if (!buffer) return;
            loop = this.chain(buffer, 0, l.pan, l.rate, now);
            loop.effect = l.effect;
            loop.source.loop = true;
            loop.source.start(now);
            this.loops[index] = loop;
        }
        loop.gain.gain.setTargetAtTime(l.gain, now, GLIDE_SECONDS);
        loop.panner.pan.setTargetAtTime(l.pan, now, GLIDE_SECONDS);
        loop.source.playbackRate.setTargetAtTime(l.rate, now, GLIDE_SECONDS);
    }

    // Silences every loop: the match is over and no more steps will steer them.
    quiet() {
        if (!this.context) return;
        const now = this.context.currentTime;
        for (const loop of this.loops)
            loop?.gain.gain.setTargetAtTime(0, now, GLIDE_SECONDS);
    }

    chain(buffer, gain, pan, rate, now) {
        const ctx = this.context;
        const source = ctx.createBufferSource(),
            gainNode = ctx.createGain(),
            panner = ctx.createStereoPanner();
        source.buffer = buffer;
        source.playbackRate.setValueAtTime(rate, now);
        gainNode.gain.setValueAtTime(gain, now);
        panner.pan.setValueAtTime(pan, now);
        source.connect(gainNode);
        gainNode.connect(panner);
        panner.connect(this.master);
        return { source, gain: gainNode, panner, ended: false };
    }
}
