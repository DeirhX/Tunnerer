namespace Tunnerer.Desktop.Audio;

using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Config;
using Tunnerer.Core.Entities.Tanks;

/// <summary>
/// Turns what happens in the simulation into sound. It is called once after every simulation step (never per
/// displayed frame) and plays the events that step recorded (<see cref="World.StepEvents"/>): one-shots for shots,
/// impacts, deaths and the like, and the engine, digging, torch and recharge loops from the tanks' activity. It
/// only reads simulation output; nothing here feeds back into the simulation.
/// </summary>
public sealed class GameAudio : IDisposable
{
    /// <summary>Tanks beyond this many make no sound of their own (a match has two).</summary>
    private const int MaxTrackedTanks = 4;

    private const int StepSamples = AudioTweaks.SampleRate / Tweaks.Perf.TargetFps;

    /// <summary>A capture file is rewritten every this many steps (two seconds).</summary>
    private const int CaptureFlushSteps = 2 * Tweaks.Perf.TargetFps;

    private struct TankTrack
    {
        public int Shots;
        public bool Moved, Dug, Torched;
        public int MoveHold, DigHold, TorchHold;
        public int LastPickupStep, PickupStreak;
        public int EngineLoop, DigLoop, TorchLoop;
        public bool EngineOn, DigOn, TorchOn;
    }

    private readonly SoundBank? _bank;
    private readonly SoundMixer? _mixer;
    private readonly SdlAudioOutput? _device;
    private readonly string? _capturePath;
    private readonly List<float> _capture = new();
    private readonly float[] _captureStep = new float[StepSamples * 2];
    private readonly bool _logEvents;
    private readonly TankTrack[] _tanks = new TankTrack[MaxTrackedTanks];
    private readonly int _rechargeLoop;
    private bool _rechargeOn;
    private int _serviceHold;
    private Position _listener;
    private int _step;
    private int _lastDebrisStep = -1000;
    private bool _runningHot, _lowShield;
    private int _nextWarningStep, _lastWarningStep = -1000, _lastWarningRank;
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

        _logEvents = _capturePath is not null ||
            DesktopDisplayOptions.ParseFlag(Environment.GetEnvironmentVariable("TUNNERER_AUDIO_LOG"));
        _bank = new SoundBank();
        _mixer = mixer;
        for (int i = 0; i < _tanks.Length; i++)
        {
            _tanks[i].EngineLoop = mixer.AddLoop(_bank[SoundEffect.EngineLoop]);
            _tanks[i].DigLoop = mixer.AddLoop(_bank[SoundEffect.DigLoop]);
            _tanks[i].TorchLoop = mixer.AddLoop(_bank[SoundEffect.TorchLoop]);
        }
        _rechargeLoop = mixer.AddLoop(_bank[SoundEffect.RechargeLoop]);
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
        if (_mixer is null) return;

        _step++;
        var tanks = world.TankList.Tanks;
        Tank? player = localSlot < tanks.Count ? tanks[localSlot] : null;
        if (player != null)
            _listener = player.Position;
        int playerColor = player?.Color ?? -1;

        for (int i = 0; i < _tanks.Length; i++)
            _tanks[i].Moved = _tanks[i].Dug = _tanks[i].Torched = false;
        bool serviced = false;
        int impacts = 0, shrapnel = 0;
        float shrapnelX = 0f, shrapnelY = 0f;

        foreach (ref readonly StepEvent e in world.StepEvents)
        {
            int index = TankIndex(tanks, e.Color);
            switch (e.Kind)
            {
                case StepEventKind.TankMoved when index >= 0:
                    _tanks[index].Moved = true;
                    break;
                case StepEventKind.TankDug when index >= 0:
                    _tanks[index].Dug = true;
                    _tanks[index].Torched |= e.Torch;
                    break;
                case StepEventKind.BaseServiced:
                    serviced |= e.Color == playerColor;
                    break;
                case StepEventKind.ShrapnelSpent:
                    if (Locate(e.Position, out _, out _))
                    {
                        shrapnel++;
                        shrapnelX += e.Position.X;
                        shrapnelY += e.Position.Y;
                    }
                    break;
                default:
                    if (StepEventSounds.CueFor(e.Kind, e.Machine) is { } cue &&
                        StepEventSounds.Hears(cue, tracked: index >= 0, isPlayer: e.Color == playerColor))
                        PlayCue(cue, e.Position, index, ref impacts);
                    break;
            }
        }

        _serviceHold = serviced ? AudioTweaks.LoopHoldSteps * 2 : Math.Max(0, _serviceHold - 1);
        for (int i = 0; i < tanks.Count && i < MaxTrackedTanks; i++)
            UpdateLoops(i, tanks[i]);

