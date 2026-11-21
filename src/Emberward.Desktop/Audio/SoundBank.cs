namespace Emberward.Desktop.Audio;

using Emberward.Desktop.Config;

/// <summary>
/// Every sound in the game, synthesised when the game starts: filtered noise, sine partials and envelopes, written
/// out as mono samples at <see cref="AudioTweaks.SampleRate"/>. There are no sound files. The noise generator is
/// seeded per effect, so the bank is the same on every run.
/// </summary>
public sealed class SoundBank
{
    private const int Rate = AudioTweaks.SampleRate;
    private const float Tau = MathF.Tau;
    private const float PeakLevel = 0.9f;
    private const float MaxOneShotRms = 0.25f;

    private readonly float[][] _samples;

    public SoundBank()
    {
        var effects = Enum.GetValues<SoundEffect>();
        _samples = new float[effects.Length][];
        foreach (SoundEffect effect in effects)
            _samples[(int)effect] = Normalize(Synthesize(effect), IsLoop(effect));
    }

    public float[] this[SoundEffect effect] => _samples[(int)effect];

    public static bool IsLoop(SoundEffect effect) => effect is SoundEffect.EngineLoop or SoundEffect.DigLoop or
        SoundEffect.TorchLoop or SoundEffect.RechargeLoop or SoundEffect.TreadLoop;

    private static float[] Synthesize(SoundEffect effect)
    {
        var noise = new Noise(0x9E3779B9u * ((uint)effect + 1));
        return effect switch
        {
            SoundEffect.Fire => Fire(ref noise),
            SoundEffect.Impact => Impact(ref noise),
            SoundEffect.Debris => Debris(ref noise),
            SoundEffect.TankHit => TankHit(ref noise),
            SoundEffect.TankDeath => TankDeath(ref noise),
            SoundEffect.Respawn => Respawn(ref noise),
            SoundEffect.Dock => Dock(ref noise),
            SoundEffect.BuildRelay => Build(ref noise, 0.45f, 170f, 430f, 28f, 900f),
            SoundEffect.BuildHarvester => Build(ref noise, 0.56f, 105f, 280f, 19f, 600f),
            SoundEffect.BuildGenerator => Build(ref noise, 0.50f, 140f, 360f, 22f, 1200f),
            SoundEffect.BuildTurret => Build(ref noise, 0.42f, 210f, 540f, 34f, 1600f),
            SoundEffect.TurretFire => TurretFire(ref noise),
            SoundEffect.BuildGatling => Build(ref noise, 0.40f, 250f, 620f, 40f, 1900f),
            SoundEffect.GatlingFire => GatlingFire(ref noise),
            SoundEffect.ShieldHit => ShieldHit(ref noise),
            SoundEffect.ShieldBreak => ShieldBreak(ref noise),
            SoundEffect.Plant => Plant(ref noise),
            SoundEffect.PlantRefused => PlantRefused(),
            SoundEffect.Pickup => Pickup(),
            SoundEffect.WarnOverheat => WarnOverheat(),
            SoundEffect.WarnShield => WarnShield(),
            SoundEffect.WarnEnergy => WarnEnergy(),
            SoundEffect.EngineLoop => EngineLoop(ref noise),
            SoundEffect.DigLoop => DigLoop(ref noise),
            SoundEffect.TorchLoop => TorchLoop(ref noise),
            SoundEffect.RechargeLoop => RechargeLoop(),
            SoundEffect.TreadLoop => TreadLoop(ref noise),
            _ => throw new ArgumentOutOfRangeException(nameof(effect)),
        };
    }

    // ---- One-shots ---------------------------------------------------------------------------------------------

