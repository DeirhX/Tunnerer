using Emberward.Core.Entities.Machines;
using Emberward.Core.Rendering;
using Emberward.Core.Types;
using Emberward.Desktop.Rendering.Entities;

namespace Emberward.Tests;

/// <summary>How the entity layer places tanks and machines between two simulation steps.</summary>
public class EntityInterpolatorTests
{
    // Tank directions (see Tank.OffsetToDirection): 3x3 grid, east on the left.
    private const int East = 3, NorthWest = 2, SouthWest = 8, NoDirection = 4;

    private static TankRenderState Tank(int x, int y, bool dead = false, int direction = East) =>
        new(new Position(x, y), Color: 0, Direction: direction, IsDead: dead, TurretDirection: default);

    private static EntityInterpolator Stepped(TankRenderState first, TankRenderState second)
    {
        var interpolator = new EntityInterpolator();
        interpolator.CaptureTanks([first]);
        interpolator.CaptureTanks([second]);
        return interpolator;
    }

    [Theory]
    [InlineData(0f, 10f, 20f)]
    [InlineData(0.5f, 11f, 21f)]
    [InlineData(1f, 12f, 22f)]
    public void TankPosition_IsInterpolatedBetweenTheLastTwoSteps(float alpha, float x, float y)
    {
        var interpolator = Stepped(Tank(10, 20), Tank(12, 22));

        var position = Assert.Single(interpolator.InterpolateTankPositions(alpha).ToArray());

        Assert.Equal(x, position.X);
        Assert.Equal(y, position.Y);
    }

    [Fact]
    public void TreadTravel_GrowsWithTheDistanceDriven()
    {
        var interpolator = Stepped(Tank(10, 20), Tank(13, 20));

        Assert.Equal(0f, interpolator.TreadTravel(0, 0f));
        Assert.Equal(1.5f, interpolator.TreadTravel(0, 0.5f));
        Assert.Equal(3f, interpolator.TreadTravel(0, 1f));
    }

    [Fact]
    public void FirstCapture_HoldsTheTankStill()
    {
        var interpolator = new EntityInterpolator();
        interpolator.CaptureTanks([Tank(10, 20)]);

        Assert.Equal(new PositionF(10, 20), interpolator.InterpolateTankPositions(0f)[0]);
    }

    [Fact]
    public void Teleport_IsNotSlidAcross()
    {
        // Further than TeleportDistanceCells in one step: a respawn, drawn at the new place from the start.
        var interpolator = Stepped(Tank(10, 20), Tank(10 + (int)EntityInterpolator.TeleportDistanceCells + 1, 20));

        Assert.Equal(new PositionF(14, 20), interpolator.InterpolateTankPositions(0f)[0]);
        Assert.Equal(0f, interpolator.TreadTravel(0, 1f));
    }

    [Fact]
    public void DyingOrRespawning_IsATeleport()
    {
        var interpolator = Stepped(Tank(10, 20, dead: true), Tank(11, 20));

        Assert.Equal(new PositionF(11, 20), interpolator.InterpolateTankPositions(0f)[0]);
    }

    [Fact]
    public void ChangedTankCount_StartsAfresh()
    {
        var interpolator = new EntityInterpolator();
        interpolator.CaptureTanks([Tank(10, 20)]);
        interpolator.CaptureTanks([Tank(11, 20), Tank(30, 30)]);

        var positions = interpolator.InterpolateTankPositions(0f);
        Assert.Equal(new PositionF(11, 20), positions[0]);
        Assert.Equal(new PositionF(30, 30), positions[1]);
    }

    [Fact]
    public void HullHeading_SnapsFirst_ThenTurnsAtItsRate()
    {
        var interpolator = Stepped(Tank(10, 20), Tank(10, 20));

        Assert.Equal(0f, interpolator.UpdateHullHeading(0, East, turnSeconds: 0.01f, snap: false));
        float turned = interpolator.UpdateHullHeading(0, SouthWest, turnSeconds: 0.01f, snap: false);
        Assert.Equal(EntityInterpolator.HullTurnRate * 0.01f, turned, precision: 5);
        // No direction: hold the heading.
        Assert.Equal(turned, interpolator.UpdateHullHeading(0, NoDirection, turnSeconds: 1f, snap: false));
    }

    [Fact]
    public void HullHeading_TurnsTheShortWayAcrossTheWrap()
    {
        var interpolator = Stepped(Tank(10, 20), Tank(10, 20));
        float southWest = interpolator.UpdateHullHeading(0, SouthWest, turnSeconds: 0f, snap: true);
        Assert.Equal(0.75f * MathF.PI, southWest, precision: 5);

        // North-west is at -3/4 pi: a quarter turn on through pi, not three quarters back through zero.
        float step = interpolator.UpdateHullHeading(0, NorthWest, turnSeconds: 0.01f, snap: false);
        Assert.True(step > southWest, $"should turn up through pi, got {step}");

        float settled = interpolator.UpdateHullHeading(0, NorthWest, turnSeconds: 1f, snap: false);
        Assert.Equal(MathF.Cos(-0.75f * MathF.PI), MathF.Cos(settled), precision: 5);
        Assert.Equal(MathF.Sin(-0.75f * MathF.PI), MathF.Sin(settled), precision: 5);
    }

    private static MachineRenderState Machine(int x, int y, MachineType type = MachineType.Harvester) =>
        new(new Position(x, y), type, MachineState.Planted, IsAlive: true, IsPowered: false);

    [Fact]
    public void Machine_SlidesOneCell_ButNotAJump()
    {
        var interpolator = new EntityInterpolator();
        interpolator.CaptureMachines([Machine(10, 10), Machine(40, 40)]);
        interpolator.CaptureMachines([Machine(11, 10), Machine(50, 40)]);

        Assert.Equal(new PositionF(10.5f, 10), interpolator.MachinePosition(0, 0.5f));
        Assert.Equal(new PositionF(50, 40), interpolator.MachinePosition(1, 0.5f));
    }

    [Fact]
    public void MachineHeading_TurnsTheShortWayAcrossTheWrap()
    {
        var interpolator = new EntityInterpolator();
        interpolator.CaptureMachines([Machine(10, 10)]);
        float west = interpolator.UpdateMachineHeading(0, new Offset(-1, 0), turnSeconds: 0f, snap: true);
        Assert.Equal(MathF.PI, west, precision: 5);

        // Straight up (-pi/2) from west (pi) is a quarter turn on, through 3/2 pi.
        float step = interpolator.UpdateMachineHeading(0, new Offset(0, -1), turnSeconds: 0.01f, snap: false);
        Assert.Equal(MathF.PI + EntityInterpolator.HarvesterTurnRate * 0.01f, step, precision: 5);
    }
}