        // However many fragments land in a step, they make one rattle, and not on every step.
        if (shrapnel >= AudioTweaks.DebrisMinShrapnel && _step - _lastDebrisStep >= AudioTweaks.DebrisIntervalSteps)
        {
            _lastDebrisStep = _step;
            var centre = new Position((int)(shrapnelX / shrapnel), (int)(shrapnelY / shrapnel));
            Play(SoundEffect.Debris, centre, AudioTweaks.DebrisGain * MathF.Min(1f, shrapnel / 12f),
                0.9f + 0.05f * (_step % 5));
        }

        if (player != null)
            ObserveWarnings(player);

        if (_capturePath is not null)
        {
            _mixer.Mix(_captureStep);
            _capture.AddRange(_captureStep);
            // Scripted runs are usually stopped by killing the process, so keep the file up to date.
            if (_step % CaptureFlushSteps == 0)
                WriteCapture();
        }
    }

    /// <summary>
    /// Plays an event's one-shot as its <see cref="CueVoice"/> says. <paramref name="index"/> is the tracked tank the
    /// event is about, or -1; <paramref name="bursts"/> counts this step's <see cref="CueVoice.Burst"/> cues.
    /// </summary>
    private void PlayCue(in StepEventCue cue, Position position, int index, ref int bursts)
    {
        switch (cue.Voice)
        {
            case CueVoice.Burst:
                // However many burst in a step, only a couple are heard.
                if (bursts < AudioTweaks.ImpactsPerStep)
                    Play(cue.Effect, position, cue.Gain, 0.92f + 0.04f * ((_step + bursts) % 5));
                bursts++;
                break;
            case CueVoice.ShotCycle when index >= 0:
                int shots = _tanks[index].Shots++;
                Play(cue.Effect, position, cue.Gain, 0.96f + 0.02f * (shots % 5), index);
                break;
            case CueVoice.PickupStreak when index >= 0:
                PlayPickup(index, position, cue);
                break;
            default:
                Play(cue.Effect, position, cue.Gain, 1f, index);
                break;
        }
    }

    /// <summary>Index of the tank of <paramref name="color"/> among the tracked ones, or -1.</summary>
    private static int TankIndex(IReadOnlyList<Tank> tanks, int color)
    {
        if (color < 0) return -1;
        for (int i = 0; i < tanks.Count && i < MaxTrackedTanks; i++)
            if (tanks[i].Color == color)
                return i;
        return -1;
    }

    private (float Peak, float Rms) WriteCapture()
    {
        var samples = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_capture);
        WavFile.Write(_capturePath!, samples, channels: 2);
        return Levels(samples);
    }

    /// <summary>The engine, digging, torch and (for the player) recharge loops, held a few steps past their last cause.</summary>
    private void UpdateLoops(int index, Tank tank)
    {
        ref TankTrack track = ref _tanks[index];
        bool dead = tank.IsDead;

        track.MoveHold = track.Moved ? 2 : Math.Max(0, track.MoveHold - 1);
        track.DigHold = track.Dug ? AudioTweaks.LoopHoldSteps : Math.Max(0, track.DigHold - 1);
        track.TorchHold = track.Torched ? AudioTweaks.LoopHoldSteps : Math.Max(0, track.TorchHold - 1);
        if (dead)
            track.MoveHold = track.DigHold = track.TorchHold = 0;

        bool audible = Locate(tank.Position, out float gain, out float pan);
        bool moving = track.MoveHold > 0;
        // Each tank's engine sits at a slightly different pitch so two tanks do not sound like one.
        float enginePitch = (moving ? 1.2f : 0.8f) * (1f - 0.07f * index);
        bool engineOn = !dead && audible;
        bool digOn = track.DigHold > 0 && audible;
        bool torchOn = track.TorchHold > 0 && audible;
        _mixer!.SetLoop(track.EngineLoop,
            engineOn ? gain * (moving ? AudioTweaks.EngineMovingGain : AudioTweaks.EngineIdleGain) : 0f, pan, enginePitch);
        // Under the torch the grinding steps back so the two do not pile up.
        _mixer.SetLoop(track.DigLoop, digOn ? gain * AudioTweaks.DigGain * (torchOn ? 0.6f : 1f) : 0f, pan);
        _mixer.SetLoop(track.TorchLoop, torchOn ? gain * AudioTweaks.TorchGain : 0f, pan);
        LogLoop(SoundEffect.EngineLoop, index, ref track.EngineOn, engineOn);
        LogLoop(SoundEffect.DigLoop, index, ref track.DigOn, digOn);
        LogLoop(SoundEffect.TorchLoop, index, ref track.TorchOn, torchOn);

        if (index == 0)
        {
            // Recharging is audible only while the base is actually doing something for the tank.
            bool rechargeOn = tank.IsDockedAtOwnBase && _serviceHold > 0;
            _mixer.SetLoop(_rechargeLoop, rechargeOn ? AudioTweaks.RechargeGain : 0f);
            LogLoop(SoundEffect.RechargeLoop, index, ref _rechargeOn, rechargeOn);
        }
    }

    /// <summary>Ore picked up; runs of pickups climb in pitch, and a burst of them sounds once.</summary>
    private void PlayPickup(int index, Position position, in StepEventCue cue)
    {
        ref TankTrack track = ref _tanks[index];
        int sinceLast = _step - track.LastPickupStep;
        if (track.LastPickupStep != 0 && sinceLast < AudioTweaks.PickupIntervalSteps)
            return;
        track.PickupStreak = track.LastPickupStep != 0 && sinceLast <= 12 ? Math.Min(track.PickupStreak + 1, 8) : 0;
        track.LastPickupStep = _step;
        float pitch = 0.75f * (1f + 0.04f * track.PickupStreak);
        Play(cue.Effect, position, cue.Gain, pitch, index);
    }

    /// <summary>
    /// Warning tones for the player's own tank, repeating while the condition lasts. Like the HUD banner, only
    /// the most urgent one sounds: overheating, then low shield, then running hot.
    /// </summary>
    private void ObserveWarnings(Tank player)
    {
        if (player.IsDead)
        {
            _runningHot = _lowShield = false;
            return;
        }

        _runningHot = player.IsRunningHot(_runningHot);
        _lowShield = player.IsShieldLow(_lowShield);

        (SoundEffect effect, int rank, int interval) = player.IsOverheating
            ? (SoundEffect.WarnOverheat, 3, AudioTweaks.OverheatWarningIntervalSteps)
            : _lowShield ? (SoundEffect.WarnShield, 2, AudioTweaks.ShieldWarningIntervalSteps)
            : _runningHot ? (SoundEffect.WarnEnergy, 1, AudioTweaks.EnergyWarningIntervalSteps)
            : (default, 0, 0);

        if (_step >= _nextWarningStep)
            _lastWarningRank = 0;
        if (rank == 0)
            return;

        // A more urgent warning does not wait for the quieter one's interval to run out.
        bool due = _step >= _nextWarningStep || (rank > _lastWarningRank && _step - _lastWarningStep >= 8);
        if (!due)
            return;

        _lastWarningRank = rank;
        _lastWarningStep = _step;
        _nextWarningStep = _step + interval;
        Play(effect, player.Position, AudioTweaks.WarningGain, 1f, 0);
    }

    /// <summary>
    /// Loudness and stereo position of something at <paramref name="position"/> as heard from the player's tank.
    /// Returns false when it is too far away to hear.
    /// </summary>
    private bool Locate(Position position, out float gain, out float pan)
    {
        float dx = position.X - _listener.X;
        float dy = position.Y - _listener.Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy) / AudioTweaks.HalfLoudnessDistance;
        float falloff = 1f / (1f + distance * distance);
        gain = MathF.Max(0f, (falloff - AudioTweaks.InaudibleGain) / (1f - AudioTweaks.InaudibleGain));
        pan = Math.Clamp(dx / AudioTweaks.FullPanDistance, -1f, 1f) * AudioTweaks.MaxPan;
        return gain > 0f;
    }

    private void Play(SoundEffect effect, Position position, float gain, float rate = 1f, int tank = -1)
    {
        if (!Locate(position, out float distanceGain, out float pan))
            return;

        _mixer!.Play(effect, _bank![effect], gain * distanceGain, pan, rate);
        if (_logEvents)
            Console.WriteLine($"[Audio] step {_step} {effect}{(tank >= 0 ? $" tank={tank}" : "")} " +
                $"gain={gain * distanceGain:F2} pan={pan:+0.00;-0.00} rate={rate:F2}");
    }

    private void LogLoop(SoundEffect effect, int tank, ref bool wasOn, bool on)
    {
        if (on == wasOn)
            return;
        wasOn = on;
        if (_logEvents)
            Console.WriteLine($"[Audio] step {_step} {effect} tank={tank} {(on ? "on" : "off")}");
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
            Console.WriteLine($"[Audio] Captured {_step} steps ({_capture.Count / 2f / AudioTweaks.SampleRate:F1} s) to " +
                $"{_capturePath}: peak={peak:F3} rms={rms:F3}");
        }
    }
}
