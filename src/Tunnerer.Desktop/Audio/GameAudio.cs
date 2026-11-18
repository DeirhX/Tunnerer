namespace Tunnerer.Desktop.Audio;

using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Desktop.Config;

/// <summary>
/// The desktop's sound: the synthesised <see cref="SoundBank"/>, the mixer and the sound device, volume and mute, and
/// capturing to a file. What each simulation step plays is decided by <see cref="SoundDirector"/>, which the browser
/// shares; this class gives it the mixer to play into.
/// </summary>
public sealed class GameAudio : IDisposable
{
    private const int StepSamples = AudioTweaks.SampleRate / Tweaks.Perf.TargetFps;

    /// <summary>A capture file is rewritten every this many steps (two seconds).</summary>
    private const int CaptureFlushSteps = 2 * Tweaks.Perf.TargetFps;

    private readonly SoundBank? _bank;
    private readonly SoundMixer? _mixer;
    private readonly SdlAudioOutput? _device;
    private readonly string? _capturePath;
    private readonly List<float> _capture = new();
    private readonly float[] _captureStep = new float[StepSamples * 2];
    private readonly SoundDirector? _director;
    private bool _disposed;

    /// <summary>False when this run makes no sound at all (automated runs, or no sound device).</summary>
    public bool Enabled => _mixer is not null;

    private GameAudio(bool automatedRun)
    {
        _capturePath = AudioOptions.CapturePath;
        if (_capturePath is null && automatedRun)
        {
            Console.WriteLine("[Audio] Off (automated run).");
            return;
        }

        var mixer = new SoundMixer(AudioOptions.Volume, AudioOptions.Muted);
        if (_capturePath is null)
        {
            _device = SdlAudioOutput.TryOpen(mixer);
            if (_device is null)
                return;
            Console.WriteLine($"[Audio] On, volume {VolumePercent}%{(AudioOptions.Muted ? " (muted)" : "")}.");
        }
        else
        {
            Console.WriteLine($"[Audio] Capturing to {_capturePath} (no sound device).");
        }

        bool logEvents = _capturePath is not null ||
            DesktopDisplayOptions.ParseFlag(Environment.GetEnvironmentVariable("TUNNERER_AUDIO_LOG"));
        _bank = new SoundBank();
        _mixer = mixer;
        _director = new SoundDirector(new MixerSink(mixer, _bank), logEvents ? Console.WriteLine : null);
    }

    /// <summary>Plays what <see cref="SoundDirector"/> decides into the mixer, with the bank's samples.</summary>
    private sealed class MixerSink(SoundMixer mixer, SoundBank bank) : ISoundSink
    {
        public void Play(SoundEffect effect, float gain, float pan, float rate) =>
            mixer.Play(effect, bank[effect], gain, pan, rate);

        public int AddLoop(SoundEffect effect) => mixer.AddLoop(bank[effect]);

        public void SetLoop(int handle, float gain, float pan = 0f, float rate = 1f) =>
            mixer.SetLoop(handle, gain, pan, rate);
    }

    public static GameAudio Create(bool automatedRun) => new(automatedRun);

    private static int VolumePercent => (int)MathF.Round(AudioOptions.Volume * 100f);

    public void SetPaused(bool paused) => _mixer?.SetPaused(paused);

    public void ToggleMute() => SetMuted(!AudioOptions.Muted);

    public void SetMuted(bool muted)
    {
        if (_mixer is null || muted == AudioOptions.Muted) return;
        AudioOptions.Muted = muted;
        _mixer.SetMaster(AudioOptions.Volume, muted);
        Console.WriteLine($"[Audio] {(muted ? "Muted" : $"Unmuted, volume {VolumePercent}%")}.");
        PlayVolumeSample();
    }

    /// <summary>Changes the master volume by <paramref name="delta"/> (a fraction of full volume) and unmutes.</summary>
    public void AdjustVolume(float delta) => SetVolume(AudioOptions.Volume + delta);

