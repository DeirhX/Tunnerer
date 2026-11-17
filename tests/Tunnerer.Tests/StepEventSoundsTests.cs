using Tunnerer.Core;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Audio;
using Tunnerer.Desktop.Presentation;

namespace Tunnerer.Tests;

/// <summary>Which sound each simulation event plays (<see cref="StepEventSounds"/>), checked without a sound device.</summary>
public class StepEventSoundsTests
{
    private const int Player = 0, Enemy = 1;

    private static SoundEffect? Cue(StepEventKind kind, int color = Player, MachineType machine = default) =>
        StepEventSounds.TryGetCue(new StepEvent(kind, new Position(5, 5), color, machine), Player, out var effect, out float gain)
            ? (gain > 0f ? effect : throw new InvalidOperationException("a cue must be audible"))
            : null;

    [Theory]
    [InlineData(StepEventKind.ShotFired, SoundEffect.Fire)]
    [InlineData(StepEventKind.BulletImpact, SoundEffect.Impact)]
    [InlineData(StepEventKind.TankHit, SoundEffect.TankHit)]
    [InlineData(StepEventKind.TankDied, SoundEffect.TankDeath)]
    [InlineData(StepEventKind.TankRespawned, SoundEffect.Respawn)]
    [InlineData(StepEventKind.MachinePlanted, SoundEffect.Plant)]
    [InlineData(StepEventKind.PlantRefused, SoundEffect.PlantRefused)]
    [InlineData(StepEventKind.Docked, SoundEffect.Dock)]
    [InlineData(StepEventKind.OreCollected, SoundEffect.Pickup)]
    public void PlayerEvents_PlayTheirEffect(StepEventKind kind, SoundEffect expected) =>
        Assert.Equal(expected, Cue(kind));

    [Fact]
    public void BuildingPlaysTheMachinesOwnSound()
    {
        foreach (var type in Enum.GetValues<MachineType>())
            Assert.Equal(MachinePresentations.Get(type).BuildSound, Cue(StepEventKind.MachineBuilt, machine: type));
    }

    [Theory]
    [InlineData(StepEventKind.PlantRefused)]
    [InlineData(StepEventKind.Docked)]
    public void PlayerOnlyEvents_AreSilentForTheEnemy(StepEventKind kind) =>
        Assert.Null(Cue(kind, Enemy));

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
