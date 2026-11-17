namespace Tunnerer.Desktop.Audio;

using Tunnerer.Core;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Presentation;

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
/// with no sound yet have no cue.
/// </summary>
public static class StepEventSounds
{
    public static StepEventCue? CueFor(StepEventKind kind, MachineType machine) => kind switch
    {
        StepEventKind.ShotFired => new(SoundEffect.Fire, AudioTweaks.FireGain, CueHeard.Anywhere, CueVoice.ShotCycle),
        StepEventKind.BulletImpact => new(SoundEffect.Impact, AudioTweaks.ImpactGain, CueHeard.Anywhere, CueVoice.Burst),
        StepEventKind.OreCollected => new(SoundEffect.Pickup, AudioTweaks.PickupGain, CueHeard.Anywhere, CueVoice.PickupStreak),
        StepEventKind.TankHit => new(SoundEffect.TankHit, AudioTweaks.TankHitGain, CueHeard.TrackedTank),
        StepEventKind.TankDied => new(SoundEffect.TankDeath, AudioTweaks.TankDeathGain, CueHeard.TrackedTank),
        StepEventKind.TankRespawned => new(SoundEffect.Respawn, AudioTweaks.RespawnGain, CueHeard.TrackedTank),
        StepEventKind.MachineBuilt => new(MachinePresentations.Get(machine).BuildSound, AudioTweaks.BuildGain, CueHeard.TrackedTank),
        StepEventKind.MachinePlanted => new(SoundEffect.Plant, AudioTweaks.PlantGain, CueHeard.TrackedTank),
        StepEventKind.PlantRefused or StepEventKind.BuildRefused =>
            new(SoundEffect.PlantRefused, AudioTweaks.RefusedGain, CueHeard.Player),
        StepEventKind.OreUnloaded => new(SoundEffect.Pickup, AudioTweaks.UnloadGain, CueHeard.Player),
        StepEventKind.Docked => new(SoundEffect.Dock, AudioTweaks.DockGain, CueHeard.Player),
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
}
