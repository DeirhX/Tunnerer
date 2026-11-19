namespace Emberward.Desktop.Audio;

/// <summary>Every one-shot and loop the game plays. The loops come last (SoundBank.IsLoop).</summary>
public enum SoundEffect
{
    Fire,
    Impact,
    Debris,
    TankHit,
    TankDeath,
    Respawn,
    Dock,
    BuildRelay,
    BuildHarvester,
    BuildPylon,
    Plant,
    PlantRefused,
    Pickup,
    WarnOverheat,
    WarnShield,
    WarnEnergy,
    EngineLoop,
    DigLoop,
    TorchLoop,
    RechargeLoop,
}
