namespace Emberward.Desktop.Audio;

using Emberward.Desktop.Config;

/// <summary>
/// Mixes one-shot effects and continuous loops into interleaved stereo float samples. The game thread starts
/// effects and steers loops; <see cref="Mix"/> runs on the sound device's thread (or on the game thread when
/// capturing to a file). A lock guards the shared state; both sides hold it only briefly.
/// </summary>
public sealed class SoundMixer
{
    // Per-sample smoothing towards a target; about 25 ms to cover most of a change, so nothing clicks.
    private const float Smoothing = 1f / (0.025f * AudioTweaks.SampleRate);

    /// <summary>Samples over which a replaced voice fades out.</summary>
    private const int StealFadeSamples = 256;

    /// <summary>Above this level the output bends towards full scale instead of clipping.</summary>
    private const float LimiterKnee = 0.75f;

    private struct Voice
    {
        public float[]? Data;
        public SoundEffect Effect;
        public double Position;
        public double Rate;
        public float GainLeft, GainRight;
        public float Filter, Low1Left, Low1Right, Low2Left, Low2Right;
        public int FadeOutLeft;
        public long Started;
        public bool IsMenuSound;
    }

    private sealed class Loop
    {
        public required float[] Data;
        public double Position;
        public float Gain, TargetGain;
        public float Pan, TargetPan;
        public float Rate = 1f, TargetRate = 1f;
        public float Filter = 1f, TargetFilter = 1f;
        public float Low1, Low2;
    }

    private readonly object _lock = new();
    private readonly Voice[] _voices = new Voice[AudioTweaks.MaxVoices];
    private readonly List<Loop> _loops = new();
    private long _startCounter;
    private float _masterTarget;
    private float _master;
    private bool _paused;
    private float _pauseGain = 1f;

    public SoundMixer(float volume, bool muted)
    {
        SetMaster(volume, muted);
        _master = _masterTarget;
    }

    public void SetMaster(float volume, bool muted)
    {
        lock (_lock)
            _masterTarget = muted ? 0f : Math.Clamp(volume, 0f, 1f);
    }

    /// <summary>
    /// While paused the game's sounds fade to silence and hold their place. Menu sounds (see <see cref="Play"/>)
    /// still play, so the pause menu's volume control can be heard.
    /// </summary>
    public void SetPaused(bool paused)
    {
        lock (_lock)
            _paused = paused;
    }

    /// <summary>
    /// Starts a one-shot. <paramref name="pan"/> runs from -1 (left) to 1 (right); <paramref name="rate"/> is the
    /// playback speed, which also shifts the pitch; <paramref name="muffle"/> (0 to 1) dulls it as if heard through
    /// the ground (<see cref="SoundSpace.CutoffHz"/>). A <paramref name="menuSound"/> is not silenced by pausing.
    /// </summary>
    public void Play(SoundEffect effect, float[] data, float gain, float pan = 0f, float rate = 1f, bool menuSound = false,
        float muffle = 0f)
    {
        if (gain <= 0f || data.Length == 0)
            return;

        (float left, float right) = PanGains(pan);
        lock (_lock)
        {
            int free = -1, sameCount = 0, oldestSame = -1;
            for (int i = 0; i < _voices.Length; i++)
            {
                ref Voice voice = ref _voices[i];
                if (voice.Data is null)
                {
                    if (free < 0) free = i;
                    continue;
                }
                if (voice.Effect != effect || voice.FadeOutLeft > 0)
                    continue;
                sameCount++;
                if (oldestSame < 0 || voice.Started < _voices[oldestSame].Started)
                    oldestSame = i;
            }

            if (sameCount >= AudioTweaks.MaxVoicesPerEffect && oldestSame >= 0)
                _voices[oldestSame].FadeOutLeft = StealFadeSamples;
            if (free < 0)
                return;

            _voices[free] = new Voice
            {
                Data = data,
                Effect = effect,
                Position = 0,
                Rate = Math.Clamp(rate, 0.25f, 4f),
                GainLeft = gain * left,
                GainRight = gain * right,
                Filter = FilterCoefficient(muffle),
                Started = _startCounter++,
                IsMenuSound = menuSound,
            };
        }
    }

    /// <summary>Adds a silent loop and returns its handle for <see cref="SetLoop"/>.</summary>
    public int AddLoop(float[] data)
    {
        lock (_lock)
        {
            _loops.Add(new Loop { Data = data });
            return _loops.Count - 1;
        }
    }

    /// <summary>Steers a loop; it glides to the new values. A loop at gain 0 costs nothing.</summary>
    public void SetLoop(int handle, float gain, float pan = 0f, float rate = 1f, float muffle = 0f)
    {
        lock (_lock)
        {
            Loop loop = _loops[handle];
            loop.TargetGain = Math.Max(0f, gain);
            loop.TargetPan = Math.Clamp(pan, -1f, 1f);
            loop.TargetRate = Math.Clamp(rate, 0.25f, 4f);
            loop.TargetFilter = FilterCoefficient(muffle);
        }
    }

    /// <summary>Number of one-shots sounding now (for logs and checks).</summary>
    public int ActiveVoiceCount
    {
        get
        {
            lock (_lock)
            {
                int count = 0;
                foreach (ref readonly Voice voice in _voices.AsSpan())
                    if (voice.Data is not null) count++;
                return count;
            }
        }
    }

