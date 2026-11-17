using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Core.Input;
using Tunnerer.Core.Rendering;
using Tunnerer.Core.Sensing;
using Tunnerer.Core.Types;
using Tunnerer.Core.Network;
using Tunnerer.Core.Entities.Bases;

namespace Tunnerer.Tests;

/// <summary>
/// Ore as the only currency (DESIGN.md §6.4): who pays for a machine, unloading the hold through the network, the
/// bot's stand-in income, and the radar upgrade staying out of the simulation.
/// </summary>
public class OreEconomyTests
{
    /// <summary>The tank at the relay position with ore in its hold; with a linked relay planted there unless blocked.</summary>
    private static RelayHeatTests.Scenario TankAtRelay(int ore, bool relay = true, bool blockLineOfSight = false)
    {
        var s = RelayHeatTests.CreateScenario(blockLineOfSight: blockLineOfSight);
        if (relay)
            RelayHeatTests.PlantRelay(s);
        // Let the link map power the relay before the tank arrives.
        for (int i = 0; i < 12; i++)
            s.World.Advance(_ => default);
        s.Tank.Position = s.RelayPos;
        s.Tank.Hold.Ore = ore;
        return s;
    }

    private static List<StepEvent> Advance(World world, int ticks, ControllerOutput input = default)
    {
        var events = new List<StepEvent>();
        for (int i = 0; i < ticks; i++)
        {
            world.Advance(t => t == 0 ? input : default);
            events.AddRange(world.StepEvents.ToArray());
        }
        return events;
    }

    [Fact]
    public void BaseStore_StartsWithThirtyOre()
    {
        var s = RelayHeatTests.CreateScenario();
        Assert.Equal(30, Tweaks.Ore.BaseStartingStore);
        Assert.Equal(Tweaks.Ore.BaseStartingStore, s.Base.Store.Ore);
        Assert.Equal(Tweaks.Ore.TankHold, s.Tank.OreHoldCapacity);
    }

    [Fact]
    public void InLinkedCoverage_TheHoldUnloadsIntoTheStore_AtTheNetworkRate_AndOreIsConserved()
    {
        var s = TankAtRelay(ore: 30);
        int total = s.Tank.OreHeld + s.Base.Store.Ore;
        int seconds = 3;

        var events = Advance(s.World, Tweaks.Perf.TargetFps * seconds);

        int unloaded = 30 - s.Tank.OreHeld;
        Assert.True(s.Tank.Coverage.Covered && !s.Tank.IsDockedAtOwnBase);
        Assert.InRange(unloaded, Tweaks.Ore.NetworkUnloadPerSecond * seconds - 1, Tweaks.Ore.NetworkUnloadPerSecond * seconds);
        Assert.Equal(total, s.Tank.OreHeld + s.Base.Store.Ore);
        Assert.True(s.Tank.IsUnloadingOre);
        // Every unit is a step event at the tank, so Desktop can play and draw it.
        var unloads = events.Where(e => e.Kind == StepEventKind.OreUnloaded).ToList();
        Assert.Equal(unloaded, unloads.Sum(e => e.Count));
        Assert.All(unloads, e => Assert.Equal(s.Tank.Color, e.Color));
    }

    [Fact]
    public void Docking_UnloadsFasterThanTheNetwork()
    {
        int ore = Tweaks.Ore.TankHold;
        var docked = RelayHeatTests.CreateScenario();
        docked.Tank.Position = docked.Base.Position;
        docked.Tank.Hold.Ore = ore;
        var networked = TankAtRelay(ore);

        Advance(docked.World, Tweaks.Perf.TargetFps);
        Advance(networked.World, Tweaks.Perf.TargetFps);

        int byDock = ore - docked.Tank.OreHeld, byNetwork = ore - networked.Tank.OreHeld;
        Assert.True(docked.Tank.IsDockedAtOwnBase);
        Assert.True(networked.Tank.Coverage.Covered && !networked.Tank.IsDockedAtOwnBase);
        Assert.True(byNetwork > 0, "the network was unloading too");
        Assert.True(byDock >= byNetwork * 4, $"docking should bank much faster. docked={byDock}, network={byNetwork}");
        Assert.Equal(ore, byDock);
    }

    [Fact]
    public void UnloadedOre_TravelsHomeAlongTheLinks()
    {
        var s = TankAtRelay(ore: 10);
        Advance(s.World, Tweaks.Perf.TargetFps);

        var links = new LinkRenderState[16];
        int count = s.World.LinkMap.CopyRenderStates(links);
        Assert.Contains(links.Take(count), l => l.OreFlow > 0f);
    }

    [Fact]
    public void AnUnlinkedNode_AcceptsNothing()
    {
        var s = TankAtRelay(ore: 30, blockLineOfSight: true);
        int store = s.Base.Store.Ore;

        var events = Advance(s.World, Tweaks.Perf.TargetFps * 2);

        Assert.False(s.Tank.Coverage.Covered);
        Assert.Equal(30, s.Tank.OreHeld);
        Assert.Equal(store, s.Base.Store.Ore);
        Assert.False(s.Tank.IsUnloadingOre);
        Assert.DoesNotContain(events, e => e.Kind == StepEventKind.OreUnloaded);
    }

    [Fact]
    public void Unloading_StopsWhenTheStoreIsFull()
    {
        var s = TankAtRelay(ore: 30);
        s.Base.Store.Ore = s.Base.Store.Capacity;

        Advance(s.World, Tweaks.Perf.TargetFps);

        Assert.Equal(30, s.Tank.OreHeld);
        Assert.False(s.Tank.IsUnloadingOre);
    }

