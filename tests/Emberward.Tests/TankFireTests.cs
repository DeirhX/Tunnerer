using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

namespace Emberward.Tests;

/// <summary>A damaged hull burns, and the fire heats the tank and, more, the air around it (DESIGN.md §4.7).</summary>
public class TankFireTests
{
    private static readonly Position Centre = new(60, 100);

    private static (World World, Tank Tank) Burning(int hullLeft)
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false, tankShield: true);
        var tank = world.TankList.Tanks[0];
        // In open ground clear of the bases, which would cool the tank and repair the hull.
        for (int y = -30; y <= 30; y++)
            for (int x = -60; x <= 60; x++)
                world.Terrain.SetPixel(Centre + new Offset(x, y), TerrainPixel.Blank);
        tank.Position = Centre;
        tank.TakeDamage(Tweaks.TankShield.Capacity + tank.Reactor.Health - hullLeft, DamageCause.EnemyFire);
        return (world, tank);
    }

    private static void Step(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++)
            world.Advance(_ => TestSimulation.Idle);
    }

    [Theory]
    [InlineData(1000, 0f)]
    [InlineData(750, 0f)]
    [InlineData(375, 0.5f)]
    [InlineData(1, 1f)]
    public void TheFire_GrowsAsTheHullFalls(int hull, float expected)
    {
        var (world, tank) = Burning(hull);

        Assert.Equal(expected, tank.FireLevel, 2);
        Assert.Equal(tank.FireLevel, world.TankShieldOf(tank).FireLevel);
    }

    [Fact]
    public void WithoutTheShieldRule_NothingBurns()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false);
        var tank = world.TankList.Tanks[0];
        tank.TakeDamage(900, DamageCause.EnemyFire);

        Assert.Equal(0f, tank.FireLevel);
    }

    [Fact]
    public void ABurningTank_EndsHotterThanAnUnhurtOne_AndWarmsTheAir()
    {
        var (burnWorld, burning) = Burning(100);
        var (calmWorld, calm) = Burning(1000);
        Assert.True(burning.FireLevel > 0.8f);
        Assert.Equal(0f, calm.FireLevel);

        Step(burnWorld, 60);
        Step(calmWorld, 60);

        Assert.True(burning.Heat > calm.Heat + 1f, $"burning {burning.Heat}, calm {calm.Heat}");
        // The air around the burning tank is warm; around the other it is not.
        Assert.True(burnWorld.Terrain.GetAirTemperature(burning.Position) > calmWorld.Terrain.GetAirTemperature(calm.Position) + 1f);
    }

    [Fact]
    public void TheLedger_BalancesEveryTickWhileBurning()
    {
        var (world, tank) = Burning(100);
        var ledger = world.Terrain.Ledger;
        double Balance() => world.SumSystemThermalEnergy() + world.SumSystemCharge() - ledger.Injected + ledger.TotalSunk;
        double start = Balance();

        for (int i = 0; i < 120; i++)
        {
            world.Advance(_ => TestSimulation.Idle);
            Assert.Equal(start, Balance(), tolerance: 1.0);
        }
        Assert.True(ledger.Injected > 0.0);
    }

    [Fact]
    public void DockingRepairsTheHull_AndPutsTheFireOut()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false, tankShield: true);
        var tank = world.TankList.Tanks[0];
        tank.TakeDamage(Tweaks.TankShield.Capacity + 900, DamageCause.EnemyFire);
        Assert.True(tank.FireLevel > 0.5f);
        Assert.True(tank.IsDockedAtOwnBase);

        for (int i = 0; i < 400 && tank.FireLevel > 0f; i++)
            world.Advance(_ => TestSimulation.Idle);

        Assert.Equal(0f, tank.FireLevel);
        Assert.False(tank.IsDead);
    }

    [Fact]
    public void AFullFire_AddsNoMoreHeatThanAFullStrengthLinkTakesAway()
    {
        var settings = SimulationSettings.FromTweaks();

        Assert.True(settings.TankFireHeatPerTickMax <= settings.TankNetworkCoolingPerTick + 1e-6f);
    }

    [Fact]
    public void ABurningTank_RaisesTheFireWarning_AndAnUnhurtOneDoesNot()
    {
        var (_, burning) = Burning(300);
        var (_, calm) = Burning(1000);
        var warnings = new Emberward.Desktop.Gui.Guidance.PlayerWarnings();

        Assert.Equal(Emberward.Desktop.Gui.Guidance.WarningKind.FireAboard, warnings.Update(burning, offerThermalView: false, 0f)?.Kind);
        Assert.NotEqual(Emberward.Desktop.Gui.Guidance.WarningKind.FireAboard, new Emberward.Desktop.Gui.Guidance.PlayerWarnings()
            .Update(calm, offerThermalView: false, 0f)?.Kind);
    }
}
