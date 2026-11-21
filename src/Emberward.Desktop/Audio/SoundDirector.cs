namespace Emberward.Desktop.Audio;

using Emberward.Core;
using Emberward.Core.Collision;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using Emberward.Desktop.Config;

/// <summary>Where <see cref="SoundDirector"/> sends what to play: the desktop's mixer, or the browser's frame.</summary>
public interface ISoundSink
{
    /// <summary>
    /// Starts a one-shot. <paramref name="pan"/> runs from -1 (left) to 1 (right); <paramref name="rate"/> is the
    /// playback speed, which also shifts the pitch; <paramref name="muffle"/> runs from 0 (clear) to 1 (heard through
    /// thick ground), and the sink dulls it with a low-pass at <see cref="SoundSpace.CutoffHz"/>.
    /// </summary>
    void Play(SoundEffect effect, float gain, float pan, float rate, float muffle = 0f);

    /// <summary>Adds a silent loop of <paramref name="effect"/> and returns its handle for <see cref="SetLoop"/>.</summary>
    int AddLoop(SoundEffect effect);

    /// <summary>Steers a loop; the sink glides it to the new values. A loop at gain 0 is silent.</summary>
    void SetLoop(int handle, float gain, float pan = 0f, float rate = 1f, float muffle = 0f);
}

/// <summary>
/// Turns what happens in the simulation into sound, for the desktop and the browser alike. It is called once after
/// every simulation step (never per displayed frame) and plays the events that step recorded
/// (<see cref="World.StepEvents"/>): one-shots for shots, impacts, deaths and the like, the engine, digging, torch,
/// tread and recharge loops from the tanks' activity, and the player's warning tones. Everything is heard from the
/// player's tank (<see cref="SoundSpace"/>): loudness follows distance, stereo position follows direction, rock and
/// dirt in between muffle it, and another tank's engine and treads rise a little in pitch while it comes closer. It
/// only reads simulation output; nothing here feeds back into the simulation.
/// </summary>
public sealed class SoundDirector
{
    /// <summary>Tanks beyond this many make no sound of their own (a match has two).</summary>
    public const int MaxTrackedTanks = 4;

    private struct TankTrack
    {
        public int Shots;
        public bool Moved, Dug, Torched;
        public int MoveHold, DigHold, TorchHold;
        public int LastPickupStep, PickupStreak;
        public int EngineLoop, DigLoop, TorchLoop, TreadLoop;
        public bool EngineOn, DigOn, TorchOn, TreadOn;

        /// <summary>Distance from the listener at the last step (negative before the first), and how fast it changes.</summary>
        public float Distance, Approach;
    }

    private readonly ISoundSink _sink;
    private readonly Action<string>? _log;
    private readonly TankTrack[] _tanks = new TankTrack[MaxTrackedTanks];
    private readonly int _rechargeLoop;
    private bool _rechargeOn;
    private int _serviceHold;
    private Position _listener;
    private ITerrainPixelReadOnly? _terrain;
    private int _localSlot;
    private int _step;
    private int _lastDebrisStep = -1000;
    private bool _runningHot, _lowShield;
    private int _nextWarningStep, _lastWarningStep = -1000, _lastWarningRank;

    /// <param name="log">Where to describe each sound started and each loop turned on or off, or null.</param>
    public SoundDirector(ISoundSink sink, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _log = log;
        for (int i = 0; i < _tanks.Length; i++)
        {
            _tanks[i].EngineLoop = sink.AddLoop(SoundEffect.EngineLoop);
            _tanks[i].DigLoop = sink.AddLoop(SoundEffect.DigLoop);
            _tanks[i].TorchLoop = sink.AddLoop(SoundEffect.TorchLoop);
            _tanks[i].TreadLoop = sink.AddLoop(SoundEffect.TreadLoop);
            _tanks[i].Distance = -1f;
        }
        _rechargeLoop = sink.AddLoop(SoundEffect.RechargeLoop);
    }

    /// <summary>Steps heard so far.</summary>
    public int Step => _step;

