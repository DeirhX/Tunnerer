using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities.Emberites;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

namespace Emberward.Tests;

/// <summary>
/// How a nest lets its creatures out (DESIGN.md §6.2): in pulses, each after a stir that warns of it and with a lull
/// behind it, sized by the hunted tank's heat; a resting creature leaves only with a full charge, and one the deposit
/// cannot feed starves, so a cold nest clears itself of the creatures that have bitten.
/// </summary>
public class EmberitePulseTests
{
    // Clear of both bases of the 320 x 200 test map, which stand at (130, 21) and (185, 178).
    private static readonly Position Centre = new(60, 100);
    private static readonly Position NestAt = Centre + new Offset(30, 0);
    private const double FullCharge = Tweaks.Emberites.BiteDegrees * (double)Tweaks.Tank.TankHeatCapacity;

    /// <summary>A world with the swarm on, open ground around the tank and a nest of <paramref name="creatures"/> 30 cells from it.</summary>
    private static (World World, Tank Tank, Nest Nest) Arena(int creatures)
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false, emberites: true);
        for (int y = -30; y <= 30; y++)
            for (int x = -60; x <= 60; x++)
                world.Terrain.SetPixel(Centre + new Offset(x, y), TerrainPixel.Blank);
        var tank = world.TankList.Tanks[0];
        tank.Position = Centre;
        return (world, tank, world.Emberites!.AddNest(NestAt, radius: 4, creatures: creatures));
    }

    /// <summary>
    /// One step with the tank held at <paramref name="heat"/> (null leaves its heat alone, as a test of the energy
    /// balance must: setting it makes heat from nothing); returns the step's events.
    /// </summary>
    private static StepEvent[] Step(World world, Tank tank, float? heat)
    {
        if (heat is float held)
            tank.Heat = held;
        world.Advance(_ => default);
        return world.StepEvents.ToArray();
    }

    [Fact]
    public void AWokenNest_StirsForAWhile_BeforeItLetsAnyOut()
    {
        var (world, tank, nest) = Arena(creatures: 6);

        int stirred = -1, firstOut = -1;
        for (int tick = 0; tick < 3 * Tweaks.Emberites.StirTicks && firstOut < 0; tick++)
        {
            foreach (StepEvent e in Step(world, tank, heat: 80f))
                if (e.Kind == StepEventKind.NestStirred)
                {
                    Assert.Equal(-1, stirred);
                    Assert.Equal(6, e.Count);
                    Assert.Equal(nest.Centre, e.Position);
                    stirred = tick;
                }
            if (stirred == tick)
                Assert.Equal(NestPhase.Stirring, nest.Phase);
            if (world.Emberites!.OutCount > 0)
                firstOut = tick;
        }

        Assert.True(stirred >= 0, "the nest never stirred");
        Assert.Equal(stirred + Tweaks.Emberites.StirTicks, firstOut);
    }

    [Fact]
    public void APulse_IsFollowedByALull_ThenAnotherStir()
    {
        var (world, tank, nest) = Arena(creatures: 12);
        float heat = Tweaks.Emberites.WakeHeat + 3f;

        int stirs = 0, lastRelease = -1, secondStir = -1, firstPulse = 0, left = nest.NeverReleased;
        for (int tick = 0; tick < 4 * Tweaks.Emberites.LullTicks && secondStir < 0; tick++)
        {
            foreach (StepEvent e in Step(world, tank, heat))
                if (e.Kind == StepEventKind.NestStirred && ++stirs == 2)
                    secondStir = tick;
            if (nest.NeverReleased < left)
            {
                left = nest.NeverReleased;
                lastRelease = tick;
                firstPulse = 12 - left;
            }
        }

        Assert.InRange(firstPulse, Tweaks.Emberites.PulseMinCreatures, 6);
        Assert.True(secondStir >= 0, "the nest did not stir a second time");
        Assert.True(secondStir - lastRelease >= Tweaks.Emberites.LullTicks,
            $"the lull was {secondStir - lastRelease} ticks, shorter than {Tweaks.Emberites.LullTicks}");
        Assert.Equal(12 - firstPulse, nest.NeverReleased);
    }

    [Fact]
    public void ThePulse_GrowsWithTheTanksHeat()
    {
        int least = Tweaks.Emberites.PulseMinCreatures;
        Assert.Equal(least, EmberiteSwarm.PulseSize(Tweaks.Emberites.WakeHeat - 20f, ready: 20));
        Assert.Equal(least, EmberiteSwarm.PulseSize(Tweaks.Emberites.WakeHeat, ready: 20));
        Assert.Equal(20, EmberiteSwarm.PulseSize(Tweaks.Emberites.FullHeat, ready: 20));
        Assert.Equal(20, EmberiteSwarm.PulseSize(Tweaks.Emberites.FullHeat + 60f, ready: 20));
        Assert.InRange(EmberiteSwarm.PulseSize(70f, ready: 20), least + 1, 19);
        Assert.True(EmberiteSwarm.PulseSize(85f, ready: 20) > EmberiteSwarm.PulseSize(60f, ready: 20));
        // Never more than could leave.
        Assert.Equal(2, EmberiteSwarm.PulseSize(Tweaks.Emberites.WakeHeat, ready: 2));
        Assert.Equal(0, EmberiteSwarm.PulseSize(Tweaks.Emberites.FullHeat, ready: 0));

        // In the world: the first pulse at a tank barely warm enough to wake the nest, and at one running flat out.
        int FirstPulse(float heat)
        {
            var (world, tank, nest) = Arena(creatures: 12);
            for (int tick = 0; tick < Tweaks.Emberites.WakeCheckTicks + Tweaks.Emberites.StirTicks + 12 * Tweaks.Emberites.PulseSpacingTicks + 8; tick++)
                Step(world, tank, heat);
            return 12 - nest.NeverReleased;
        }
        int cool = FirstPulse(Tweaks.Emberites.WakeHeat + 3f);
        Assert.InRange(cool, least, 5);
        Assert.Equal(12, FirstPulse(Tweaks.Emberites.FullHeat));
    }

    [Fact]
    public void ARestingCreature_LeavesOnlyWithAFullCharge()
    {
        var (world, tank, nest) = Arena(creatures: 0);
        nest.Resting = 2;
        nest.Stored = 1.5 * FullCharge; // one whole charge and half of another; the open ground around is cold

        int mostOut = 0;
        bool checkedCharge = false;
        for (int tick = 0; tick < 200; tick++)
        {
            Step(world, tank, heat: 80f);
            mostOut = Math.Max(mostOut, nest.Out);
            if (nest.Out == 1 && !checkedCharge)
            {
                checkedCharge = true;
                Assert.Equal(0.5 * FullCharge, nest.Stored, tolerance: 1e-6);
                Assert.Equal(1.5 * FullCharge, world.Emberites!.TotalCharge(), tolerance: 1e-6);
            }
        }

        Assert.True(checkedCharge, "the creature with a full charge should have left");
        Assert.Equal(1, mostOut);
    }

    [Fact]
    public void SpentCreaturesInAColdNest_Starve_AndTheNestIsCleared()
    {
        var (world, tank, nest) = Arena(creatures: 0);
        nest.Resting = 3; // three that have bitten and come home to a deposit with no heat to give
        var ledger = world.Terrain.Ledger;
        double Balance() => world.SumSystemThermalEnergy() + world.SumSystemCharge() - ledger.Injected + ledger.TotalSunk;
        double start = Balance();

        for (int tick = 0; tick < Tweaks.Emberites.StarveTicks - 2 * Tweaks.Emberites.RechargeIntervalTicks; tick++)
            Step(world, tank, heat: null);
        Assert.Equal(3, nest.Resting);
        Assert.True(world.IsOreGuarded(nest.Centre));

        var starved = new List<int>();
        for (int tick = 0; tick < 3 * Tweaks.Emberites.StarveStepTicks + 4 * Tweaks.Emberites.RechargeIntervalTicks; tick++)
        {
            foreach (StepEvent e in Step(world, tank, heat: null))
                if (e.Kind == StepEventKind.EmberiteStarved)
                    starved.Add(e.Count);
            Assert.Equal(start, Balance(), tolerance: 1.0);
        }

        Assert.Equal([2, 1, 0], starved);
        Assert.True(nest.IsCleared);
        Assert.False(world.IsOreGuarded(nest.Centre));
        Assert.Equal(0L, world.Emberites!.CreatureSteps);
    }

    [Fact]
    public void AStarvedCreaturesPartCharge_GoesBackIntoTheGround()
    {
        var (world, tank, nest) = Arena(creatures: 0);
        nest.Resting = 1;
        nest.Stored = 0.5 * FullCharge;
        var ledger = world.Terrain.Ledger;
        double Balance() => world.SumSystemThermalEnergy() + world.SumSystemCharge() - ledger.Injected + ledger.TotalSunk;
        double start = Balance();

        for (int tick = 0; tick < Tweaks.Emberites.StarveTicks + 2 * Tweaks.Emberites.RechargeIntervalTicks; tick++)
        {
            Step(world, tank, heat: null);
            Assert.Equal(start, Balance(), tolerance: 1.0);
        }

        Assert.True(nest.IsCleared);
        Assert.Equal(0.0, world.Emberites!.TotalCharge());
    }

    [Fact]
    public void AWarmDeposit_FeedsItsCreatures_AndNoneStarve()
    {
        var (world, tank, nest) = Arena(creatures: 0);
        nest.Resting = 3;
        world.Terrain.AddEnergyInRadiusArea(nest.Centre, nest.Radius, 40 * FullCharge, TerrainGrid.ThermalField.Terrain);

        var events = new List<StepEvent>();
        for (int tick = 0; tick < Tweaks.Emberites.StarveTicks + 4 * Tweaks.Emberites.StarveStepTicks; tick++)
            events.AddRange(Step(world, tank, heat: 10f));

        Assert.Equal(3, nest.Resting);
        Assert.Equal(3 * FullCharge, nest.Stored, tolerance: 1e-3);
        Assert.Equal(0, nest.HungerTicks);
        Assert.DoesNotContain(events, e => e.Kind == StepEventKind.EmberiteStarved);
    }

    [Fact]
    public void AStirringNest_RoundTripsThroughASnapshot_AndPlaysOnAlike()
    {
        var (world, tank, nest) = Arena(creatures: 8);
        nest.Resting = 2; // and two that go hungry, so the hunger count is in the snapshot too
        for (int tick = 0; tick < Tweaks.Emberites.WakeCheckTicks + 6; tick++)
            Step(world, tank, heat: 80f);
        Assert.Equal(NestPhase.Stirring, nest.Phase);
        Assert.True(nest.HungerTicks > 0);

        int taken = Tweaks.Emberites.WakeCheckTicks + 6;
        SnapshotRoundTrip.AssertPlaysOnAlike(world, [null, null],
            () => TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false, emberites: true), ticks: 4 * Tweaks.Emberites.StirTicks,
            (_, _) => default, firstFrame: taken, before: (w, _) => w.TankList.Tanks[0].Heat = 80f);
    }
}
