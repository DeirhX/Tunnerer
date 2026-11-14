namespace Tunnerer.Desktop.Audio;

using Tunnerer.Desktop.Config;

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
        public int FadeOutLeft;
        public long Started;
    }

    private sealed class Loop
    {
        public required float[] Data;
        public double Position;
        public float Gain, TargetGain;
        public float Pan, TargetPan;
        public float Rate = 1f, TargetRate = 1f;
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

    /// <summary>While paused the mix fades to silence and every sound holds its place.</summary>
    public void SetPaused(bool paused)
    {
        lock (_lock)
            _paused = paused;
    }

    /// <summary>
    /// Starts a one-shot. <paramref name="pan"/> runs from -1 (left) to 1 (right); <paramref name="rate"/> is the
    /// playback speed, which also shifts the pitch.
    /// </summary>
    public void Play(SoundEffect effect, float[] data, float gain, float pan = 0f, float rate = 1f)
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
                Started = _startCounter++,
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
    public void SetLoop(int handle, float gain, float pan = 0f, float rate = 1f)
    {
        lock (_lock)
        {
            Loop loop = _loops[handle];
            loop.TargetGain = Math.Max(0f, gain);
            loop.TargetPan = Math.Clamp(pan, -1f, 1f);
            loop.TargetRate = Math.Clamp(rate, 0.25f, 4f);
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
            if (_paused && _pauseGain <= 0.0005f)
            {
                _pauseGain = 0f;
                return;
            }

            for (int v = 0; v < _voices.Length; v++)
                MixVoice(ref _voices[v], output, frames);
            foreach (Loop loop in _loops)
                MixLoop(loop, output, frames);

            float pauseTarget = _paused ? 0f : 1f;
            for (int i = 0; i < frames; i++)
            {
                _master += (_masterTarget - _master) * Smoothing;
                _pauseGain += (pauseTarget - _pauseGain) * Smoothing * 4f;
                float gain = _master * _pauseGain;
                output[2 * i] = Limit(output[2 * i] * gain);
                output[2 * i + 1] = Limit(output[2 * i + 1] * gain);
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

            output[2 * i] += sample * voice.GainLeft;
            output[2 * i + 1] += sample * voice.GainRight;
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
            return;
        }

        float[] data = loop.Data;
        int length = data.Length;
        for (int i = 0; i < frames; i++)
        {
            loop.Gain += (loop.TargetGain - loop.Gain) * Smoothing;
            loop.Pan += (loop.TargetPan - loop.Pan) * Smoothing;
            loop.Rate += (loop.TargetRate - loop.Rate) * Smoothing;

            int index = (int)loop.Position;
            float fraction = (float)(loop.Position - index);
            float next = data[index + 1 < length ? index + 1 : 0];
            float sample = (data[index] + (next - data[index]) * fraction) * loop.Gain;

            // Linear pan is enough for a gliding loop and avoids two square roots per sample.
            output[2 * i] += sample * (1f - Math.Max(0f, loop.Pan));
            output[2 * i + 1] += sample * (1f + Math.Min(0f, loop.Pan));

            loop.Position += loop.Rate;
            if (loop.Position >= length)
                loop.Position -= length;
        }
    }

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
