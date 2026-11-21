using Emberward.Core.Entities.Machines;
using Emberward.Core.Rendering;
using Emberward.Core.Types;
using Emberward.Desktop.Rendering.Entities;

namespace Emberward.Tests;

/// <summary>How the entity layer places tanks and machines through a simulation step.</summary>
public class EntityMotionTests
{
    // Tank directions (see Tank.OffsetToDirection): 3x3 grid, east on the left.
    private const int East = 3, NorthWest = 2, SouthWest = 8, NoDirection = 4;

    private static TankRenderState Tank(int x, int y, bool dead = false, int direction = East) =>
        new(new Position(x, y), Color: 0, Direction: direction, IsDead: dead, TurretDirection: default);

    /// <summary>Captures the tank at each x in turn, drawing every step to its end before the next, like a steady frame rate.</summary>
    private static EntityMotion Driven(params int[] xs)
    {
        var motion = new EntityMotion();
        foreach (int x in xs)
        {
            motion.CaptureTanks([Tank(x, 20)]);
            motion.SetAlpha(1f);
        }
        return motion;
    }

    [Fact]
    public void SteadyMotion_IsDrawnExactlyOneStepAheadAtTheEndOfTheStep()
    {
        var motion = Driven(10, 11, 12, 13, 14);
        motion.CaptureTanks([Tank(15, 20)]);

        Assert.Equal(15f, motion.TankPositions(0f)[0].X, precision: 4);
        Assert.Equal(15.5f, motion.TankPositions(0.5f)[0].X, precision: 4);
        Assert.Equal(16f, motion.TankPositions(1f)[0].X, precision: 4);
    }

