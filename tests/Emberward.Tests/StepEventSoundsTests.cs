using Emberward.Core;
using Emberward.Core.Entities.Machines;
using Emberward.Desktop.Audio;

namespace Emberward.Tests;

/// <summary>Which sound each simulation event plays (<see cref="StepEventSounds"/>), checked without a sound device.</summary>
public class StepEventSoundsTests
{
    private static SoundEffect? Cue(StepEventKind kind, MachineType machine = default) =>
        StepEventSounds.CueFor(kind, machine) is { } cue
            ? (cue.Gain > 0f ? cue.Effect : throw new InvalidOperationException("a cue must be audible"))
            : null;

    private static bool Hears(StepEventKind kind, bool tracked, bool isPlayer) =>
        StepEventSounds.Hears(StepEventSounds.CueFor(kind, default)!.Value, tracked, isPlayer);

    [Theory]
    [InlineData(StepEventKind.ShotFired, SoundEffect.Fire)]
    [InlineData(StepEventKind.BulletImpact, SoundEffect.Impact)]
    [InlineData(StepEventKind.TankHit, SoundEffect.TankHit)]
    [InlineData(StepEventKind.TankDied, SoundEffect.TankDeath)]
    [InlineData(StepEventKind.TankRespawned, SoundEffect.Respawn)]
    [InlineData(StepEventKind.MachinePlanted, SoundEffect.Plant)]
    [InlineData(StepEventKind.PlantRefused, SoundEffect.PlantRefused)]
    [InlineData(StepEventKind.Docked, SoundEffect.Dock)]
    [InlineData(StepEventKind.NestStirred, SoundEffect.WarnEnergy)]
    [InlineData(StepEventKind.OreCollected, SoundEffect.Pickup)]
    public void PlayerEvents_PlayTheirEffect(StepEventKind kind, SoundEffect expected) =>
        Assert.Equal(expected, Cue(kind));

    [Fact]
    public void BuildingPlaysTheMachinesOwnSound()
    {
        foreach (var type in Enum.GetValues<MachineType>())
            Assert.Equal(StepEventSounds.BuildSound(type), Cue(StepEventKind.MachineBuilt, machine: type));
    }

    [Theory]
    [InlineData(StepEventKind.PlantRefused)]
    [InlineData(StepEventKind.BuildRefused)]
    [InlineData(StepEventKind.OreUnloaded)]
    [InlineData(StepEventKind.Docked)]
    public void PlayerOnlyEvents_AreSilentForTheEnemy(StepEventKind kind)
    {
        Assert.True(Hears(kind, tracked: true, isPlayer: true));
        Assert.False(Hears(kind, tracked: true, isPlayer: false));
        Assert.False(Hears(kind, tracked: false, isPlayer: true));
    }

    [Theory]
    [InlineData(StepEventKind.TankHit)]
    [InlineData(StepEventKind.TankDied)]
    [InlineData(StepEventKind.TankRespawned)]
    [InlineData(StepEventKind.MachineBuilt)]
    [InlineData(StepEventKind.MachinePlanted)]
    public void ATanksOwnEvents_AreHeardOnlyForTrackedTanks(StepEventKind kind)
    {
        Assert.True(Hears(kind, tracked: true, isPlayer: false));
        Assert.False(Hears(kind, tracked: false, isPlayer: false));
    }

    [Theory]
    [InlineData(StepEventKind.ShotFired)]
    [InlineData(StepEventKind.BulletImpact)]
    [InlineData(StepEventKind.OreCollected)]
    public void ShotsImpactsAndPickups_AreHeardForAnyone(StepEventKind kind) =>
        Assert.True(Hears(kind, tracked: false, isPlayer: false));

    [Theory]
    [InlineData(StepEventKind.TankMoved)]
    [InlineData(StepEventKind.TankDug)]
    [InlineData(StepEventKind.BaseServiced)]
    [InlineData(StepEventKind.ShrapnelSpent)]
    [InlineData(StepEventKind.Undocked)]
    public void LoopAndAggregateEvents_HaveNoOneShot(StepEventKind kind) =>
        Assert.Null(Cue(kind));

    [Fact]
    public void EveryKind_HasAnAnswer()
    {
        foreach (var kind in Enum.GetValues<StepEventKind>())
            _ = Cue(kind);
    }
}