    /// <summary>Fills <paramref name="output"/> (left, right, left, right, ...) with the next samples.</summary>
    public void Mix(Span<float> output)
    {
        output.Clear();
        int frames = output.Length / 2;

        lock (_lock)
        {
            // The game's own sounds first; they are scaled by the pause fade before menu sounds are added.
            if (_paused && _pauseGain <= 0.0005f)
            {
                _pauseGain = 0f;
            }
            else
            {
                for (int v = 0; v < _voices.Length; v++)
                    if (!_voices[v].IsMenuSound)
                        MixVoice(ref _voices[v], output, frames);
                foreach (Loop loop in _loops)
                    MixLoop(loop, output, frames);

                float pauseTarget = _paused ? 0f : 1f;
                for (int i = 0; i < frames; i++)
                {
                    _pauseGain += (pauseTarget - _pauseGain) * Smoothing * 4f;
                    output[2 * i] *= _pauseGain;
                    output[2 * i + 1] *= _pauseGain;
                }
            }

            for (int v = 0; v < _voices.Length; v++)
                if (_voices[v].IsMenuSound)
                    MixVoice(ref _voices[v], output, frames);

            for (int i = 0; i < frames; i++)
            {
                _master += (_masterTarget - _master) * Smoothing;
                output[2 * i] = Limit(output[2 * i] * _master);
                output[2 * i + 1] = Limit(output[2 * i + 1] * _master);
            }
        }
    }

    private static void MixVoice(ref Voice voice, Span<float> output, int frames)
    {
        float[]? data = voice.Data;
        if (data is null)
            return;

        int last = data.Length - 1;
        for (int i = 0; i < frames; i++)
        {
            int index = (int)voice.Position;
            if (index >= last)
            {
                voice.Data = null;
                return;
            }

            float fraction = (float)(voice.Position - index);
            float sample = data[index] + (data[index + 1] - data[index]) * fraction;
            if (voice.FadeOutLeft > 0)
            {
                sample *= voice.FadeOutLeft / (float)StealFadeSamples;
                if (--voice.FadeOutLeft == 0)
                {
                    voice.Data = null;
                    return;
                }
            }

            // A muffled voice runs through two low-pass stages on each side, as a loop does (and as steep as the
            // browser's filter); a clear one (coefficient 1) passes unchanged.
            float left = sample * voice.GainLeft, right = sample * voice.GainRight;
            voice.Low1Left += (left - voice.Low1Left) * voice.Filter;
            voice.Low1Right += (right - voice.Low1Right) * voice.Filter;
            voice.Low2Left += (voice.Low1Left - voice.Low2Left) * voice.Filter;
            voice.Low2Right += (voice.Low1Right - voice.Low2Right) * voice.Filter;
            output[2 * i] += voice.Low2Left;
            output[2 * i + 1] += voice.Low2Right;
            voice.Position += voice.Rate;
        }
    }

    private static void MixLoop(Loop loop, Span<float> output, int frames)
    {
        if (loop.TargetGain <= 0f && loop.Gain < 0.0002f)
        {
            loop.Gain = 0f;
            loop.Pan = loop.TargetPan;
            loop.Rate = loop.TargetRate;
            loop.Filter = loop.TargetFilter;
            loop.Low1 = loop.Low2 = 0f;
            return;
        }

        float[] data = loop.Data;
        int length = data.Length;
        for (int i = 0; i < frames; i++)
        {
            loop.Gain += (loop.TargetGain - loop.Gain) * Smoothing;
            loop.Pan += (loop.TargetPan - loop.Pan) * Smoothing;
            loop.Rate += (loop.TargetRate - loop.Rate) * Smoothing;
            loop.Filter += (loop.TargetFilter - loop.Filter) * Smoothing;

            int index = (int)loop.Position;
            float fraction = (float)(loop.Position - index);
            float next = data[index + 1 < length ? index + 1 : 0];
            float sample = (data[index] + (next - data[index]) * fraction) * loop.Gain;
            // Two low-pass stages, so ground in the way leaves a dull rumble rather than a thinner hiss.
            loop.Low1 += (sample - loop.Low1) * loop.Filter;
            loop.Low2 += (loop.Low1 - loop.Low2) * loop.Filter;
            sample = loop.Low2;

            // Linear pan is enough for a gliding loop and avoids two square roots per sample.
            output[2 * i] += sample * (1f - Math.Max(0f, loop.Pan));
            output[2 * i + 1] += sample * (1f + Math.Min(0f, loop.Pan));

            loop.Position += loop.Rate;
            if (loop.Position >= length)
                loop.Position -= length;
        }
    }

    /// <summary>One-pole low-pass coefficient for <paramref name="muffle"/>: 1 (no filtering) when nothing muffles it.</summary>
    private static float FilterCoefficient(float muffle) =>
        muffle <= 0.001f ? 1f : 1f - MathF.Exp(-2f * MathF.PI * SoundSpace.CutoffHz(muffle) / AudioTweaks.SampleRate);

    /// <summary>Constant-power pan: the sum of the squared gains is 1 at every position.</summary>
    private static (float Left, float Right) PanGains(float pan)
    {
        float angle = (Math.Clamp(pan, -1f, 1f) + 1f) * (MathF.PI / 4f);
        // Scaled so that a centred sound has gain 1 in both speakers.
        return (MathF.Cos(angle) * MathF.Sqrt(2f), MathF.Sin(angle) * MathF.Sqrt(2f));
    }

    /// <summary>Passes quiet samples unchanged and bends loud ones so the output never reaches full scale.</summary>
    public static float Limit(float sample)
    {
        float magnitude = MathF.Abs(sample);
        if (magnitude <= LimiterKnee)
            return sample;
        float room = 1f - LimiterKnee;
        float limited = LimiterKnee + room * MathF.Tanh((magnitude - LimiterKnee) / room);
        return sample < 0f ? -limited : limited;
    }
}
