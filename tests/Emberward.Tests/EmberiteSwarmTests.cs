using Emberward.Core;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Emberites;
using Emberward.Core.Entities.Projectiles;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Config;
using Emberward.Core.Input;
using Emberward.Core.Rendering;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

namespace Emberward.Tests;

/// <summary>
/// The emberites (DESIGN.md §6.2, <see cref="EmberiteSwarm"/>): a nest wakes to a hot tank and not a cool one, to digging
/// whatever the heat; they run fast in tunnels and burrow slowly, prefer tunnels, bite heat into a tank without breaking
/// the energy balance, die to one bullet or fragment, guard their ore from harvesters until the nest is dead, and cost
/// nothing while none is out.
/// </summary>
public class EmberiteSwarmTests
{
    // Clear of both bases of the 320 x 200 test map, which stand at (130, 21) and (185, 178).
    private static readonly Position Centre = new(60, 100);

    /// <summary>A world with the swarm on and no ore, and a square of <paramref name="ground"/> around the map's centre.</summary>
    private static (World World, Tank Tank) Arena(TerrainPixel ground, int halfWidth = 60, int halfHeight = 30)
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false, emberites: true);
        foreach (var tankBase in world.TankBases.Bases)
            Assert.True(Math.Abs(tankBase.Position.X - Centre.X) > halfWidth + 20 || Math.Abs(tankBase.Position.Y - Centre.Y) > halfHeight + 20,
                "the arena must keep clear of the bases");
        for (int y = -halfHeight; y <= halfHeight; y++)
            for (int x = -halfWidth; x <= halfWidth; x++)
                world.Terrain.SetPixel(Centre + new Offset(x, y), ground);
        return (world, world.TankList.Tanks[0]);
    }

    /// <summary>Steps <paramref name="ticks"/> times with the tank held at <paramref name="heat"/> (null leaves its heat alone).</summary>
    private static List<StepEvent> Tick(World world, Tank tank, int ticks, float? heat = null, ControllerOutput input = default)
    {
        var events = new List<StepEvent>();
        for (int i = 0; i < ticks; i++)
        {
            if (heat is float h)
                tank.Heat = h;
            world.Advance(slot => slot == 0 ? input : default);
            events.AddRange(world.StepEvents.ToArray());
        }
        return events;
    }

    /// <summary>A creature as the tests look at it. (VectorF's record ToString recurses through Normalized, so a failed assertion must not print one.)</summary>
    private sealed record Seen(float X, float Y, float Speed);

    private static Seen[] Creatures(World world)
    {
        var states = new EmberiteRenderState[world.Emberites!.OutCount];
        world.CopyEmberiteRenderStates(states);
        return [.. states.Select(c => new Seen(c.Position.X, c.Position.Y, c.Velocity.Length))];
    }

    [Fact]
    public void ACoolTank_IsIgnored_AHotOneIsHunted()
    {
        var (world, tank) = Arena(TerrainPixel.Blank);
        var nest = world.Emberites!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 6);
        tank.Position = Centre;

        Tick(world, tank, 90, heat: 20f);
        Assert.False(nest.Awake);
        Assert.Equal(0, world.Emberites.OutCount);
        Assert.Equal(0L, world.Emberites.CreatureSteps);

        // A tank just under the wake heat is still not sensed.
        Tick(world, tank, 30, heat: Tweaks.Emberites.WakeHeat - 1f);
        Assert.False(nest.Awake);

        var events = Tick(world, tank, 60, heat: 80f);
        Assert.True(nest.Awake);
        Assert.Contains(events, e => e.Kind == StepEventKind.NestWoke);
        Assert.True(world.Emberites.OutCount > 0);
        Assert.Contains(events, e => e.Kind == StepEventKind.EmberiteBit && e.Color == tank.Color);
    }

    [Fact]
    public void TheSenseRange_GrowsWithHeat()
    {
        Assert.Equal(0f, EmberiteSwarm.SenseRange(Tweaks.Emberites.WakeHeat - 0.1f));
        Assert.Equal(Tweaks.Emberites.WakeRange, EmberiteSwarm.SenseRange(Tweaks.Emberites.WakeHeat));
        Assert.Equal(Tweaks.Emberites.FullRange, EmberiteSwarm.SenseRange(Tweaks.Emberites.FullHeat));
        Assert.True(EmberiteSwarm.SenseRange(70f) > EmberiteSwarm.SenseRange(50f));
    }

    [Fact]
    public void AHotTankOutOfRange_DoesNotWakeTheNest()
    {
        var (world, tank) = Arena(TerrainPixel.Blank);
        var nest = world.Emberites!.AddNest(Centre + new Offset(55, 0), radius: 4, creatures: 6);
        tank.Position = Centre + new Offset(-5, 0);

        // 60 cells away; a tank at the wake heat is sensed from 40.
        Tick(world, tank, 60, heat: Tweaks.Emberites.WakeHeat + 1f);

        Assert.False(nest.Awake);
    }

    [Fact]
    public void DiggingTheNestsOre_WakesItWhateverTheHeat()
    {
        var (world, tank) = Arena(TerrainPixel.Blank);
        for (int y = -6; y <= 6; y++)
            for (int x = 28; x <= 52; x++)
                world.Terrain.SetPixel(Centre + new Offset(x, y), TerrainPixel.Pyrite);
        var nest = world.Emberites!.AddNest(Centre + new Offset(40, 0), radius: 14, creatures: 4);
        tank.Position = Centre + new Offset(18, 0);

        // Cool, not digging: nothing happens.
        Tick(world, tank, 30, heat: 10f);
        Assert.False(nest.Awake);

        var right = new ControllerOutput { MoveSpeed = new Offset(1, 0) };
        var events = Tick(world, tank, 250, heat: 10f, input: right);

        int dug = events.FindIndex(e => e.Kind == StepEventKind.OreCollected);
        int woke = events.FindIndex(e => e.Kind == StepEventKind.NestWoke);
        Assert.True(dug >= 0, "the tank dug no ore");
        Assert.True(woke >= dug, "the nest should wake at the dig, not before it");
        Assert.Contains(events, e => e.Kind == StepEventKind.EmberiteBit);
    }

    [Fact]
    public void OpenGroundIsFasterThanTheTank_AndBurrowingIsSlower()
    {
        Assert.True(Tweaks.Emberites.OpenSpeed > 1f, "faster than the tank's cell a tick in a tunnel");
        Assert.True(Tweaks.Emberites.SoilSpeed < 0.5f, "slower than the tank's dig in dirt");

        var (open, openTank) = Arena(TerrainPixel.Blank);
        open.Emberites!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 3);
        openTank.Position = Centre;
        Tick(open, openTank, 14, heat: 80f);
        Assert.Contains(Creatures(open), c => Math.Abs(c.Speed - Tweaks.Emberites.OpenSpeed) < 0.01f);

        var (dirt, dirtTank) = Arena(TerrainPixel.Dirt);
        dirt.Emberites!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 3);
        dirtTank.Position = Centre;
        Tick(dirt, dirtTank, 30, heat: 80f);
        var burrowing = Creatures(dirt);
        Assert.NotEmpty(burrowing);
        Assert.All(burrowing, c => Assert.True(c.Speed <= Tweaks.Emberites.SoilSpeed + 0.01f, $"speed {c.Speed}"));
    }

    [Fact]
    public void ACreature_StopsAtRock()
    {
        var (world, tank) = Arena(TerrainPixel.Blank);
        world.Emberites!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 4);
        tank.Position = Centre;
        // The nest wakes only to a tank it has a line to, so let it wake first.
        Tick(world, tank, 8, heat: 80f);
        // Then a wall of rock between the nest and the tank, from the top of the map to its bottom: no way round.
        for (int y = -Centre.Y; y < world.Terrain.Height - Centre.Y; y++)
            for (int x = 10; x <= 14; x++)
                world.Terrain.SetPixel(Centre + new Offset(x, y), TerrainPixel.Rock);

        Tick(world, tank, 82, heat: 80f);

        Assert.NotEmpty(Creatures(world));
        Assert.All(Creatures(world), c => Assert.True(c.X > Centre.X + 14, $"a creature got through the rock at {c.X}, {c.Y}"));
    }

    [Fact]
    public void TheField_PrefersATunnelToAShorterLineThroughDirt()
    {
        var (world, tank) = Arena(TerrainPixel.Dirt, halfWidth: 70, halfHeight: 40);
        var goal = Centre;
        var start = Centre + new Offset(0, 30);
        // An open way round: along, up and back, much longer than the straight line but far cheaper than dirt.
        for (int x = 0; x <= 24; x++)
            for (int w = -2; w <= 2; w++)
                world.Terrain.SetPixel(start + new Offset(x, w), TerrainPixel.Blank);
        for (int y = 0; y <= 30; y++)
            for (int w = -2; w <= 2; w++)
                world.Terrain.SetPixel(Centre + new Offset(24 + w, y), TerrainPixel.Blank);
        for (int x = 0; x <= 24; x++)
            for (int w = -2; w <= 2; w++)
                world.Terrain.SetPixel(Centre + new Offset(x, w), TerrainPixel.Blank);
        world.Emberites!.AddNest(start, radius: 3, creatures: 1);
        tank.Position = goal;

        Tick(world, tank, 8, heat: 80f);

        var field = Assert.Single(world.Emberites.Fields, f => f.Kind == 0);
        Assert.True(field.TryDirection(start.X + 6, start.Y, out float dx, out float dy));
        Assert.True(dx > 0.5f, $"the field should lead along the tunnel (+x), not up through the dirt: ({dx}, {dy})");
    }

    [Fact]
    public void ABite_MovesHeatIntoTheTank_AndTheLedgerBalancesEveryTick()
    {
        var (world, tank) = Arena(TerrainPixel.Blank);
        world.Emberites!.AddNest(Centre + new Offset(30, 0), radius: 4, creatures: 8);
        tank.Position = Centre;
        tank.Heat = 60f;
        var ledger = world.Terrain.Ledger;
        double Balance() => world.SumSystemThermalEnergy() + world.SumSystemCharge() - ledger.Injected + ledger.TotalSunk;
        double start = Balance();

        int bites = 0;
        float heatBeforeBite = 0f;
        for (int i = 0; i < 120; i++)
        {
            tank.Heat = Math.Min(tank.Heat, 70f);
            float before = tank.Heat;
            world.Advance(_ => default);
            if (world.StepEvents.ToArray().Any(e => e.Kind == StepEventKind.EmberiteBit))
            {
                bites++;
                heatBeforeBite = before;
                // A creature that sees a cool tank bites it too, but a cooling tank hides a bite's gain: judge warm ones.
                if (before >= 40f)
                    Assert.True(tank.Heat > before + 0.5f, $"a bite should add heat: {before} -> {tank.Heat}");
            }
            Assert.Equal(start, Balance(), tolerance: 1.0);
        }

        Assert.True(bites > 0, "no creature bit");
        Assert.True(heatBeforeBite > 0f);
        Assert.True(Math.Abs(Tweaks.Emberites.BiteDegrees - 2f) < 1e-3f);
    }

    [Fact]
    public void ABullet_KillsACreature()
    {
        var (world, tank) = Arena(TerrainPixel.Blank);
        var nest = world.Emberites!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 1);
        tank.Position = Centre;
        Tick(world, tank, 8, heat: 80f);
        var creature = Assert.Single(Creatures(world));

        var bullet = Projectile.CreateBullet(new Position(0, 0), new VectorF(1f, 0f), ownerColor: tank.Color);
        bullet.Position = new PositionF(creature.X - 1f, creature.Y);
        world.Projectiles.Add(bullet);
        world.Advance(_ => default);

        Assert.Contains(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.EmberiteKilled);
        Assert.Equal(0, nest.Out);
        Assert.True(nest.IsCleared);
    }

    [Fact]
    public void AShrapnelFragment_KillsACreature()
    {
        var (world, tank) = Arena(TerrainPixel.Blank);
        var nest = world.Emberites!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 1);
        tank.Position = Centre;
        Tick(world, tank, 8, heat: 80f);
        var creature = Assert.Single(Creatures(world));

        var fragment = Projectile.CreateShrapnel(new Position(0, 0), new VectorF(1f, 0f), life: 10);
        fragment.Position = new PositionF(creature.X - 1f, creature.Y);
        world.Projectiles.Add(fragment);
        world.Advance(_ => default);

        Assert.Contains(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.EmberiteKilled);
        Assert.True(nest.IsCleared);
    }

    [Fact]
    public void AKilledCreature_SpillsItsHeatWhereItDies()
    {
        var (world, tank) = Arena(TerrainPixel.Blank);
        world.Emberites!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 1);
        tank.Position = Centre;
        Tick(world, tank, 8, heat: 80f);
        var creature = Assert.Single(Creatures(world));
        double carried = world.Emberites.TotalCharge();
        Assert.True(carried > 0.0);
        double ground = world.Terrain.SumTotalThermalEnergy();

        var bullet = Projectile.CreateBullet(new Position(0, 0), new VectorF(1f, 0f), ownerColor: tank.Color);
        bullet.Position = new PositionF(creature.X - 1f, creature.Y);
        world.Projectiles.Add(bullet);
        world.Advance(_ => default);

        Assert.Equal(0.0, world.Emberites.TotalCharge(), tolerance: 1e-6);
        Assert.True(world.Terrain.SumTotalThermalEnergy() > ground, "the heat should have gone into the ground");
    }

    [Fact]
    public void ANestWithCreaturesLeft_GuardsItsOre_AndAClearedOneFreesIt()
    {
        var s = RelayHeatTests.CreateScenario(emberites: true);
        HarvesterHeatTests.SeedOre(s);
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * -15));
        var harvester = new Machine(s.RelayPos, MachineType.Harvester, s.Tank.Color);
        s.World.Machines.Add(harvester);
        s.World.PlantMachine(harvester, s.Base);
        var nest = s.World.Emberites!.AddNest(s.RelayPos + new Offset(5, 0), radius: 12, creatures: 3);
        int store = s.Base.Store.Ore;

        for (int i = 0; i < 160; i++)
            s.World.Advance(_ => default);
        Assert.Equal(HarvesterState.Guarded, MachineBehaviors.HarvesterStateOf(harvester));
        Assert.True(s.World.IsOreGuarded(s.RelayPos + new Offset(5, 0)));
        Assert.Equal(store, s.Base.Store.Ore);

        nest.NeverReleased = 0;
        for (int i = 0; i < 400; i++)
            s.World.Advance(_ => default);
        Assert.False(s.World.IsOreGuarded(s.RelayPos + new Offset(5, 0)));
        Assert.NotEqual(HarvesterState.Guarded, MachineBehaviors.HarvesterStateOf(harvester));
        Assert.True(s.Base.Store.Ore > store, "the cleared nest's ore should have been worked");
    }

    [Fact]
    public void WithNothingOut_ThereIsNoPerCreatureWork()
    {
        var (world, tank) = Arena(TerrainPixel.Blank);
        world.Emberites!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 20);
        world.Emberites.AddNest(Centre + new Offset(-40, 0), radius: 4, creatures: 20);
        tank.Position = Centre;

        Tick(world, tank, 240, heat: 20f);

        Assert.Equal(0L, world.Emberites.CreatureSteps);
        Assert.Equal(0, world.Emberites.OutCount);
        Assert.Empty(world.Emberites.Fields);
        Assert.False(world.CollisionSolver.CanHitEmberites);
    }

    [Fact]
    public void ACoolTank_LosesTheScent_AndTheCreaturesGoHome()
    {
        var (world, tank) = Arena(TerrainPixel.Blank);
        var nest = world.Emberites!.AddNest(Centre + new Offset(50, 0), radius: 4, creatures: 3);
        tank.Position = Centre + new Offset(-30, 0);
        Tick(world, tank, 30, heat: 80f);
        Assert.True(nest.Out > 0);

        // The tank, still far from the nest, cools below the scent: they come home and the nest sleeps.
        Tick(world, tank, 160, heat: 5f);

        Assert.Equal(0, nest.Out);
        Assert.Equal(3 - nest.NeverReleased, nest.Resting);
        Assert.False(nest.Awake);
    }

    [Fact]
    public void ANestInTheLiveRules_HoldsTheSeededDeposits()
    {
        var world = TestHelpers.CreateSeededWorld(size: new Size(960, 600), seedOre: true, emberites: true, enableTerrainRegrowth: false);

        Assert.NotEmpty(world.OreDeposits);
        Assert.NotEmpty(world.Emberites!.Nests);
        foreach (var nest in world.Emberites.Nests)
        {
            Assert.True(nest.Inside >= Tweaks.Emberites.PocketMinCreatures);
            foreach (var tankBase in world.TankBases.Bases)
                Assert.True(Position.DistanceSquared(nest.Centre, tankBase.Position) >=
                    Tweaks.Emberites.MinNestDistanceFromBase * Tweaks.Emberites.MinNestDistanceFromBase);
        }
    }

    /// <summary>An arena with a nest and a hot tank, played for <paramref name="ticks"/>: creatures out, hunting, biting, going home.</summary>
    private static World HuntWorld(int ticks)
    {
        var (world, tank) = Arena(TerrainPixel.Blank);
        world.Emberites!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 12);
        tank.Position = Centre;
        Tick(world, tank, ticks, heat: 75f);
        return world;
    }

    [Fact]
    public void WithTheSwarmOn_TwoRunsAgreeOnEveryTick()
    {
        var first = HuntWorld(0);
        var second = HuntWorld(0);
        bool sawCreatures = false;
        for (int i = 0; i < 200; i++)
        {
            first.TankList.Tanks[0].Heat = second.TankList.Tanks[0].Heat = 75f;
            first.Advance(_ => default);
            second.Advance(_ => default);
            sawCreatures |= first.Emberites!.OutCount > 0;
            Assert.True(WorldStateHash.ComputeSync(first).Combined == WorldStateHash.ComputeSync(second).Combined,
                $"the runs differ at tick {i + 1}");
        }
        Assert.True(sawCreatures);
    }

    [Fact]
    public void AGuardedHarvester_ReportsTheNest_EvenWithACoreLeftInReach()
    {
        var s = RelayHeatTests.CreateScenario(emberites: true);
        HarvesterHeatTests.SeedOre(s);
        HarvesterHeatTests.SeedOre(s, along: 2, depth: 4, length: 2, core: true);
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * -15));
        var harvester = new Machine(s.RelayPos, MachineType.Harvester, s.Tank.Color);
        s.World.Machines.Add(harvester);
        s.World.PlantMachine(harvester, s.Base);
        s.World.Emberites!.AddNest(s.RelayPos + new Offset(5, 0), radius: 12, creatures: 3);

        for (int i = 0; i < 160; i++)
            s.World.Advance(_ => default);
        Assert.Equal(HarvesterState.Guarded, MachineBehaviors.HarvesterStateOf(harvester));
    }

    [Fact]
    public void APocketWithNothingStored_LetsNoEmptyCreatureOut()
    {
        var (world, tank) = Arena(TerrainPixel.Blank);
        var nest = world.Emberites!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 0);
        nest.Resting = 1; // a pocket that cannot recharge: its resting creature has no charge to come out with
        tank.Position = Centre; // 40 cells off: hot enough to hunt, and far enough that the nest's ground stays cold
        nest.Awake = true; // a nest that woke on its last charge and still hunts the tank
        nest.Target = 0;

        int mostOut = 0;
        for (int i = 0; i < 60; i++)
        {
            nest.Stored = 0.0; // nothing stored for the resting creature to take
            Tick(world, tank, 1, heat: 80f);
            mostOut = Math.Max(mostOut, world.Emberites!.OutCount);
        }
        Assert.Equal(0, mostOut);
        Assert.Equal(1, nest.Resting);
        Assert.True(nest.Awake, "a nest that still hunts a warm tank stays awake, even with nothing to let out");
    }

    [Fact]
    public void WithCreaturesOut_ASnapshotRoundTripsAndPlaysOnAlike()
    {
        var world = HuntWorld(45);
        Assert.True(world.Emberites!.OutCount > 0, "the snapshot should be taken with creatures out");
        Assert.NotEmpty(world.Emberites.Fields);

        SnapshotRoundTrip.AssertPlaysOnAlike(world, [null, null],
            () => TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false, emberites: true), ticks: 80,
            (_, _) => default, firstFrame: 45, before: (w, _) => w.TankList.Tanks[0].Heat = 75f);
    }

    [Fact]
    public void FlagOff_TheWorldHasNoSwarm_AndGuardsNothing()
    {
        var world = TestHelpers.CreateSeededWorld(seedOre: true);

        Assert.Null(world.Emberites);
        Assert.False(world.IsOreGuarded(Centre));
    }
}
