namespace Emberward.Desktop.Audio;

using Emberward.Core;
using Emberward.Core.Entities.Machines;
using Emberward.Desktop.Config;

/// <summary>Whose event a one-shot is heard for.</summary>
public enum CueHeard
{
    /// <summary>Whoever caused it, tracked tank or not (a shot, a bullet bursting, ore picked up).</summary>
    Anywhere,

    /// <summary>Only a tank <see cref="GameAudio"/> tracks: what happens to a tank itself.</summary>
    TrackedTank,

    /// <summary>Only the player's own tank: feedback on what the player did.</summary>
    Player,
}

/// <summary>How a one-shot varies from one play to the next.</summary>
public enum CueVoice
{
    /// <summary>The same every time.</summary>
    Plain,

    /// <summary>Only the first few in a step are heard, each at a slightly different pitch.</summary>
    Burst,

    /// <summary>The pitch cycles over the tank's last few shots.</summary>
    ShotCycle,

    /// <summary>Rate-limited per tank, rising in pitch over a quick run of them.</summary>
    PickupStreak,
}

/// <summary>The one-shot a simulation event plays: what, how loud, for whom, and how it varies.</summary>
public readonly record struct StepEventCue(SoundEffect Effect, float Gain, CueHeard Heard, CueVoice Voice = CueVoice.Plain);

/// <summary>
/// Which one-shot effect a simulation event (<see cref="World.StepEvents"/>) plays. This is the one table of event
/// sounds: <see cref="GameAudio"/> plays what it says. Events that make no one-shot of their own (moving, digging,
/// base service and spent shrapnel, which drive loops and the debris rattle in <see cref="GameAudio"/>) and events
/// with no sound yet have no cue. The browser client shares this file and plays the cues it picks with its own tones.
/// </summary>
public static class StepEventSounds
{
    public static StepEventCue? CueFor(StepEventKind kind, MachineType machine) => kind switch
    {
        StepEventKind.ShotFired => new(SoundEffect.Fire, AudioTweaks.FireGain, CueHeard.Anywhere, CueVoice.ShotCycle),
        StepEventKind.TurretFired => new(SoundEffect.TurretFire, AudioTweaks.TurretFireGain, CueHeard.Anywhere, CueVoice.Burst),
        StepEventKind.BulletImpact => new(SoundEffect.Impact, AudioTweaks.ImpactGain, CueHeard.Anywhere, CueVoice.Burst),
        StepEventKind.OreCollected => new(SoundEffect.Pickup, AudioTweaks.PickupGain, CueHeard.Anywhere, CueVoice.PickupStreak),
        StepEventKind.TankHit => new(SoundEffect.TankHit, AudioTweaks.TankHitGain, CueHeard.TrackedTank),
        StepEventKind.BaseDamaged => new(SoundEffect.TankHit, AudioTweaks.BaseHitGain, CueHeard.Player, CueVoice.Burst),
        StepEventKind.BaseDestroyed => new(SoundEffect.TankDeath, AudioTweaks.TankDeathGain, CueHeard.Player),
        StepEventKind.TankDied => new(SoundEffect.TankDeath, AudioTweaks.TankDeathGain, CueHeard.TrackedTank),
        StepEventKind.TankRespawned => new(SoundEffect.Respawn, AudioTweaks.RespawnGain, CueHeard.TrackedTank),
        StepEventKind.MachineBuilt => new(BuildSound(machine), AudioTweaks.BuildGain, CueHeard.TrackedTank),
        StepEventKind.MachinePlanted => new(SoundEffect.Plant, AudioTweaks.PlantGain, CueHeard.TrackedTank),
        StepEventKind.MachineUpgraded => new(SoundEffect.Plant, AudioTweaks.PlantGain, CueHeard.Player),
        StepEventKind.PlantRefused or StepEventKind.BuildRefused or StepEventKind.MachineUpgradeRefused =>
            new(SoundEffect.PlantRefused, AudioTweaks.RefusedGain, CueHeard.Player),
        StepEventKind.OreUnloaded or StepEventKind.ReserveBurned => new(SoundEffect.Pickup, AudioTweaks.UnloadGain, CueHeard.Player),
        StepEventKind.Docked => new(SoundEffect.Dock, AudioTweaks.DockGain, CueHeard.Player),
        // Emberites: a nest waking is an alarm for whoever is near enough to hear it; a bite is a hit on a tank; a kill is a small burst.
        StepEventKind.NestWoke => new(SoundEffect.WarnOverheat, AudioTweaks.NestWakeGain, CueHeard.Anywhere),
        StepEventKind.EmberiteBit => new(SoundEffect.TankHit, AudioTweaks.EmberiteBiteGain, CueHeard.TrackedTank, CueVoice.Burst),
        StepEventKind.EmberiteKilled => new(SoundEffect.Impact, AudioTweaks.EmberiteKilledGain, CueHeard.Anywhere, CueVoice.Burst),
        _ => null,
    };

    /// <summary>
    /// Whether <paramref name="cue"/> is heard for an event of a tank that is <paramref name="tracked"/> or not, and
    /// is the player's own (<paramref name="isPlayer"/>) or not.
    /// </summary>
    public static bool Hears(in StepEventCue cue, bool tracked, bool isPlayer) => cue.Heard switch
    {
        CueHeard.Anywhere => true,
        CueHeard.TrackedTank => tracked,
        _ => tracked && isPlayer,
    };

    /// <summary>The sound of building a machine of <paramref name="type"/>; each machine has its own.</summary>
    public static SoundEffect BuildSound(MachineType type) =>
        (uint)type < (uint)BuildSounds.Length ? BuildSounds[(int)type] : throw new ArgumentOutOfRangeException(nameof(type));

    private static readonly SoundEffect[] BuildSounds = BuildSoundTable(
        (MachineType.Relay, SoundEffect.BuildRelay),
        (MachineType.Harvester, SoundEffect.BuildHarvester),
        (MachineType.Generator, SoundEffect.BuildGenerator),
        (MachineType.Turret, SoundEffect.BuildTurret));

    private static SoundEffect[] BuildSoundTable(params (MachineType Type, SoundEffect Sound)[] entries)
    {
        var table = new SoundEffect?[Enum.GetValues<MachineType>().Length];
        foreach (var (type, sound) in entries)
            table[(int)type] = sound;
        if (Array.IndexOf(table, null) >= 0)
            throw new InvalidOperationException("Every MachineType needs a build sound.");
        return Array.ConvertAll(table, s => s!.Value);
    }
}
