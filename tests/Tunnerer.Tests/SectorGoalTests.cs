using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Links;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Input;
using Tunnerer.Core.Resources;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;
using static Tunnerer.Tests.TankHeatTestHelpers;

namespace Tunnerer.Tests;

/// <summary>
/// The sector goal and upgrades (DESIGN.md §4.8): the base's ore store buys upgrades for the base and its tank, and the
/// sector is won by tapping the hot core at the map centre at the goal rate for long enough.
/// </summary>
public class SectorGoalTests
{
    private static void Fund(TankBase b, int ore) => b.Store.Add(ore);

    [Theory]
    [InlineData(BaseUpgrade.Radiator)]
    [InlineData(BaseUpgrade.CargoHold)]
    [InlineData(BaseUpgrade.Radar)]
    public void Upgrades_CostOreFromTheStore_25_60_140_AndStopAtTheMax(BaseUpgrade upgrade)
    {
        var s = RelayHeatTests.CreateScenario();
        var b = s.Base;
        Assert.Equal([25, 60, 140], Tweaks.Upgrades.LevelCosts.ToArray());
        Assert.Equal(Tweaks.Upgrades.MaxLevel, Tweaks.Upgrades.LevelCosts.Length);

        for (int level = 0; level < BaseUpgrades.MaxLevel; level++)
        {
            int cost = Tweaks.Upgrades.LevelCosts[level];
            Assert.Equal(cost, b.Upgrades.NextCost(upgrade));
            b.Store.Ore = cost - 1;
            Assert.False(b.TryBuyUpgrade(upgrade), "one short");
            b.Store.Ore = cost;
            Assert.True(b.TryBuyUpgrade(upgrade));
            Assert.Equal(0, b.Store.Ore);
            Assert.Equal(level + 1, b.Upgrades.Level(upgrade));
        }
        Assert.Equal(0, b.Upgrades.NextCost(upgrade));
        Fund(b, b.Store.Capacity);
        Assert.False(b.TryBuyUpgrade(upgrade), "maxed out");
    }

    [Fact]
    public void Upgrades_ComeInTwoTracks_FourForTheBase_TwoForTheTank()
    {
        var tracks = Enum.GetValues<BaseUpgrade>().Select(BaseUpgrades.TrackOf).ToArray();
        Assert.Equal(BaseUpgrades.Count, tracks.Length);
        Assert.Equal(4, tracks.Count(t => t == UpgradeTrack.Base));
        Assert.Equal([BaseUpgrade.CargoHold, BaseUpgrade.Radar],
            Enum.GetValues<BaseUpgrade>().Where(u => BaseUpgrades.TrackOf(u) == UpgradeTrack.Tank));
        foreach (BaseUpgrade u in Enum.GetValues<BaseUpgrade>())
            Assert.False(string.IsNullOrEmpty(BaseUpgrades.Describe(u).Effect));
    }

    [Fact]
    public void ACargoHoldLevel_AddsThirtyToTheTanksHold()
    {
        var s = RelayHeatTests.CreateScenario();
        var press = new ControllerOutput { Upgrade = (int)BaseUpgrade.CargoHold + 1 };
        int[] expected = [40, 70, 100, 130];
        Assert.Equal(expected[0], s.Tank.OreHoldCapacity);
        for (int level = 1; level <= BaseUpgrades.MaxLevel; level++)
        {
            Fund(s.Base, s.Base.Store.Capacity);
            s.World.Advance(i => i == 0 ? press : default);
            s.World.Advance(_ => default);
            Assert.Equal(level, s.Base.Upgrades.Level(BaseUpgrade.CargoHold));
            Assert.Equal(expected[level], s.Tank.OreHoldCapacity);
        }
    }

    [Fact]
    public void ARadarLevel_ScalesEveryRange_ByAFifth_AndTheHeatBudget_ByAQuarter()
    {
        var b = RelayHeatTests.CreateScenario().Base;
        for (int level = 0; level <= BaseUpgrades.MaxLevel; level++)
        {
            Assert.Equal(1f + 0.2f * level, b.Upgrades.RadarRangeScale, 5);
            Assert.Equal(1f + 0.25f * level, b.Upgrades.RadarHeatBudgetScale, 5);
            if (level < BaseUpgrades.MaxLevel)
            {
                Fund(b, b.Store.Capacity);
                b.TryBuyUpgrade(BaseUpgrade.Radar);
            }
        }
    }

