using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities;
using Emberward.Core.Entities.Emberites;
using Emberward.Core.Entities.Projectiles;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Rendering;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

namespace Emberward.Tests;

/// <summary>
/// What an emberite's bite does besides heat (<see cref="EmberiteSwarm"/>): it hurts by
/// <see cref="Tweaks.Emberites.BiteDamage"/>, it takes a unit of pyrite from the tank's hold, the creature carries it home
/// to its nest's store or spills it where it dies, and no unit is ever made or lost.
/// </summary>
public class EmberiteBiteTests
{
    private static readonly Position Centre = new(60, 100);
    private const int HalfWidth = 60, HalfHeight = 30;

    private static (World World, Tank Tank, Nest Nest) Arena(int creatures, int hold)
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false, emberites: true);
        for (int y = -HalfHeight; y <= HalfHeight; y++)
            for (int x = -HalfWidth; x <= HalfWidth; x++)
                world.Terrain.SetPixel(Centre + new Offset(x, y), TerrainPixel.Blank);
        var tank = world.TankList.Tanks[0];
        tank.Position = Centre;
        tank.Heat = 60f;
        tank.Hold.Add(hold);
        var nest = world.Emberites!.AddNest(Centre + new Offset(30, 0), radius: 4, creatures: creatures);
        return (world, tank, nest);
    }

    /// <summary>Steps once with the tank held warm enough to be hunted; returns the bite event of the step, if any.</summary>
    private static StepEvent? Step(World world, Tank tank)
    {
        tank.Heat = Math.Min(tank.Heat, 60f);
        world.Advance(_ => default);
        foreach (StepEvent e in world.StepEvents.ToArray())
            if (e.Kind == StepEventKind.EmberiteBit)
                return e;
        return null;
    }

    private static StepEvent StepToBite(World world, Tank tank)
    {
        for (int i = 0; i < 400; i++)
            if (Step(world, tank) is { } bite)
                return bite;
        throw new Xunit.Sdk.XunitException("no creature bit");
    }

    /// <summary>Every unit of pyrite: the tank's hold, the bases' stores, the ground, the nests and the creatures carrying it.</summary>
    private static int TotalPyrite(World world)
    {
        int total = 0;
        foreach (Tank tank in world.TankList.Tanks)
            total += tank.Hold.Ore;
        foreach (var tankBase in world.TankBases.Bases)
            total += tankBase.Store.Ore;
        for (int y = 0; y < world.Terrain.Height; y++)
            for (int x = 0; x < world.Terrain.Width; x++)
            {
                TerrainPixel pixel = world.Terrain.GetPixelRaw(new Position(x, y));
                total += pixel == TerrainPixel.Pyrite ? 1 : pixel == TerrainPixel.PyriteCore ? 3 : 0;
            }
        foreach (Nest nest in world.Emberites!.Nests)
            total += nest.Pyrite;
        return total + world.Emberites.PyriteCarried();
    }

    private static int GroundPyrite(World world)
    {
        int total = 0;
        for (int y = 0; y < world.Terrain.Height; y++)
            for (int x = 0; x < world.Terrain.Width; x++)
                total += world.Terrain.GetPixelRaw(new Position(x, y)) == TerrainPixel.Pyrite ? 1 : 0;
        return total;
    }

    [Fact]
    public void ABite_HurtsByTheBiteDamage_AndStillMovesHeat()
    {
        var (world, tank, _) = Arena(creatures: 1, hold: 0);
        int health = tank.Reactor.Health;
        float heat = 0f;
        StepEvent? bite = null;
        for (int i = 0; i < 400 && bite is null; i++)
        {
            health = tank.Reactor.Health;
            heat = tank.Heat = Math.Min(tank.Heat, 60f);
            bite = Step(world, tank);
        }

        Assert.NotNull(bite);
        Assert.Equal(health - Tweaks.Emberites.BiteDamage, tank.Reactor.Health);
        Assert.Equal(DamageCause.Emberite, tank.LastDamageCause);
        Assert.True(tank.Heat > heat + 0.5f, $"a bite should still add heat: {heat} -> {tank.Heat}");
        Assert.Equal(0, bite.Value.Count);
    }

    [Fact]
    public void ABiteDuringSpawnProtection_DoesNoDamage()
    {
        var (world, tank, _) = Arena(creatures: 1, hold: 2);
        tank.Spawn(world.Terrain.Ledger);
        tank.Position = Centre;
        tank.Heat = 60f;
        int health = tank.Reactor.Health;

        for (int i = 0; i < 30; i++)
        {
            Assert.Null(Step(world, tank));
            Assert.True(tank.SpawnProtectionTicks > 0, "the shield should still be up");
        }
        Assert.Equal(health, tank.Reactor.Health);
        Assert.Equal(2, tank.Hold.Ore);

        // Once it is down, the creature that waited bites.
        StepEvent bite = StepToBite(world, tank);
        Assert.Equal(0, tank.SpawnProtectionTicks);
        Assert.Equal(1, bite.Count);
    }

    [Fact]
    public void ABite_TakesAUnitOfPyriteFromTheHold_AndTheCreatureCarriesIt()
    {
        var (world, tank, _) = Arena(creatures: 1, hold: 3);

        StepEvent bite = StepToBite(world, tank);

        Assert.Equal(Tweaks.Emberites.BiteStealUnits, bite.Count);
        Assert.Equal(2, tank.Hold.Ore);
        Assert.Equal(1, world.Emberites!.PyriteCarried());
    }

    [Fact]
    public void ABiteOnAnEmptyHold_TakesNothing()
    {
        var (world, tank, nest) = Arena(creatures: 1, hold: 0);

        StepEvent bite = StepToBite(world, tank);

        Assert.Equal(0, bite.Count);
        Assert.Equal(0, world.Emberites!.PyriteCarried());
        Assert.Equal(0, nest.Pyrite);
    }

    [Fact]
    public void ACarrierThatGetsHome_PutsItsPyriteInTheNestStore()
    {
        var (world, tank, nest) = Arena(creatures: 1, hold: 3);
        StepToBite(world, tank);

        for (int i = 0; i < 200 && nest.Pyrite == 0; i++)
            Step(world, tank);

        Assert.Equal(1, nest.Pyrite);
        Assert.Equal(0, world.Emberites!.PyriteCarried());
        Assert.Equal(2, tank.Hold.Ore);
    }

    [Fact]
    public void ACarrierKilledOnTheWay_SpillsItsPyriteOnTheGround()
    {
        var (world, tank, nest) = Arena(creatures: 1, hold: 3);
        StepToBite(world, tank);
        Assert.Equal(1, world.Emberites!.PyriteCarried());
        int ground = GroundPyrite(world);

        var states = new EmberiteRenderState[world.Emberites.OutCount];
        world.CopyEmberiteRenderStates(states);
        var at = states[0].Position;
        var bullet = Projectile.CreateBullet(new Position(0, 0), new VectorF(1f, 0f), ownerColor: tank.Color);
        bullet.Position = new PositionF(at.X - 1f, at.Y);
        world.Projectiles.Add(bullet);
        world.Advance(_ => default);

        Assert.Contains(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.EmberiteKilled);
        Assert.Equal(0, world.Emberites.PyriteCarried());
        Assert.Equal(ground + 1, GroundPyrite(world));
        Assert.Equal(0, nest.Pyrite);
    }

    [Fact]
    public void ANestClearedWhileHoldingPyrite_FreesItIntoTheGround()
    {
        var (world, tank, nest) = Arena(creatures: 1, hold: 3);
        StepToBite(world, tank);
        for (int i = 0; i < 200 && nest.Pyrite == 0; i++)
            Step(world, tank);
        Assert.Equal(1, nest.Pyrite);
        int ground = GroundPyrite(world);

        // The only creature is resting at home; clearing the nest is a matter of killing it, so use the counters.
        nest.Resting = 0;
        // The stored pyrite is spilled on the nest's check cadence, so give it one full cycle.
        for (int i = 0; i < Tweaks.Emberites.WakeCheckTicks && nest.Pyrite > 0; i++)
            world.Advance(_ => default);

        Assert.True(nest.IsCleared);
        Assert.Equal(0, nest.Pyrite);
        Assert.Equal(ground + 1, GroundPyrite(world));
    }

    [Fact]
    public void TotalPyrite_IsTheSameEveryTick_WhileTheyBiteAndCarry()
    {
        var (world, tank, _) = Arena(creatures: 10, hold: 8);
        int total = TotalPyrite(world);

        int stolen = 0;
        for (int i = 0; i < 600; i++)
        {
            if (Step(world, tank) is { } bite)
                stolen += bite.Count;
            Assert.Equal(total, TotalPyrite(world));
        }
        Assert.True(stolen > 0, "nothing was stolen");
    }

    [Fact]
    public void ASnapshotWithACarrierHoldingPyrite_RoundTripsAndPlaysOnAlike()
    {
        var (world, tank, nest) = Arena(creatures: 6, hold: 4);
        nest.Pyrite = 2;
        StepToBite(world, tank);
        Assert.True(world.Emberites!.PyriteCarried() > 0, "the snapshot should be taken with a carrier out");

        SnapshotRoundTrip.AssertPlaysOnAlike(world, [null, null],
            () => TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false, emberites: true), ticks: 120,
            (_, _) => default, firstFrame: 0, before: (w, _) => w.TankList.Tanks[0].Heat = Math.Min(w.TankList.Tanks[0].Heat, 60f));
    }
}
