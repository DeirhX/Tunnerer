using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Input;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using static Emberward.Tests.TankReserveTests;

namespace Emberward.Tests;

/// <summary>
/// Running out of the tank's reserve (DESIGN.md §4.9, <see cref="TankReserveSystem"/>): warning, degraded, stranded,
/// then heat death, each with its counter and effects, and a generator that holds the clock.
/// </summary>
public class TankReserveStageTests
{
    [Fact]
    public void TheStages_FollowTheClock_EachWithItsCounter()
    {
        var (world, tank) = OpenWorld();
        world.Advance(_ => default);
        var start = world.ReserveOf(tank);
        Assert.True(start.IsActive, "far from the base the tank is off its network");
        Assert.Equal(TankReserveStage.None, start.Stage);

        // One tick moves the clock this many seconds in the open world.
        float tick = (float)Speedup / Tweaks.Perf.TargetFps;
        (TankReserveStage Stage, float Seconds)[] stages =
        [
            (TankReserveStage.Warning, Tweaks.Reserve.WarningSeconds),
            (TankReserveStage.Degraded, Tweaks.Reserve.DegradedSeconds),
            (TankReserveStage.Stranded, Tweaks.Reserve.StrandedSeconds),
            (TankReserveStage.HeatDeath, 0f),
        ];
        foreach (var (stage, seconds) in stages)
        {
            AdvanceTo(world, tank, stage);
            var view = world.ReserveOf(tank);
            Assert.InRange(view.SecondsLeft, seconds - tick - 0.01f, seconds);
            Assert.Equal(0f, view.SecondsInStage);
            Assert.Contains(world.StepEvents.ToArray(),
                e => e.Kind == StepEventKind.ReserveStageChanged && e.Count == (int)stage);

            // The counter: time in the stage grows, time to the next stage falls.
            Advance(world, 6);
            var later = world.ReserveOf(tank);
            Assert.Equal(stage, later.Stage);
            Assert.Equal(0.25f, later.SecondsInStage, precision: 4);
            if (stage != TankReserveStage.HeatDeath)
                Assert.True(later.SecondsToNextStage < view.SecondsToNextStage);
        }
    }

    [Fact]
    public void Degraded_SlowsTheGun_AndShortensSight()
    {
        var (world, tank) = OpenWorld();
        var vision = world.TrackVision(tank);
        var shoot = new ControllerOutput { ShootPrimary = true };
        world.Advance(_ => default);
        Assert.True(vision.IsVisible(tank.Position + new Offset(40, 0)), "a fresh tank sees 45 cells");

        int normalShots = 0;
        for (int i = 0; i < Tweaks.Perf.TargetFps; i++)
        {
            world.Advance(_ => shoot);
            normalShots += Count(world, StepEventKind.ShotFired);
        }

        AdvanceTo(world, tank, TankReserveStage.Degraded);
        int degradedShots = 0;
        for (int i = 0; i < Tweaks.Perf.TargetFps; i++)
        {
            world.Advance(_ => shoot);
            degradedShots += Count(world, StepEventKind.ShotFired);
        }

        Assert.Equal(TankReserveStage.Degraded, world.ReserveOf(tank).Stage);
        Assert.InRange(degradedShots, normalShots / Tweaks.Reserve.DegradedFireDelayFactor - 1,
            normalShots / Tweaks.Reserve.DegradedFireDelayFactor + 1);
        Assert.False(vision.IsVisible(tank.Position + new Offset(40, 0)), "degraded, the tank sees less far");
        Assert.True(vision.IsVisible(tank.Position + new Offset(15, 0)));
    }

    [Fact]
    public void Degraded_SlowsTheDig()
    {
        var (world, tank) = OpenWorld();
        int Dig(int y)
        {
            // A wall of dirt in front of the tank; drive into it for a second.
            tank.Position = new Position(150, y);
            for (int x = 155; x < 230; x++)
                for (int dy = -8; dy <= 8; dy++)
                    world.Terrain.SetPixel(new Position(x, y + dy), TerrainPixel.Dirt);
            int from = tank.Position.X;
            Advance(world, Tweaks.Perf.TargetFps, TestSimulation.Move(1, 0));
            return tank.Position.X - from;
        }

        int normal = Dig(60);
        AdvanceTo(world, tank, TankReserveStage.Degraded);
        int degraded = Dig(120);

        Assert.Equal(TankReserveStage.Degraded, world.ReserveOf(tank).Stage);
        Assert.True(degraded < normal, $"degraded the dig is slower. normal={normal} degraded={degraded}");
    }

    [Fact]
    public void Stranded_CrawlsAndOnlyDigs()
    {
        var (world, tank) = OpenWorld();
        AdvanceTo(world, tank, TankReserveStage.Stranded);

        int from = tank.Position.X;
        Advance(world, Tweaks.Perf.TargetFps, TestSimulation.Move(1, 0));
        int crawled = tank.Position.X - from;
        int expected = (int)(Tweaks.Perf.TargetFps * Tweaks.Reserve.StrandedMoveFactor);
        Assert.InRange(crawled, expected - 1, expected + 1);

        tank.Hold.Ore = 10;
        int shots = 0, built = 0;
        for (int i = 0; i < 12; i++)
        {
            world.Advance(_ => new ControllerOutput { ShootPrimary = true, BuildPrimary = i % 2 == 0 });
            shots += Count(world, StepEventKind.ShotFired);
            built += Count(world, StepEventKind.MachineBuilt);
        }
        Assert.True(world.ReserveOf(tank).Stage >= TankReserveStage.Stranded);
        Assert.Equal(0, shots);
        Assert.Equal(0, built);
        Assert.Null(tank.CarriedMachineType);
    }

    [Fact]
    public void AGenerator_HoldsTheClock()
    {
        var (world, tank) = OpenWorld(energyNetwork: true);
        Advance(world, Tweaks.Perf.TargetFps * 2);
        var generator = new Machine(tank.Position + new Offset(0, 10), MachineType.Generator, tank.Color);
        world.Machines.Add(generator);
        world.PlantMachine(generator, tank.Base);
        world.Advance(_ => default);
        float floor = tank.OffNetworkFloor;

        Advance(world, Tweaks.Perf.TargetFps * 2);

        Assert.True(tank.IsOffNetwork, "a generator holds the clock but does not cool the tank");
        Assert.Equal(floor, tank.OffNetworkFloor, precision: 4);
        Assert.True(world.ReserveOf(tank).ClockHeld);

        tank.Position += new Offset(Tweaks.Reserve.GeneratorHoldRadius + 5, 0);
        Advance(world, Tweaks.Perf.TargetFps);
        Assert.True(tank.OffNetworkFloor > floor + 0.9f, "out of the generator's reach the clock runs again");
        Assert.False(world.ReserveOf(tank).ClockHeld);
    }
}