    /// <summary>Cannon shot: a bright crack that closes down fast, over a short falling thump.</summary>
    private static float[] Fire(ref Noise noise)
    {
        var samples = Buffer(0.24f);
        var crack = new LowPass();
        var body = new LowPass();
        float phase = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            float white = noise.Next();
            float hiss = crack.Process(white, 900f + 6000f * Decay(t, 0.025f));
            float report = body.Process(white, 700f);
            phase += Tau * (90f + 160f * Decay(t, 0.03f)) / Rate;
            samples[i] = hiss * Decay(t, 0.05f) * 4f + report * Decay(t, 0.09f) * 3f
                + MathF.Sin(phase) * Decay(t, 0.06f) * 0.7f;
        }
        return samples;
    }

    /// <summary>A gatling's round: a short dry snap, made to be heard many times a second.</summary>
    private static float[] GatlingFire(ref Noise noise)
    {
        var samples = Buffer(0.07f);
        var snap = new LowPass();
        float phase = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            float hiss = snap.Process(noise.Next(), 2400f + 6000f * Decay(t, 0.008f));
            phase += Tau * (260f + 240f * Decay(t, 0.012f)) / Rate;
            samples[i] = hiss * Decay(t, 0.016f) * 3.0f + MathF.Sin(phase) * Decay(t, 0.02f) * 0.45f;
        }
        return samples;
    }

    /// <summary>A shield taking a hit: a bright zap, a falling sine sweep over a short burst of hiss.</summary>
    private static float[] ShieldHit(ref Noise noise)
    {
        var samples = Buffer(0.2f);
        var fizz = new LowPass();
        float phase = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            phase += Tau * (600f + 1200f * Decay(t, 0.03f)) / Rate;
            float hiss = fizz.Process(noise.Next(), 5000f);
            samples[i] = MathF.Sin(phase) * Decay(t, 0.06f) * 0.7f + hiss * Decay(t, 0.012f) * 1.6f;
        }
        return samples;
    }

    /// <summary>A shield giving way: the zap, then a glassy shatter of high inharmonic tones.</summary>
    private static float[] ShieldBreak(ref Noise noise)
    {
        ReadOnlySpan<float> frequencies = [1320f, 1970f, 2610f, 3480f, 4310f];
        var samples = Buffer(0.55f);
        var fizz = new LowPass();
        float phase = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            phase += Tau * (500f + 1500f * Decay(t, 0.05f)) / Rate;
            float glass = 0f;
            for (int k = 0; k < frequencies.Length; k++)
                glass += MathF.Sin(Tau * frequencies[k] * t) * Decay(t, 0.05f + 0.03f * k);
            samples[i] = MathF.Sin(phase) * Decay(t, 0.09f) * 0.6f + glass * 0.14f + fizz.Process(noise.Next(), 6000f) * Decay(t, 0.02f) * 1.8f;
        }
        return samples;
    }

    /// <summary>A turret's shot: a lighter, higher crack than the tank's cannon, over a short metallic tick.</summary>
    private static float[] TurretFire(ref Noise noise)
    {
        var samples = Buffer(0.16f);
        var crack = new LowPass();
        float phase = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            float hiss = crack.Process(noise.Next(), 1600f + 7000f * Decay(t, 0.015f));
            phase += Tau * (180f + 320f * Decay(t, 0.02f)) / Rate;
            samples[i] = hiss * Decay(t, 0.035f) * 3.5f + MathF.Sin(phase) * Decay(t, 0.04f) * 0.6f
                + MathF.Sin(Tau * 1870f * t) * Decay(t, 0.02f) * 0.25f;
        }
        return samples;
    }

    /// <summary>A shell bursting: duller and longer than the shot, with a low boom.</summary>
    private static float[] Impact(ref Noise noise)
    {
        var samples = Buffer(0.5f);
        var blast = new LowPass();
        float phase = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            float rumble = blast.Process(noise.Next(), 300f + 3000f * Decay(t, 0.05f));
            phase += Tau * (70f + 110f * Decay(t, 0.07f)) / Rate;
            samples[i] = rumble * Decay(t, 0.13f) * 4f + MathF.Sin(phase) * Decay(t, 0.18f) * 0.3f;
        }
        return samples;
    }

    /// <summary>Fragments rattling into rock: a scatter of short high ticks.</summary>
    private static float[] Debris(ref Noise noise)
    {
        var samples = Buffer(0.3f);
        var top = new LowPass();
        var top2 = new LowPass();
        var bottom = new LowPass();
        float tick = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            if (noise.Next01() < 70f / Rate * Decay(t, 0.1f) + (i == 0 ? 1f : 0f))
                tick = 0.4f + 0.6f * noise.Next01();
            tick *= MathF.Exp(-1f / (0.004f * Rate));
            float band = top2.Process(top.Process(noise.Next(), 4000f), 4000f);
            samples[i] = (band - bottom.Process(band, 1200f)) * tick;
        }
        return samples;
    }

    /// <summary>A shell striking armour: an inharmonic clang with a hard front.</summary>
    private static float[] TankHit(ref Noise noise)
    {
        ReadOnlySpan<float> frequencies = [410f, 967f, 1543f, 2290f, 3170f];
        ReadOnlySpan<float> decays = [0.12f, 0.09f, 0.06f, 0.04f, 0.03f];
        ReadOnlySpan<float> levels = [1f, 0.9f, 0.8f, 0.6f, 0.5f];

        var samples = Buffer(0.38f);
        var strike = new LowPass();
        float thudPhase = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            float ring = 0f;
            for (int k = 0; k < frequencies.Length; k++)
                ring += MathF.Sin(Tau * frequencies[k] * t) * Decay(t, decays[k]) * levels[k];
            thudPhase += Tau * (60f + 50f * Decay(t, 0.03f)) / Rate;
            samples[i] = ring * 0.45f
                + strike.Process(noise.Next(), 4000f) * Decay(t, 0.012f) * 2.5f
                + MathF.Sin(thudPhase) * Decay(t, 0.05f) * 0.6f;
        }
        return samples;
    }

    /// <summary>A tank blowing up: a long low blast with secondary crackles in its tail.</summary>
    private static float[] TankDeath(ref Noise noise)
    {
        var samples = Buffer(1.8f);
        var blast = new LowPass();
        var crackleBody = new LowPass();
        float phase = 0f, crackle = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            float rumble = blast.Process(noise.Next(), 200f + 2500f * Decay(t, 0.2f));
            phase += Tau * (50f + 80f * Decay(t, 0.2f)) / Rate;
            if (t > 0.08f && noise.Next01() < 14f / Rate * Decay(t, 0.6f))
                crackle = (0.3f + 0.7f * noise.Next01()) * Decay(t, 0.7f);
            crackle *= MathF.Exp(-1f / (0.012f * Rate));
            samples[i] = rumble * Decay(t, 0.45f) * 5f
                + MathF.Sin(phase) * Decay(t, 0.6f) * 0.25f
                + crackleBody.Process(noise.Next(), 3000f) * crackle * 2f;
        }
        return samples;
    }

    /// <summary>A new tank arriving: four rising notes over a soft whoosh.</summary>
    private static float[] Respawn(ref Noise noise)
    {
        ReadOnlySpan<float> notes = [220f, 329.63f, 440f, 554.37f];
        var samples = Buffer(0.85f);
        var whoosh = new LowPass();
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            float chord = 0f;
            for (int k = 0; k < notes.Length; k++)
            {
                float local = t - k * 0.11f;
                if (local < 0f) continue;
                float tone = MathF.Sin(Tau * notes[k] * local) + 0.3f * MathF.Sin(Tau * 2f * notes[k] * local);
                chord += tone * Attack(local, 0.005f) * Decay(local, 0.22f);
            }
            float air = t < 0.5f
                ? whoosh.Process(noise.Next(), 300f + 6000f * t) * MathF.Sin(MathF.PI * t / 0.5f) * 0.25f
                : 0f;
            samples[i] = chord * 0.4f + air;
        }
        return samples;
    }

    /// <summary>Driving into the base: a clunk, then a short two-note chime.</summary>
    private static float[] Dock(ref Noise noise)
    {
        var samples = Buffer(0.42f);
        float phase = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            phase += Tau * (55f + 40f * Decay(t, 0.04f)) / Rate;
            float clunk = MathF.Sin(phase) * Decay(t, 0.06f) * 0.9f + noise.Next() * Decay(t, 0.004f) * 0.4f;
            float chime = 0f;
            float local = t - 0.08f;
            if (local >= 0f)
                chime = (MathF.Sin(Tau * 523.25f * local) + MathF.Sin(Tau * 783.99f * local))
                    * Attack(local, 0.004f) * Decay(local, 0.12f) * 0.3f;
            samples[i] = clunk + chime;
        }
        return samples;
    }

    /// <summary>Fabricating a machine: a rising servo whirr that ends in a latch click.</summary>
    private static float[] Build(ref Noise noise, float seconds, float fromHz, float toHz, float flutterHz, float latchHz)
    {
        var samples = Buffer(seconds);
        float whirrEnd = seconds - 0.08f;
        float phase = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            float whirr = 0f;
            if (t < whirrEnd)
            {
                float progress = t / whirrEnd;
                phase += Tau * (fromHz + (toHz - fromHz) * MathF.Pow(progress, 1.5f)) / Rate;
                for (int k = 1; k <= 6; k++)
                    whirr += MathF.Sin(k * phase) / k;
                whirr *= (1f + 0.3f * MathF.Sin(Tau * flutterHz * t)) * Attack(t, 0.01f) * Attack(whirrEnd - t, 0.03f) * 0.45f;
            }
            float latch = 0f;
            float local = t - (whirrEnd + 0.01f);
            if (local >= 0f)
                latch = noise.Next() * Decay(local, 0.006f) * 0.8f + MathF.Sin(Tau * latchHz * local) * Decay(local, 0.03f) * 0.5f;
            samples[i] = whirr + latch;
        }
        return samples;
    }

    /// <summary>Setting a machine into the ground: a thud and two confirming blips.</summary>
    private static float[] Plant(ref Noise noise)
    {
        var samples = Buffer(0.36f);
        float phase = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            phase += Tau * (55f + 95f * Decay(t, 0.04f)) / Rate;
            float sample = MathF.Sin(phase) * Decay(t, 0.09f) + noise.Next() * Decay(t, 0.005f) * 0.5f;
            sample += Blip(t - 0.12f, 660f, 0.05f) * 0.4f + Blip(t - 0.2f, 990f, 0.07f) * 0.4f;
            samples[i] = sample;
        }
        return samples;
    }

    /// <summary>The relay would not link: two low buzzes.</summary>
    private static float[] PlantRefused()
    {
        var samples = Buffer(0.3f);
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            samples[i] = Buzz(t, 0f, 0.11f, 140f) + Buzz(t, 0.15f, 0.28f, 140f);
        }
        return samples;
    }

    /// <summary>Ore picked up: two bright bell notes.</summary>
    private static float[] Pickup()
    {
        var samples = Buffer(0.42f);
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            samples[i] = Bell(t, 1318.51f, 0.09f) + Bell(t - 0.07f, 1975.53f, 0.16f);
        }
        return samples;
    }

    /// <summary>Overheating alarm: an urgent two-tone alternation.</summary>
    private static float[] WarnOverheat()
    {
        var samples = Buffer(0.5f);
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            samples[i] = Buzz(t, 0f, 0.11f, 880f) + Buzz(t, 0.12f, 0.23f, 660f)
                + Buzz(t, 0.24f, 0.35f, 880f) + Buzz(t, 0.36f, 0.47f, 660f);
        }
        return samples;
    }

    /// <summary>Shield low: a double beep.</summary>
    private static float[] WarnShield()
    {
        var samples = Buffer(0.3f);
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            samples[i] = Buzz(t, 0f, 0.1f, 520f) + Buzz(t, 0.15f, 0.25f, 520f);
        }
        return samples;
    }

    /// <summary>Energy low: one falling tone, like something winding down.</summary>
    private static float[] WarnEnergy()
    {
        var samples = Buffer(0.4f);
        float phase = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            phase += Tau * (700f - 280f * MathF.Min(1f, t / 0.35f)) / Rate;
            samples[i] = (MathF.Sin(phase) + 0.2f * MathF.Sin(2f * phase)) * Attack(t, 0.005f) * Decay(t, 0.2f);
        }
        return samples;
    }

    // ---- Loops -------------------------------------------------------------------------------------------------
    // Every tone in a loop has a whole number of cycles in the loop's length, and the noise is cross-faded onto
    // itself, so the end runs into the start without a click.

    /// <summary>Engine and treads: a low chugging drone with a rattle on each beat.</summary>
    private static float[] EngineLoop(ref Noise noise)
    {
        var samples = Buffer(1f);
        float[] rattle = SeamlessNoise(ref noise, samples.Length, 150f, 700f);
        Span<float> phases = stackalloc float[10];
        for (int k = 0; k < phases.Length; k++)
            phases[k] = noise.Next01() * Tau;

        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            float drone = 0f;
            for (int k = 1; k <= phases.Length; k++)
                drone += MathF.Sin(Tau * 55f * k * t + phases[k - 1]) * (k == 1 ? 0.5f : 1f / MathF.Sqrt(k));
            float beat = MathF.Sin(Tau * 6f * t);
            beat *= beat;
            samples[i] = drone * (0.75f + 0.25f * MathF.Sin(Tau * 12f * t)) * 0.5f + rattle[i] * beat * beat * 1.6f;
        }
        return samples;
    }

    /// <summary>Tracks grinding through dirt: gritty mid noise that surges unevenly over a low rumble.</summary>
    private static float[] DigLoop(ref Noise noise)
    {
        var samples = Buffer(1f);
        float[] grit = SeamlessNoise(ref noise, samples.Length, 120f, 1400f);
        float[] rumble = SeamlessNoise(ref noise, samples.Length, 30f, 120f);
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            float surge = 0.55f + 0.2f * MathF.Sin(Tau * 7f * t) + 0.15f * MathF.Sin(Tau * 13f * t + 1.3f)
                + 0.1f * MathF.Sin(Tau * 23f * t + 0.4f);
            samples[i] = grit[i] * MathF.Max(0.15f, surge) + rumble[i] * 2.5f;
        }
        return samples;
    }

    /// <summary>The cutting torch: a fluttering high hiss over a low roar.</summary>
    private static float[] TorchLoop(ref Noise noise)
    {
        var samples = Buffer(1f);
        float[] hiss = SeamlessNoise(ref noise, samples.Length, 1500f, 4500f);
        float[] roar = SeamlessNoise(ref noise, samples.Length, 60f, 300f);
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            samples[i] = hiss[i] * (0.8f + 0.2f * MathF.Sin(Tau * 17f * t))
                + roar[i] * (0.7f + 0.3f * MathF.Sin(Tau * 9f * t)) * 1.5f;
        }
        return samples;
    }

    /// <summary>
    /// Another tank's treads: a dry clank of links meeting the sprocket, eight to the second, over a gritty squeal.
    /// It sits above the engine's drone so it stays clear when muffled to a thud, and tells a moving tank from an idle one.
    /// </summary>
    private static float[] TreadLoop(ref Noise noise)
    {
        var samples = Buffer(1f);
        float[] grit = SeamlessNoise(ref noise, samples.Length, 400f, 2400f);
        float[] body = SeamlessNoise(ref noise, samples.Length, 80f, 260f);
        const int Clanks = 8;
        Span<float> accents = stackalloc float[Clanks];
        for (int k = 0; k < Clanks; k++)
            accents[k] = 0.7f + 0.3f * noise.Next01();

        int period = samples.Length / Clanks;
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            int clank = i / period % Clanks;
            float since = (i % period) / (float)Rate;
            float hit = Decay(since, 0.012f) * accents[clank];
            float ring = MathF.Sin(Tau * 320f * t) * Decay(since, 0.03f) * 0.4f;
            float squeal = 0.25f + 0.1f * MathF.Sin(Tau * 3f * t);
            samples[i] = grit[i] * (hit * 2.2f + squeal) + body[i] * hit * 2.5f + ring * accents[clank];
        }
        return samples;
    }

    /// <summary>Docked and being serviced: a steady electrical hum with a slow shimmer.</summary>
    private static float[] RechargeLoop()
    {
        var samples = Buffer(2f);
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)Rate;
            float hum = MathF.Sin(Tau * 110f * t) + 0.5f * MathF.Sin(Tau * 220f * t) + 0.3f * MathF.Sin(Tau * 165f * t);
            float shimmer = MathF.Sin(Tau * 1320f * t) * 0.12f * (0.5f + 0.5f * MathF.Sin(Tau * 1.5f * t));
            samples[i] = hum * (0.8f + 0.2f * MathF.Sin(Tau * 3f * t)) + shimmer;
        }
        return samples;
    }

    // ---- Building blocks ---------------------------------------------------------------------------------------

    private static float[] Buffer(float seconds) => new float[(int)(seconds * Rate)];

    private static float Decay(float t, float timeConstant) => MathF.Exp(-t / timeConstant);

    /// <summary>Ramp from 0 to 1 over <paramref name="seconds"/>; 0 before the start.</summary>
    private static float Attack(float t, float seconds) => Math.Clamp(t / seconds, 0f, 1f);

    private static float Blip(float t, float frequency, float timeConstant) =>
        t < 0f ? 0f : MathF.Sin(Tau * frequency * t) * Attack(t, 0.003f) * Decay(t, timeConstant);

    private static float Bell(float t, float frequency, float timeConstant) =>
        t < 0f
            ? 0f
            : (MathF.Sin(Tau * frequency * t) * Decay(t, timeConstant)
               + 0.25f * MathF.Sin(Tau * 2.76f * frequency * t) * Decay(t, timeConstant * 0.5f)) * Attack(t, 0.002f);

    /// <summary>A soft square tone (odd harmonics) sounding from <paramref name="start"/> to <paramref name="end"/>.</summary>
    private static float Buzz(float t, float start, float end, float frequency)
    {
        if (t < start || t > end)
            return 0f;
        float local = t - start;
        float phase = Tau * frequency * local;
        float tone = MathF.Sin(phase) + MathF.Sin(3f * phase) / 3f + MathF.Sin(5f * phase) / 5f;
        return tone * Attack(local, 0.006f) * Attack(end - t, 0.006f);
    }

    /// <summary>Noise limited to a frequency band whose last samples blend into its first, so it can repeat.</summary>
    private static float[] SeamlessNoise(ref Noise noise, int length, float lowHz, float highHz)
    {
        int fade = Rate / 10;
        var raw = new float[length + fade];
        var top = new LowPass();
        var top2 = new LowPass();
        var bottom = new LowPass();
        // Run the filters in before keeping samples so the loop does not start on their settling.
        for (int i = -Rate / 20; i < raw.Length; i++)
        {
            // Two stages on the high side: one alone lets too much hiss through above the band.
            float band = top2.Process(top.Process(noise.Next(), highHz), highHz);
            band -= bottom.Process(band, lowHz);
            if (i >= 0)
                raw[i] = band;
        }

        var loop = new float[length];
        for (int i = 0; i < length; i++)
        {
            if (i >= fade)
            {
                loop[i] = raw[i];
                continue;
            }
            // Equal-power blend: the two pieces are unrelated noise.
            float mix = i / (float)fade;
            loop[i] = raw[i] * MathF.Sqrt(mix) + raw[length + i] * MathF.Sqrt(1f - mix);
        }
        return loop;
    }

    /// <summary>
    /// Scales to a common peak. One-shots also get a short fade at the very end so a tail that has not quite died
    /// away does not stop with a click, and a very short one at the start.
    /// </summary>
    private static float[] Normalize(float[] samples, bool isLoop)
    {
        if (!isLoop)
        {
            int fade = Math.Min(samples.Length, Rate / 200);
            for (int i = 0; i < fade; i++)
                samples[samples.Length - 1 - i] *= i / (float)fade;
            // And half a millisecond at the front, so a bang starts steeply but not as a step.
            int rise = Math.Min(samples.Length, Rate / 2000);
            for (int i = 0; i < rise; i++)
                samples[i] *= i / (float)rise;
        }

        float peak = 0f;
        double squares = 0.0;
        foreach (float sample in samples)
        {
            peak = MathF.Max(peak, MathF.Abs(sample));
            squares += sample * (double)sample;
        }
        if (peak > 0f)
        {
            // Steady tones (the buzzers) carry far more energy at the same peak than a bang does, so one-shots
            // are also held to a common average level.
            float scale = PeakLevel / peak;
            float rms = (float)Math.Sqrt(squares / samples.Length) * scale;
            if (!isLoop && rms > MaxOneShotRms)
                scale *= MaxOneShotRms / rms;
            for (int i = 0; i < samples.Length; i++)
                samples[i] *= scale;
        }
        return samples;
    }

    /// <summary>One-pole low-pass filter whose cutoff can change every sample.</summary>
    private struct LowPass
    {
        private float _state;

        public float Process(float input, float cutoffHz)
        {
            _state += (input - _state) * (1f - MathF.Exp(-Tau * cutoffHz / Rate));
            return _state;
        }
    }

    /// <summary>Small deterministic white-noise source (xorshift).</summary>
    private struct Noise(uint seed)
    {
        private uint _state = seed == 0 ? 1u : seed;

        private uint NextBits()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state;
        }

        /// <summary>Uniform in [-1, 1).</summary>
        public float Next() => (NextBits() >> 8) * (2f / 16777216f) - 1f;

        /// <summary>Uniform in [0, 1).</summary>
        public float Next01() => (NextBits() >> 8) * (1f / 16777216f);
    }
}