    [Fact]
    public void InLinkedCoverage_TheBaseStorePays()
    {
        var s = TankAtRelay(ore: 10);
        Advance(s.World, 1);
        int hold = s.Tank.OreHeld, store = s.Base.Store.Ore;

        var quote = s.Tank.QuoteBuild(MachineType.Relay);
        Assert.True(quote.InNetwork);
        Assert.Equal(OrePayer.BaseStore, quote.Payer);
        Assert.Equal(store, quote.Stock);

        Advance(s.World, 1, new ControllerOutput { BuildPrimary = true });

        Assert.Equal(MachineType.Relay, s.Tank.CarriedMachineType);
        // The hold may have unloaded a unit meanwhile; what was spent came out of the store.
        Assert.Equal(store + hold - Tweaks.Ore.RelayCost, s.Base.Store.Ore + s.Tank.OreHeld);
        Assert.True(s.Base.Store.Ore <= store - Tweaks.Ore.RelayCost + 1);
    }

    [Fact]
    public void InLinkedCoverage_WhenTheStoreIsShort_TheHoldPays()
    {
        var s = TankAtRelay(ore: 10);
        s.Base.Store.Ore = 0;
        Advance(s.World, 1);

        var quote = s.Tank.QuoteBuild(MachineType.Pylon);
        Assert.Equal(OrePayer.Hold, quote.Payer);

        int hold = s.Tank.OreHeld;
        Advance(s.World, 1, new ControllerOutput { BuildTertiary = true });
        Assert.Equal(MachineType.Pylon, s.Tank.CarriedMachineType);
        Assert.True(s.Tank.OreHeld <= hold - Tweaks.Ore.PylonCost);
    }

    [Fact]
    public void OutsideTheNetwork_OnlyTheHoldCounts()
    {
        var s = TankAtRelay(ore: 2, relay: false);
        Advance(s.World, 1);
        Assert.True(s.Base.Store.Ore >= Tweaks.Ore.HarvesterCost);

        var quote = s.Tank.QuoteBuild(MachineType.Harvester);

        Assert.False(quote.InNetwork);
        Assert.Equal(OrePayer.None, quote.Payer);
        Assert.Equal(BuildRefusal.NotEnoughOre, quote.Refusal);
        Assert.Equal(2, quote.Stock);
    }

    [Fact]
    public void ABotSide_GetsItsStandInIncome_AndOnlyIt()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false);
        var player = world.TankList.Tanks[0].Base!;
        var bot = world.TankList.Tanks[1].Base!;
        world.SetBotControlled(1);
        int seconds = 10;

        Advance(world, Tweaks.Perf.TargetFps * seconds);

        Assert.True(bot.IsBotControlled);
        Assert.False(player.IsBotControlled);
        Assert.Equal(Tweaks.Ore.BaseStartingStore, player.Store.Ore);
        Assert.InRange(bot.Store.Ore - Tweaks.Ore.BaseStartingStore,
            (int)(Tweaks.Ore.BotOrePerSecond * seconds) - 1, (int)(Tweaks.Ore.BotOrePerSecond * seconds));
    }

    [Fact]
    public void RadarUpgrade_LengthensThePulse_WithoutChangingTheSimulation()
    {
        // Two identical worlds; one buys a radar level, the other spends the same ore on nothing.
        World Run(bool radar, out Radar tracked)
        {
            var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false);
            var home = world.TankList.Tanks[0].Base!;
            if (radar)
                Assert.True(home.TryBuyUpgrade(BaseUpgrade.Radar));
            else
                Assert.True(home.Store.Pay(Tweaks.Upgrades.LevelCosts[0]));
            tracked = world.TrackRadar(world.TankList.Tanks[0]);
            for (int i = 0; i < Tweaks.Perf.TargetFps * 3; i++)
                world.Advance(t => t == 0 ? TestSimulation.Move(0, 1) : default);
            return world;
        }

        var upgraded = Run(radar: true, out var longRadar);
        var plain = Run(radar: false, out var shortRadar);

        Assert.Equal(Tweaks.Radar.Range * 1.2f, longRadar.PulseRange, 3);
        Assert.Equal(Tweaks.Radar.Range, shortRadar.PulseRange, 3);
        Assert.Equal(WorldStateHash.Compute(plain), WorldStateHash.Compute(upgraded));
    }

    [Fact]
    public void RadarReach_ScalesEveryRange_AndTheHeatBudget()
    {
        var reach = RadarReach.Scaled(1.4f, 1.5f);
        Assert.Equal(RadarReach.Scaled(1f, 1f), RadarReach.Base);
        Assert.Equal(Tweaks.Radar.Range * 1.4f, reach.Range);
        Assert.Equal(Tweaks.Radar.SolidRangeMin * 1.4f, reach.SolidRangeMin);
        Assert.Equal(Tweaks.Radar.GoldRange * 1.4f, reach.GoldRange);
        Assert.Equal(Tweaks.Radar.MachineRange * 1.4f, reach.MachineRange);
        Assert.Equal(Tweaks.Radar.LifeRange * 1.4f, reach.LifeRange);
        Assert.Equal(Tweaks.Radar.CavityRange * 1.4f, reach.CavityRange);
        Assert.Equal(Tweaks.Radar.HeatRangeMin * 1.4f, reach.HeatRangeMin);
        Assert.Equal(Tweaks.Radar.HeatRangeMax * 1.4f, reach.HeatRangeMax);
        Assert.Equal(Tweaks.Radar.OreRangeMin * 1.4f, reach.OreRangeMin);
        Assert.Equal(Tweaks.Radar.OreRangeMax * 1.4f, reach.OreRangeMax);
        Assert.Equal(Tweaks.Radar.FrontCellsPerTick * 1.4f, reach.FrontCellsPerTick);
        Assert.Equal(Tweaks.Radar.HeatBudgetDegreeCells * 1.5f, reach.HeatBudgetDegreeCells);
    }
}