    /// <summary>Sets the master volume (0 to 1, kept to steps of 5%) and unmutes.</summary>
    public void SetVolume(float volume)
    {
        if (_mixer is null) return;
        volume = Math.Clamp(MathF.Round(volume * 20f) / 20f, 0f, 1f);
        if (volume == AudioOptions.Volume && !AudioOptions.Muted) return;
        AudioOptions.Volume = volume;
        AudioOptions.Muted = false;
        _mixer.SetMaster(volume, muted: false);
        Console.WriteLine($"[Audio] Volume {VolumePercent}%.");
        PlayVolumeSample();
    }

    /// <summary>A short chime at the new level, so a change of volume can be heard, in the pause menu too.</summary>
    private void PlayVolumeSample() =>
        _mixer!.Play(SoundEffect.Pickup, _bank![SoundEffect.Pickup], AudioTweaks.PickupGain, menuSound: true);


    /// <summary>Call after each simulation step, while <see cref="World.StepEvents"/> still holds that step's events.</summary>
    public void AfterStep(World world, int localSlot)
    {
        if (_mixer is null || _director is null) return;

        _director.AfterStep(world, localSlot);
        if (_capturePath is not null)
        {
            _mixer.Mix(_captureStep);
            _capture.AddRange(_captureStep);
            // Scripted runs are usually stopped by killing the process, so keep the file up to date.
            if (_director.Step % CaptureFlushSteps == 0)
                WriteCapture();
        }
    }

    private (float Peak, float Rms) WriteCapture()
    {
        var samples = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_capture);
        WavFile.Write(_capturePath!, samples, channels: 2);
        return Levels(samples);
    }

    /// <summary>
    /// Writes every effect to a WAV file in <paramref name="directory"/> and prints its length and levels
    /// (<c>--dump-sounds=DIR</c>), for listening to them one by one and for checking them without a sound device.
    /// </summary>
    public static void DumpEffects(string directory)
    {
        var bank = new SoundBank();
        Directory.CreateDirectory(directory);
        foreach (SoundEffect effect in Enum.GetValues<SoundEffect>())
        {
            float[] samples = bank[effect];
            (float peak, float rms) = Levels(samples);
            string line = $"[Audio] {effect,-15} {samples.Length / (float)AudioTweaks.SampleRate:F2} s  peak={peak:F3}  rms={rms:F3}";
            if (SoundBank.IsLoop(effect))
            {
                // A clean loop's jump from its last sample to its first is no larger than its other steps.
                float seam = MathF.Abs(samples[0] - samples[^1]);
                float largestStep = 0f;
                for (int i = 1; i < samples.Length; i++)
                    largestStep = MathF.Max(largestStep, MathF.Abs(samples[i] - samples[i - 1]));
                line += $"  loop seam={seam:F4} (largest step {largestStep:F4})";
            }
            Console.WriteLine(line);
            WavFile.Write(Path.Combine(directory, effect + ".wav"), samples, channels: 1);
        }
        Console.WriteLine($"[Audio] Wrote {Enum.GetValues<SoundEffect>().Length} effects to {Path.GetFullPath(directory)}");
    }

    private static (float Peak, float Rms) Levels(ReadOnlySpan<float> samples)
    {
        float peak = 0f;
        double squares = 0.0;
        foreach (float sample in samples)
        {
            peak = MathF.Max(peak, MathF.Abs(sample));
            squares += sample * (double)sample;
        }
        return (peak, samples.Length > 0 ? (float)Math.Sqrt(squares / samples.Length) : 0f);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _device?.Dispose();
        if (_capturePath is not null && _mixer is not null)
        {
            (float peak, float rms) = WriteCapture();
            Console.WriteLine($"[Audio] Captured {_director?.Step ?? 0} steps ({_capture.Count / 2f / AudioTweaks.SampleRate:F1} s) to " +
                $"{_capturePath}: peak={peak:F3} rms={rms:F3}");
        }
    }
}