    [Fact]
    public void Upgrades_AreRefused_WhenTheStoreCannotPay()
    {
        var s = RelayHeatTests.CreateScenario();
        s.Base.Store.Ore = Tweaks.Upgrades.LevelCosts[0] - 1;
        Assert.False(s.Base.TryBuyUpgrade(BaseUpgrade.Engine));
        Assert.Equal(0, s.Base.Upgrades.Level(BaseUpgrade.Engine));
    }

    [Fact]
    public void EachUpgrade_ChangesWhatItSays()
    {
        var s = RelayHeatTests.CreateScenario();
        var b = s.Base;
        int radiator = b.Upgrades.RadiatorUnits;
        int reach = b.Upgrades.CoverageBonusCells;
        double eta = b.UpgradedEngineEfficiency(40);
        float insulation = s.Tank.OffNetworkHeatPerSecond;

        foreach (BaseUpgrade u in Enum.GetValues<BaseUpgrade>())
        {
            Fund(b, b.Store.Capacity);
            Assert.True(b.TryBuyUpgrade(u));
        }

        Assert.True(b.Upgrades.RadiatorUnits > radiator);
        Assert.True(b.Upgrades.CoverageBonusCells > reach);
        Assert.True(b.UpgradedEngineEfficiency(40) > eta);
        Assert.True(s.Tank.OffNetworkHeatPerSecond < insulation);

        var nodes = new List<CoverageNode>();
        s.World.CollectCoverageNodes(b, nodes);
        Assert.Equal(Tweaks.Coverage.BaseRadius + b.Upgrades.CoverageBonusCells, nodes[0].Radius);
    }

    [Fact]
    public void ARadiatorUpgrade_VentsMoreHeatPerTick()
    {
        double Vented(bool upgraded)
        {
            var s = RelayHeatTests.CreateScenario();
            if (upgraded)
            {
                Fund(s.Base, s.Base.Store.Capacity);
                s.Base.TryBuyUpgrade(BaseUpgrade.Radiator);
            }
            s.Base.Reactor.Current.Heat = s.Base.Reactor.HeatCapacity;
            double before = s.World.Terrain.Ledger.SunkBase;
            s.World.Advance(_ => default);
            return s.World.Terrain.Ledger.SunkBase - before;
        }

        Assert.True(Vented(upgraded: true) > Vented(upgraded: false) + Tweaks.Base.ReactorEnergyPerHeatUnit * 0.9);
    }

    [Fact]
    public void TheTank_BuysUpgradesWithTheUpgradeKeys_OnlyWhileDocked()
    {
        var s = RelayHeatTests.CreateScenario();
        Fund(s.Base, s.Base.Store.Capacity);
        var press = new ControllerOutput { Upgrade = (int)BaseUpgrade.Reach + 1 };

        // Docked (the scenario starts the tank in its base): one press buys one level.
        s.World.Advance(i => i == 0 ? press : default);
        s.World.Advance(i => i == 0 ? press : default);   // held, not a new press
        Assert.Equal(1, s.Base.Upgrades.Level(BaseUpgrade.Reach));
        s.World.Advance(_ => default);

        // Away from the base the press is refused.
        s.Tank.Position = s.RelayPos;
        s.World.Advance(i => i == 0 ? press : default);
        Assert.Equal(1, s.Base.Upgrades.Level(BaseUpgrade.Reach));
        Assert.True(s.Tank.UpgradeRefusedTicks > 0);
    }