    [Fact]
    public void DrawnPosition_IsContinuousAcrossSteps()
    {
        var motion = new EntityMotion();
        float last = float.NaN;
        float largestJump = 0f;
        for (int x = 10; x < 40; x++)
        {
            motion.CaptureTanks([Tank(x, 20)]);
            foreach (float alpha in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
            {
                motion.SetAlpha(alpha);
                float drawn = motion.TankPositions(alpha)[0].X;
                // The first steps have too little history to predict from.
                if (!float.IsNaN(last) && x > 13)
                    largestJump = MathF.Max(largestJump, MathF.Abs(drawn - last));
                last = drawn;
            }
        }
        // A quarter step at one cell a step; never a jump back to a step's start.
        Assert.True(largestJump < 0.3f, $"largest jump {largestJump}");
    }

    [Fact]
    public void ADigAlternation_DrawsSteadily()
    {
        // Moving one cell, then none, then one: half a cell a step on average.
        var motion = new EntityMotion();
        int[] xs = [10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16];
        var drawn = new List<float>();
        foreach (int x in xs)
        {
            motion.CaptureTanks([Tank(x, 20)]);
            for (float alpha = 0f; alpha < 1f; alpha += 0.25f)
            {
                motion.SetAlpha(alpha);
                drawn.Add(motion.TankPositions(alpha)[0].X);
            }
        }
        float worst = 0f;
        for (int i = 12; i < drawn.Count; i++)
            worst = MathF.Max(worst, MathF.Abs(drawn[i] - drawn[i - 1]));
        Assert.True(worst < 0.4f, $"largest quarter-step move {worst}");
    }

    [Fact]
    public void AbruptStop_OvershootsByLessThanACellAndEasesBack()
    {
        var motion = Driven(10, 11, 12, 13);
        motion.CaptureTanks([Tank(14, 20)]);
        motion.SetAlpha(1f);
        float atEnd = motion.TankPositions(1f)[0].X;
        // The key was released: the tank stays on 14.
        motion.CaptureTanks([Tank(14, 20)]);
        motion.SetAlpha(1f);
        motion.CaptureTanks([Tank(14, 20)]);
        motion.SetAlpha(1f);
        motion.CaptureTanks([Tank(14, 20)]);

        Assert.True(atEnd > 14f && atEnd - 14f <= 1.01f, $"overshoot {atEnd - 14f}");
        Assert.Equal(14f, motion.TankPositions(1f)[0].X, precision: 3);
    }

    [Fact]
    public void FirstCapture_HoldsTheTankStill()
    {
        var motion = new EntityMotion();
        motion.CaptureTanks([Tank(10, 20)]);

        Assert.Equal(new PositionF(10, 20), motion.TankPositions(0f)[0]);
        Assert.Equal(new PositionF(10, 20), motion.TankPositions(1f)[0]);
    }

    [Fact]
    public void TreadTravel_GrowsWithTheDistanceDrawn()
    {
        var motion = Driven(10, 11, 12, 13, 14);
        motion.CaptureTanks([Tank(15, 20)]);

        float start = motion.TreadTravel(0, 0f);
        Assert.Equal(start + 0.5f, motion.TreadTravel(0, 0.5f), precision: 4);
        Assert.Equal(start + 1f, motion.TreadTravel(0, 1f), precision: 4);
    }

    [Fact]
    public void Teleport_IsNotSlidAcross()
    {
        // Further than TeleportDistanceCells in one step: a respawn, drawn at the new place from the start.
        var motion = Driven(10, 11, 12);
        motion.CaptureTanks([Tank(12 + (int)EntityMotion.TeleportDistanceCells + 1, 20)]);

        Assert.Equal(new PositionF(16, 20), motion.TankPositions(0f)[0]);
        Assert.Equal(new PositionF(16, 20), motion.TankPositions(1f)[0]);
        Assert.Equal(0f, motion.TreadTravel(0, 1f));
    }

    [Fact]
    public void DyingOrRespawning_IsATeleport()
    {
        var motion = new EntityMotion();
        motion.CaptureTanks([Tank(10, 20, dead: true)]);
        motion.CaptureTanks([Tank(11, 20)]);

        Assert.Equal(new PositionF(11, 20), motion.TankPositions(0f)[0]);
        Assert.Equal(new PositionF(11, 20), motion.TankPositions(1f)[0]);
    }

    [Fact]
    public void ChangedTankCount_StartsAfresh()
    {
        var motion = new EntityMotion();
        motion.CaptureTanks([Tank(10, 20)]);
        motion.CaptureTanks([Tank(11, 20), Tank(30, 30)]);

        var positions = motion.TankPositions(0f);
        Assert.Equal(new PositionF(11, 20), positions[0]);
        Assert.Equal(new PositionF(30, 30), positions[1]);
    }

    [Fact]
    public void Hold_FreezesTheTankWhereItIsDrawn()
    {
        var motion = Driven(10, 11, 12, 13, 14);
        motion.CaptureTanks([Tank(15, 20)]);
        motion.SetAlpha(0.5f);
        float before = motion.TankPositions(0.5f)[0].X;

        motion.Hold();

        Assert.Equal(before, motion.TankPositions(0f)[0].X, precision: 4);
        Assert.Equal(before, motion.TankPositions(1f)[0].X, precision: 4);
        Assert.Equal((new PositionF(0, 0)), motion.TankMotion(0).Motion);
    }

    [Fact]
    public void MotionLead_ScalesThePrediction()
    {
        var motion = new EntityMotion { Lead = 0f };
        foreach (int x in new[] { 10, 11, 12, 13, 14 })
        {
            motion.CaptureTanks([Tank(x, 20)]);
            motion.SetAlpha(1f);
        }
        motion.CaptureTanks([Tank(15, 20)]);

        // No lead: the end of the step is where the step left it (a step late), not a step on.
        Assert.Equal(15f, motion.TankPositions(1f)[0].X, precision: 3);
    }

    [Fact]
    public void Machine_SlidesOneCell_ButNotAJump()
    {
        var motion = new EntityMotion();
        motion.CaptureMachines([Machine(10, 10), Machine(40, 40)]);
        motion.CaptureMachines([Machine(11, 10), Machine(50, 40)]);

        var (start, drift) = motion.MachineMotion(0);
        Assert.Equal(new PositionF(10, 10), start);
        Assert.Equal(new PositionF(1, 0), drift);
        var (jumpStart, jumpDrift) = motion.MachineMotion(1);
        Assert.Equal(new PositionF(50, 40), jumpStart);
        Assert.Equal(default, jumpDrift);
    }

    [Fact]
    public void HullHeading_SnapsFirst_ThenTurnsAtItsRate()
    {
        var interpolator = Driven(10, 10);

        Assert.Equal(0f, interpolator.UpdateHullHeading(0, East, turnSeconds: 0.01f, snap: false));
        float turned = interpolator.UpdateHullHeading(0, SouthWest, turnSeconds: 0.01f, snap: false);
        Assert.Equal(EntityMotion.HullTurnRate * 0.01f, turned, precision: 5);
        // No direction: hold the heading.
        Assert.Equal(turned, interpolator.UpdateHullHeading(0, NoDirection, turnSeconds: 1f, snap: false));
    }

    [Fact]
    public void HullHeading_TurnsTheShortWayAcrossTheWrap()
    {
        var interpolator = Driven(10, 10);
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
    public void MachineHeading_TurnsTheShortWayAcrossTheWrap()
    {
        var interpolator = new EntityMotion();
        interpolator.CaptureMachines([Machine(10, 10)]);
        float west = interpolator.UpdateMachineHeading(0, new Offset(-1, 0), turnSeconds: 0f, snap: true);
        Assert.Equal(MathF.PI, west, precision: 5);

        // Straight up (-pi/2) from west (pi) is a quarter turn on, through 3/2 pi.
        float step = interpolator.UpdateMachineHeading(0, new Offset(0, -1), turnSeconds: 0.01f, snap: false);
        Assert.Equal(MathF.PI + EntityMotion.HarvesterTurnRate * 0.01f, step, precision: 5);
    }
}