    /// <summary>Call after each simulation step, while <see cref="World.StepEvents"/> still holds that step's events.</summary>
    /// <param name="localSlot">The player's tank, whose position is where everything is heard from.</param>
    public void AfterStep(World world, int localSlot)
    {
        ArgumentNullException.ThrowIfNull(world);
        _step++;
        _terrain = world.Terrain;
        _localSlot = localSlot;
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
                    if (InEarshot(e.Position))
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

    /// <summary>
    /// The engine, digging, torch, tread and (for the player) recharge loops, held a few steps past their last cause.
    /// Another tank's treads clatter while it moves, so where it is and where it is going can be heard.
    /// </summary>
    private void UpdateLoops(int index, Tank tank)
    {
        ref TankTrack track = ref _tanks[index];
        bool dead = tank.IsDead;
        bool isPlayer = index == _localSlot;

        track.MoveHold = track.Moved ? 2 : Math.Max(0, track.MoveHold - 1);
        track.DigHold = track.Dug ? AudioTweaks.LoopHoldSteps : Math.Max(0, track.DigHold - 1);
        track.TorchHold = track.Torched ? AudioTweaks.LoopHoldSteps : Math.Max(0, track.TorchHold - 1);
        if (dead)
            track.MoveHold = track.DigHold = track.TorchHold = 0;

        Heard heard = Locate(tank.Position);
        float approachRate = isPlayer ? 1f : SoundSpace.ApproachRate(TrackApproach(ref track, tank, dead));
        float gain = heard.Gain, pan = heard.Pan, muffle = heard.Muffle;
        bool audible = heard.IsAudible;
        bool moving = track.MoveHold > 0;
        // Each tank's engine sits at a slightly different pitch so two tanks do not sound like one.
        float enginePitch = (moving ? 1.2f : 0.8f) * (1f - 0.07f * index) * approachRate;
        bool engineOn = !dead && audible;
        bool digOn = track.DigHold > 0 && audible;
        bool torchOn = track.TorchHold > 0 && audible;
        bool treadOn = !isPlayer && moving && audible;
        _sink.SetLoop(track.EngineLoop,
            engineOn ? gain * (moving ? AudioTweaks.EngineMovingGain : AudioTweaks.EngineIdleGain) : 0f, pan, enginePitch,
            muffle);
        // Under the torch the grinding steps back so the two do not pile up.
        _sink.SetLoop(track.DigLoop, digOn ? gain * AudioTweaks.DigGain * (torchOn ? 0.6f : 1f) : 0f, pan, 1f, muffle);
        _sink.SetLoop(track.TorchLoop, torchOn ? gain * AudioTweaks.TorchGain : 0f, pan, 1f, muffle);
        _sink.SetLoop(track.TreadLoop, treadOn ? gain * AudioTweaks.TreadGain : 0f, pan,
            (1f - 0.05f * index) * approachRate, muffle);
        LogLoop(SoundEffect.EngineLoop, index, ref track.EngineOn, engineOn);
        LogLoop(SoundEffect.DigLoop, index, ref track.DigOn, digOn);
        LogLoop(SoundEffect.TorchLoop, index, ref track.TorchOn, torchOn);
        LogLoop(SoundEffect.TreadLoop, index, ref track.TreadOn, treadOn);

        if (isPlayer)
        {
            // Recharging is audible only while the base is actually doing something for the tank.
            bool rechargeOn = tank.IsDockedAtOwnBase && _serviceHold > 0;
            _sink.SetLoop(_rechargeLoop, rechargeOn ? AudioTweaks.RechargeGain : 0f);
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
    /// The smoothed rate (cells per step) at which <paramref name="tank"/> comes closer to the listener (negative) or
    /// goes away (positive). A respawn or the listener's own respawn starts it afresh instead of counting as a leap.
    /// </summary>
    private float TrackApproach(ref TankTrack track, Tank tank, bool dead)
    {
        if (dead)
        {
            track.Distance = -1f;
            track.Approach = 0f;
            return 0f;
        }
        float dx = tank.Position.X - _listener.X, dy = tank.Position.Y - _listener.Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        float change = track.Distance < 0f ? 0f : distance - track.Distance;
        if (MathF.Abs(change) > 4f)
            change = 0f;
        track.Distance = distance;
        track.Approach += (change - track.Approach) * AudioTweaks.ApproachSmoothing;
        return track.Approach;
    }

    /// <summary>Whether something at <paramref name="position"/> is near enough to hear, before anything muffles it.</summary>
    private bool InEarshot(Position position) =>
        SoundSpace.Hear(position.X - _listener.X, position.Y - _listener.Y, 0).IsAudible;

    /// <summary>
    /// How something at <paramref name="position"/> is heard from the player's tank: distance, direction, and the
    /// ground between the two (cast only for what is near enough to hear).
    /// </summary>
    private Heard Locate(Position position)
    {
        float dx = position.X - _listener.X, dy = position.Y - _listener.Y;
        Heard heard = SoundSpace.Hear(dx, dy, 0);
        if (!heard.IsAudible || _terrain is null || position == _listener)
            return heard;
        int solid = Raycaster.CountSolidCells(_terrain, _listener, position, AudioTweaks.MuffleRayLimit);
        return solid == 0 ? heard : SoundSpace.Hear(dx, dy, solid);
    }

    private void Play(SoundEffect effect, Position position, float gain, float rate = 1f, int tank = -1)
    {
        Heard heard = Locate(position);
        if (!heard.IsAudible)
            return;

        _sink.Play(effect, gain * heard.Gain, heard.Pan, rate, heard.Muffle);
        _log?.Invoke($"[Audio] step {_step} {effect}{(tank >= 0 ? $" tank={tank}" : "")} " +
            $"gain={gain * heard.Gain:F2} pan={heard.Pan:+0.00;-0.00} rate={rate:F2} muffle={heard.Muffle:F2}");
    }

    private void LogLoop(SoundEffect effect, int tank, ref bool wasOn, bool on)
    {
        if (on == wasOn)
            return;
        wasOn = on;
        _log?.Invoke($"[Audio] step {_step} {effect} tank={tank} {(on ? "on" : "off")}");
    }
}