    [Fact]
    public void ARefusedUpgrade_SaysWhy()
    {
        var s = RelayHeatTests.CreateScenario();
        var engine = new ControllerOutput { Upgrade = (int)BaseUpgrade.Engine + 1 };
        void Press() { s.World.Advance(i => i == 0 ? engine : default); s.World.Advance(_ => default); }

        s.Base.Store.Ore = 0;
        Press();   // docked, empty store
        Assert.Equal(UpgradeRefusal.NotEnoughOre, s.Tank.LastUpgradeRefusal);

        // A successful press right after clears the refusal, so its message does not linger.
        Fund(s.Base, s.Base.Store.Capacity);
        Press();
        Assert.Equal(UpgradeRefusal.None, s.Tank.LastUpgradeRefusal);
        Assert.Equal(0, s.Tank.UpgradeRefusedTicks);

        while (!s.Base.Upgrades.IsMaxed(BaseUpgrade.Engine))
        {
            Fund(s.Base, s.Base.Store.Capacity);
            Assert.True(s.Base.TryBuyUpgrade(BaseUpgrade.Engine));
        }
        Fund(s.Base, s.Base.Store.Capacity);
        Press();
        Assert.Equal(UpgradeRefusal.Maxed, s.Tank.LastUpgradeRefusal);

        s.Tank.Position = s.RelayPos;
        Press();
        Assert.Equal(UpgradeRefusal.NotDocked, s.Tank.LastUpgradeRefusal);
    }

    [Fact]
    public void CoreHold_GrowsAtTheGoalRate_AndDrainsFasterBelowIt()
    {
        double dt = Tweaks.World.AdvanceStep.TotalSeconds;
        double goalPerTick = Tweaks.Goal.GoalChargePerSecond * dt;

        Assert.Equal(dt, World.NextCoreHold(0, goalPerTick, dt), precision: 9);
        Assert.Equal(10 - dt * Tweaks.Goal.GoalHoldDecay, World.NextCoreHold(10, goalPerTick * 0.5, dt), precision: 9);
        Assert.Equal(0, World.NextCoreHold(0.001, 0, dt));
    }

    [Trait("Category", "Slow")]
    [Fact]
    public void EveryWorldWithPockets_HasACore_ClearOfTheBases_AndOneWithoutDoesNot()
    {
        int minSq = Tweaks.World.HotRockMinDistanceFromBase * Tweaks.World.HotRockMinDistanceFromBase;
        for (int seed = 8180; seed < 8186; seed++)
        {
            var world = TestHelpers.CreateSeededWorld(seed: seed, hotRockPocketCount: 4);
            var core = world.CorePocket;
            Assert.NotNull(core);
            Assert.Equal(TerrainPixel.HotRock, world.Terrain.GetPixelRaw(core.Value));
            Assert.All(world.TankBases.Bases, b => Assert.True(Position.DistanceSquared(core.Value, b.Position) >= minSq));
        }
        Assert.Null(TestHelpers.CreateSeededWorld(seed: 8181).CorePocket);
    }

    [Fact]
    public void RelaysBesideTheCore_CountTowardTheGoal()
    {
        var world = TestHelpers.CreateSeededWorld(seed: 8181, hotRockPocketCount: 4);
        var tank = world.TankList.Tanks[0];
        var home = tank.Base!;
        var core = world.CorePocket!.Value;

        // A straight open corridor from the base to the core's edge, with a relay chain along it.
        var from = home.Position;
        float dx = core.X - from.X, dy = core.Y - from.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        float ux = dx / length, uy = dy / length;
        float stopAt = length - (Tweaks.World.HotRockCoreRadius + 3);
        for (float t = Tweaks.Base.BaseSize / 2f - 2f; t <= stopAt; t += 0.5f)
            for (int w = -2; w <= 2; w++)
            {
                var p = new Position((int)MathF.Round(from.X + ux * t - uy * w), (int)MathF.Round(from.Y + uy * t + ux * w));
                if (world.Terrain.IsInside(p) && Pixel.IsDiggable(world.Terrain.GetPixelRaw(p)) || world.Terrain.GetPixelRaw(p) == TerrainPixel.Rock)
                    world.Terrain.SetPixel(p, TerrainPixel.Blank);
            }
        for (float t = stopAt; t > Tweaks.Base.BaseSize / 2f; t -= 60f)
        {
            var at = new Position((int)MathF.Round(from.X + ux * t), (int)MathF.Round(from.Y + uy * t));
            var relay = new Machine(at, MachineType.Relay, tank.Color);
            world.Machines.Add(relay);
            world.PlantMachine(relay, home);
        }

        double maxTap = 0;
        for (int i = 0; i < Tweaks.Perf.TargetFps * 4; i++)
        {
            world.Advance(_ => default);
            maxTap = Math.Max(maxTap, home.CoreTapPerTick);
        }

        Assert.True(maxTap > 0, "a linked relay at the core's edge should make charge that counts toward the goal");
    }
}
