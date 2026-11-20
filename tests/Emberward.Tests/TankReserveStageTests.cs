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
        int normal = DigFor(world, tank, 60, Tweaks.Perf.TargetFps);
        AdvanceTo(world, tank, TankReserveStage.Degraded);
        int degraded = DigFor(world, tank, 120, Tweaks.Perf.TargetFps);

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
    public void Stranded_DigsAtTheCrawl_NotSlowerStill()
    {
        // A blocked tank digs on the ticks it would move, so the crawl already slows the dig; no dig factor on top.
        var (world, tank) = OpenWorld();
        int ticks = Tweaks.Perf.TargetFps / 2;
        int normal = DigFor(world, tank, 60, ticks);
        AdvanceTo(world, tank, TankReserveStage.Stranded);
        int stranded = DigFor(world, tank, 120, ticks);

        Assert.True(world.ReserveOf(tank).Stage >= TankReserveStage.Stranded);
        Assert.InRange(stranded, (int)(normal * Tweaks.Reserve.StrandedMoveFactor) - 1,
            (int)(normal * Tweaks.Reserve.StrandedMoveFactor) + 2);
    }

    [Fact]
    public void Degraded_AWastedDig_SpendsNoCredit()
    {
        var (world, tank) = OpenWorld();
        AdvanceTo(world, tank, TankReserveStage.Degraded);
        tank.Position = new Position(150, 60);
        Fill(world, 155, 60, TerrainPixel.Rock);

        // Without the torch the rock gives nothing, however long the tank pushes.
        int dug = 0;
        for (int i = 0; i < 20; i++)
        {
            world.Advance(_ => TestSimulation.Move(1, 0));
            dug += Count(world, StepEventKind.TankDug);
        }
        Assert.Equal(0, dug);

        // The credit those pushes built up was not spent on them: dirt in place of the rock is dug at once.
        Fill(world, 155, 60, TerrainPixel.Dirt);
        world.Advance(_ => TestSimulation.Move(1, 0));
        Assert.Equal(TankReserveStage.Degraded, world.ReserveOf(tank).Stage);
        Assert.Equal(1, Count(world, StepEventKind.TankDug));
    }

    [Fact]
    public void Stranded_StillPlantsACarriedMachine()
    {
        var (world, tank) = OpenWorld();
        var harvester = MachineBehaviors.Harvester;
        Assert.False(harvester.RequiresLinkToPlant, "a harvester plants anywhere outside a base");
        tank.Hold.Ore = Tweaks.Ore.HarvesterCost;
        world.Advance(_ => new ControllerOutput { BuildSecondary = true });
        Assert.Equal(MachineType.Harvester, tank.CarriedMachineType);

        AdvanceTo(world, tank, TankReserveStage.Stranded);
        world.Advance(_ => new ControllerOutput { BuildSecondary = true });

        Assert.Equal(TankReserveStage.Stranded, world.ReserveOf(tank).Stage);
        Assert.Equal(1, Count(world, StepEventKind.MachinePlanted));
        Assert.Null(tank.CarriedMachineType);
    }

    [Fact]
    public void Stranded_ABuildKeyHeldThroughTheStage_IsNotANewPress()
    {
        var (world, tank) = OpenWorld();
        AdvanceTo(world, tank, TankReserveStage.Stranded);
        tank.Hold.Ore = 10;
        var hold = new ControllerOutput { BuildPrimary = true };
        Advance(world, 3, hold);

        // A burn takes the tank back to degraded, where it may build; the key it has held all along builds nothing.
        world.Advance(_ => hold with { Recall = true });
        Assert.Equal(TankReserveStage.Degraded, world.ReserveOf(tank).Stage);
        Assert.Equal(0, Count(world, StepEventKind.MachineBuilt));
        Assert.Null(tank.CarriedMachineType);

        // Let go and press again: that is a press.
        world.Advance(_ => default);
        world.Advance(_ => hold);
        Assert.Equal(1, Count(world, StepEventKind.MachineBuilt));
    }

    [Fact]
    public void Death_ClearsTheReserve()
    {
        var (world, tank) = OpenWorld();
        AdvanceTo(world, tank, TankReserveStage.HeatDeath);
        Assert.Equal(0, tank.OreHeld);

        // At heat death with an empty hold the key recalls: the tank dies and is back at its base in the same tick.
        world.Advance(_ => Burn);
        Assert.Contains(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.TankRespawned);

        // The tank it respawns as has no reserve left over from the last: no stage to leave.
        int changes = 0;
        for (int i = 0; i < 3; i++)
        {
            if (i == 2)
                tank.Position = new Position(200, 150);
            world.Advance(_ => default);
            changes += Count(world, StepEventKind.ReserveStageChanged);
        }
        Assert.True(tank.IsOffNetwork);
        Assert.Equal(TankReserveStage.None, world.ReserveOf(tank).Stage);
        Assert.Equal(0, changes);
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

    // A block of ground ahead of a tank at (150, y), its front edge at x.
    private static void Fill(World world, int x, int y, TerrainPixel pixel)
    {
        for (int px = x; px < x + 75; px++)
            for (int dy = -8; dy <= 8; dy++)
                world.Terrain.SetPixel(new Position(px, y + dy), pixel);
    }

    // Parks the tank in front of a wall of dirt and drives into it for the ticks given: how far it got.
    private static int DigFor(World world, Tank tank, int y, int ticks)
    {
        tank.Position = new Position(150, y);
        Fill(world, 155, y, TerrainPixel.Dirt);
        int from = tank.Position.X;
        Advance(world, ticks, TestSimulation.Move(1, 0));
        return tank.Position.X - from;
    }
}
