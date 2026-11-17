namespace Tunnerer.Desktop.Audio;

using Tunnerer.Core;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Presentation;

/// <summary>
/// Which one-shot effect a simulation event (<see cref="World.StepEvents"/>) plays, and how loud. Events that make
/// no one-shot of their own (moving, digging, base service and spent shrapnel, which drive loops and the debris
/// rattle in <see cref="GameAudio"/>) and events with no sound yet answer false.
/// </summary>
public static class StepEventSounds
{
    /// <param name="playerColor">The player's tank: some events are only heard for the player's own tank.</param>
    public static bool TryGetCue(in StepEvent e, int playerColor, out SoundEffect effect, out float gain)
    {
        bool isPlayer = e.Color == playerColor;
        (effect, gain) = e.Kind switch
        {
            StepEventKind.ShotFired => (SoundEffect.Fire, AudioTweaks.FireGain),
            StepEventKind.BulletImpact => (SoundEffect.Impact, AudioTweaks.ImpactGain),
            StepEventKind.TankHit => (SoundEffect.TankHit, AudioTweaks.TankHitGain),
            StepEventKind.TankDied => (SoundEffect.TankDeath, AudioTweaks.TankDeathGain),
            StepEventKind.TankRespawned => (SoundEffect.Respawn, AudioTweaks.RespawnGain),
            StepEventKind.MachineBuilt => (MachinePresentations.Get(e.Machine).BuildSound, AudioTweaks.BuildGain),
            StepEventKind.MachinePlanted => (SoundEffect.Plant, AudioTweaks.PlantGain),
            StepEventKind.PlantRefused or StepEventKind.BuildRefused when isPlayer =>
                (SoundEffect.PlantRefused, AudioTweaks.RefusedGain),
            StepEventKind.OreUnloaded when isPlayer => (SoundEffect.Pickup, AudioTweaks.UnloadGain),
            StepEventKind.Docked when isPlayer => (SoundEffect.Dock, AudioTweaks.DockGain),
            StepEventKind.GoldCollected or StepEventKind.OreCollected => (SoundEffect.Pickup, AudioTweaks.PickupGain),
            _ => (default(SoundEffect), 0f),
        };
        return gain > 0f;
    }
}
