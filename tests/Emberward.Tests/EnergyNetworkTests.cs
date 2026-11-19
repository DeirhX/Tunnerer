using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities.Bases;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Match;
using Emberward.Core.Network;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using static Emberward.Tests.SnapshotRoundTrip;

namespace Emberward.Tests;

/// <summary>
/// The energy network (DESIGN.md §4.9, <see cref="EnergyNetwork"/>): every machine has a capacitor, each base's island
/// feeds its machines, a cut-off machine drains, and heat plus charge still balance with the ledger.
/// </summary>
public class EnergyNetworkTests
{
    private static RelayHeatTests.Scenario Scenario(bool blockLineOfSight = false) =>
        RelayHeatTests.CreateScenario(blockLineOfSight: blockLineOfSight, energyNetwork: true);

    private static Position PylonSpot(RelayHeatTests.Scenario s) =>
        s.Base.Position + new Offset(0, s.DirY * (Tweaks.Base.BaseSize / 2 + 4));

    private static Machine Plant(RelayHeatTests.Scenario s, MachineType type, Position at)
    {
        var machine = new Machine(at, type, s.Tank.Color);
        s.World.Machines.Add(machine);
        s.World.PlantMachine(machine, s.Base);
        return machine;
    }

    private static void Advance(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++)
            world.Advance(_ => default);
    }

    private static double IdleRate(MachineType type) => MachineBehaviors.Get(type).Capacitor.IdleDrawPerTick * Tweaks.Perf.TargetFps;

    [Fact]
    public void WithTheFlagOff_ThereIsNoNetwork_AndNoAnswers()
    {
        var s = RelayHeatTests.CreateScenario();
        var relay = RelayHeatTests.PlantRelay(s);
        Advance(s.World, 5);

        Assert.Null(s.World.Energy);
        Assert.Null(s.World.MachinePowerOf(relay));
        Assert.Null(s.World.BasePowerOf(s.Base));
        Assert.Equal(0.0, relay.ChargeCapacity); // a relay keeps no battery off the network
    }

    [Fact]
    public void ALinkedRelay_IsPlantedFull_JoinsItsIsland_AndAtRestCostsNothing()
    {
        var s = Scenario();
        double storeBefore = s.Base.Charge;
        var relays = Enumerable.Range(0, 3).Select(i => RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, -s.DirY * 4 * i))).ToArray();

        Assert.All(relays, r => Assert.Equal((double)Tweaks.Machine.RelayCapacitor, r.Charge));
        Assert.Equal(storeBefore - 3 * Tweaks.Machine.RelayCapacitor, s.Base.Charge, precision: 6);

        Advance(s.World, Tweaks.Perf.TargetFps * 2);
        var energy = s.World.Energy!;
        for (int i = 0; i < Tweaks.Perf.TargetFps * 5; i++)
        {
            s.World.Advance(_ => default);
            Assert.Equal(0, energy.FillingCount);
            Assert.All(relays, r => Assert.Equal((double)Tweaks.Machine.RelayCapacitor, r.Charge));
        }

        var power = s.World.MachinePowerOf(relays[0])!.Value;
        Assert.True(power.IsFed);
        Assert.Equal((double)Tweaks.Machine.RelayCapacitor, power.Charge);
        Assert.Equal((double)Tweaks.Machine.RelayCapacitor, power.Capacity);
        Assert.Equal(IdleRate(MachineType.Relay), power.InPerSecond, precision: 6);
        Assert.Equal(IdleRate(MachineType.Relay), power.OutPerSecond, precision: 6);

        // The base pays the three idle draws, and its engine makes nothing on cold ground.
        var store = s.World.BasePowerOf(s.Base)!.Value;
        Assert.Equal(3 * IdleRate(MachineType.Relay), store.DrawPerSecond, precision: 6);
        Assert.Equal(s.Base.Charge, store.Charge);
        Assert.Equal(s.Base.ChargeCapacity, store.Capacity);
    }

    [Fact]
    public void ACutOffRelay_KeepsItsCharge_AndDrainsByItsIdleDraw()
    {
        var s = Scenario(blockLineOfSight: true);
        var relay = RelayHeatTests.PlantRelay(s);
        int ticks = Tweaks.Perf.TargetFps * 10;

        Advance(s.World, ticks);

        var power = s.World.MachinePowerOf(relay)!.Value;
        Assert.False(power.IsFed);
        Assert.Equal(Tweaks.Machine.RelayCapacitor - Tweaks.Machine.RelayIdleDraw * ticks, power.Charge, precision: 6);
        Assert.Equal(0.0, power.InPerSecond);
        Assert.Equal(IdleRate(MachineType.Relay), power.OutPerSecond, precision: 6);
        Assert.True(relay.Charge >= power.Charge, "the settled level is never below the level now");
    }

    [Fact]
    public void ARelayBeyond64Hops_IsFedByItsBase()
    {
        const int hops = 65;
        int spacing = (int)(Tweaks.World.MaximumLiveLinkDistance * 0.8f);
        var terrain = new TerrainGrid(new Size(spacing * hops + 3, 3));
        terrain.Fill(TerrainPixel.Blank);
        var links = new LinkMap();
        var home = new TankBase(new Position(1, 1), 0);
        home.LinkPoint = links.RegisterPoint(home.Position, LinkPointType.Base);
        var network = new EnergyNetwork(SimulationSettings.FromTweaks());
        var machines = new MachineList();
        for (int hop = 1; hop <= hops; hop++)
        {
            var position = new Position(1 + spacing * hop, 1);
            var relay = machines.Add(new Machine(position, MachineType.Relay, home.Color));
            relay.State = MachineState.Planted;
            relay.OwnerBase = home;
            relay.LinkPoint = links.RegisterPoint(position, LinkPointType.Machine);
            network.Register(relay);
        }

        links.Advance(terrain, Tweaks.World.RefreshLinkMapInterval);
        network.Refresh(links, [home]);
        network.Advance(terrain);

        var last = machines.Machines[^1];
        Assert.Equal(hops, last.LinkPoint!.Hops);
        Assert.True(network.PowerOf(last)!.Value.IsFed);
    }

    [Fact]
    public void AHarvesterCoveredOnlyByARivalPoweredRelay_DrainsItsOwnCharge()
    {
        var size = new Size(430, 140);
        var generated = new TerrainGrid(size);
        generated.Fill(TerrainPixel.LevelGenDirt);
        var world = new World(size, rules: new MatchRules { EnergyNetwork = true });
        world.Initialize(generated, [new Position(60, 65), new Position(355, 65)]);
        world.Terrain.Fill(TerrainPixel.Blank);
        var home = world.TankBases.Bases[0];
        var relay = world.Machines.Add(new Machine(new Position(280, 65), MachineType.Relay, home.Color));
        world.PlantMachine(relay, home);
        var harvester = world.Machines.Add(new Machine(new Position(265, 65), MachineType.Harvester, home.Color));
        world.PlantMachine(harvester, home);
        double plantedCharge = harvester.Charge;

        Advance(world, Tweaks.Perf.TargetFps * 3);

        Assert.True(relay.LinkPoint!.IsPowered);
        Assert.False(world.MachinePowerOf(relay)!.Value.IsFed);
        Assert.True(harvester.IsOffNetwork);
        var power = world.MachinePowerOf(harvester)!.Value;
        Assert.False(power.IsFed);
        Assert.True(power.Charge < plantedCharge);
    }

    [Fact]
    public void AContestedFront_NodeFedAndCoveringHarvesterAgree()
    {
        // Relays at 140 and 220 hang off our base; the one at 290 has its own chain back, but its shortest route
        // (one hop) runs through the rival base at 355. Its power, its feed and the harvester's coverage must agree.
        var size = new Size(430, 140);
        var generated = new TerrainGrid(size);
        generated.Fill(TerrainPixel.LevelGenDirt);
        var world = new World(size, rules: new MatchRules { EnergyNetwork = true });
        world.Initialize(generated, [new Position(60, 65), new Position(355, 65)]);
        world.Terrain.Fill(TerrainPixel.Blank);
        var home = world.TankBases.Bases[0];
        var near = world.Machines.Add(new Machine(new Position(140, 65), MachineType.Relay, home.Color));
        world.PlantMachine(near, home);
        var middle = world.Machines.Add(new Machine(new Position(220, 65), MachineType.Relay, home.Color));
        world.PlantMachine(middle, home);
        var contested = world.Machines.Add(new Machine(new Position(290, 65), MachineType.Relay, home.Color));
        world.PlantMachine(contested, home);
        var harvester = world.Machines.Add(new Machine(new Position(295, 75), MachineType.Harvester, home.Color));
        world.PlantMachine(harvester, home);
        double plantedCharge = harvester.Charge;

        Advance(world, Tweaks.Perf.TargetFps * 3);

        Assert.True(world.MachinePowerOf(near)!.Value.IsFed, "a relay powered by its own base is fed");
        Assert.True(contested.LinkPoint!.IsPowered);
        Assert.False(world.MachinePowerOf(contested)!.Value.IsFed, "a relay whose route runs through the rival base is not fed by ours");
        Assert.True(harvester.IsOffNetwork, "the harvester's only cover is the rival-powered relay");
        Assert.False(world.MachinePowerOf(harvester)!.Value.IsFed);
        Assert.True(world.MachinePowerOf(harvester)!.Value.Charge < plantedCharge);
    }

    [Fact]
    public void ACutOffHarvester_DrainsFlat_AndThenWorksNoMore()
    {
        var s = Scenario(blockLineOfSight: true);
        HarvesterHeatTests.SeedOre(s);
        var harvester = Plant(s, MachineType.Harvester, s.RelayPos);
        harvester.Charge = Tweaks.Machine.HarvesterIdleDraw * 30;

        Advance(s.World, Tweaks.Perf.TargetFps * 3);

        Assert.Equal(0.0, harvester.Charge);
        Assert.Equal(0.0, s.World.MachinePowerOf(harvester)!.Value.Charge);
        Assert.Equal(HarvesterState.OutOfCharge, MachineBehaviors.HarvesterStateOf(harvester));
        Assert.Equal(0.0, s.World.MachinePowerOf(harvester)!.Value.OutPerSecond);
    }

    [Fact]
    public void ACoveredHarvester_IsToppedUpWhileItWorks_WithNoTripsToAPylon()
    {
        var s = Scenario();
        HarvesterHeatTests.SeedOre(s);
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * 3));
        var harvester = Plant(s, MachineType.Harvester, s.RelayPos);
        harvester.Charge = Tweaks.Machine.HarvesterBatteryCapacity * 0.1;
        int storeBefore = s.Base.Store.Ore;

        bool flowsSeen = false;
        for (int i = 0; i < Tweaks.Perf.TargetFps * 10; i++)
        {
            s.World.Advance(_ => default);
            Assert.False(harvester.IsReturning);
            var power = s.World.MachinePowerOf(harvester)!.Value;
            flowsSeen |= power.InPerSecond > IdleRate(MachineType.Harvester) && power.OutPerSecond > IdleRate(MachineType.Harvester);
        }

        Assert.True(s.Base.Store.Ore > storeBefore, "the harvester should have mined");
        Assert.True(harvester.Charge > Tweaks.Machine.HarvesterBatteryCapacity * 0.5, $"charge={harvester.Charge:0}");
        Assert.True(s.World.MachinePowerOf(harvester)!.Value.IsFed);
        Assert.True(flowsSeen, "the overlay should show charge coming in and going out while it works");
    }

    [Fact]
    public void AWreckedMachine_LeavesTheNetwork_EvenWhileItsCapacitorIsFilling()
    {
        var s = Scenario();
        HarvesterHeatTests.SeedOre(s);
        var relay = RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * 3));
        var harvester = Plant(s, MachineType.Harvester, s.RelayPos);
        harvester.Charge = Tweaks.Machine.HarvesterBatteryCapacity * 0.1;
        var energy = s.World.Energy!;
        for (int i = 0; i < Tweaks.Perf.TargetFps && energy.FillingCount == 0; i++)
            s.World.Advance(_ => default);
        Assert.True(energy.FillingCount > 0, "the harvester's capacitor should be filling");

        harvester.TakeDamage(Tweaks.Machine.ReactorHealthCapacity);
        Advance(s.World, Tweaks.Energy.SettlePeriodTicks + 2);

        Assert.Equal(1, energy.CapacitorCount);
        Assert.Equal(0, energy.FillingCount);
        Assert.Null(s.World.MachinePowerOf(harvester));
        Assert.True(s.World.MachinePowerOf(relay)!.Value.IsFed);
    }

    [Fact]
    public void AnEmptyStore_StarvesItsTier_UntilTheEngineFillsItAgain()
    {
        var s = Scenario();
        var relay = RelayHeatTests.PlantRelay(s);
        Advance(s.World, 12);
        Assert.True(s.World.MachinePowerOf(relay)!.Value.IsFed);

        s.Base.DrawCharge(s.Base.Charge);
        Advance(s.World, Tweaks.Perf.TargetFps);
        var starved = s.World.MachinePowerOf(relay)!.Value;
        Assert.False(starved.IsFed);
        Assert.True(starved.Charge < Tweaks.Machine.RelayCapacitor, "a starved machine drains its own capacitor");

        // Hot ground by the relay: the engine fills the store, the tier is fed again and the relay tops up.
        s.World.Terrain.AddHeatRadius(s.RelayPos, 150, 8);
        Advance(s.World, Tweaks.Perf.TargetFps * 3);
        var fed = s.World.MachinePowerOf(relay)!.Value;
        Assert.True(fed.IsFed);
        Assert.Equal((double)Tweaks.Machine.RelayCapacitor, fed.Charge, precision: 6);
        Assert.True(s.World.BasePowerOf(s.Base)!.Value.ProductionPerSecond > 0.0);
    }

    [Fact]
    public void ThermalEnergyPlusAllStoredCharge_BalancesEveryTick()
    {
        var s = Scenario();
        s.World.Terrain.AddHeatRadius(s.RelayPos, 150, 8);
        HarvesterHeatTests.SeedOre(s);
        Plant(s, MachineType.Pylon, PylonSpot(s));
        var harvester = Plant(s, MachineType.Harvester, s.RelayPos);
        harvester.Charge *= 0.3;
        var doomed = RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * 3));
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, -s.DirY * 6));
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(20, 0)); // in the rock beside the tunnel: cut off
        var ledger = s.World.Terrain.Ledger;

        double start = s.World.SumSystemThermalEnergy() + s.World.SumSystemCharge();
        double startExternal = ledger.Injected - ledger.TotalSunk;
        double maxResidual = 0.0;
        for (int i = 0; i < Tweaks.Perf.TargetFps * 20; i++)
        {
            if (i == Tweaks.Perf.TargetFps * 8)
                doomed.TakeDamage(Tweaks.Machine.ReactorHealthCapacity);
            s.World.Advance(_ => default);
            double now = s.World.SumSystemThermalEnergy() + s.World.SumSystemCharge();
            double residual = (now - start) - (ledger.Injected - ledger.TotalSunk - startExternal);
            maxResidual = Math.Max(maxResidual, Math.Abs(residual));
        }

        Assert.False(doomed.IsAlive);
        Assert.True(ledger.ConvertedToCharge != 0.0, "the loop should have converted energy");
        Assert.True(maxResidual < 1.0, $"heat and charge must add up. residual={maxResidual:0.0000}");
    }

    /// <summary>
    /// The guard of DESIGN.md §4.9: powered machines on ambient ground, with nothing hot to tap, only spend charge.
    /// Whatever the relays win back from the harvester's work heat is less than the work cost, so the network as a whole
    /// never holds more charge than it started with.
    /// </summary>
    [Fact]
    public void AClosedLoopOnAmbientGround_NeverGainsCharge()
    {
        var s = Scenario();
        HarvesterHeatTests.SeedOre(s);
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * 3));
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, -s.DirY * 3));
        Plant(s, MachineType.Pylon, PylonSpot(s));
        Plant(s, MachineType.Harvester, s.RelayPos);
        int storeBefore = s.Base.Store.Ore;

        double start = s.World.SumSystemCharge();
        double peak = start;
        for (int i = 0; i < Tweaks.Perf.TargetFps * 30; i++)
        {
            s.World.Advance(_ => default);
            peak = Math.Max(peak, s.World.SumSystemCharge());
        }

        Assert.True(s.Base.Store.Ore > storeBefore, "the harvester should have worked, so there was heat to win back");
        Assert.True(peak <= start + 1e-6, $"charge grew from {start:0.000} to {peak:0.000}");
        Assert.True(s.World.SumSystemCharge() < start, "work and idle draw spend charge");
    }

    [Fact]
    public void AWorldOnTheNetwork_SavedMidway_PlaysOnAlike()
    {
        var s = Scenario();
        s.World.Terrain.AddHeatRadius(s.RelayPos, 150, 8);
        HarvesterHeatTests.SeedOre(s);
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * 3));
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(20, 0));
        var harvester = Plant(s, MachineType.Harvester, s.RelayPos);
        harvester.Charge *= 0.2;
        Run(s.World, [null, null], 0, 150, (_, _) => default);

        AssertPlaysOnAlike(s.World, [null, null], () => Scenario().World, ticks: 150, (_, _) => default, firstFrame: 150);
    }

    [Fact]
    public void TheFlag_ChangesTheRulesFingerprint_OnlyWhenOn()
    {
        var off = new Emberward.Core.Match.MatchRules();
        Assert.Equal(off.Fingerprint(), (off with { EnergyNetwork = false }).Fingerprint());
        Assert.NotEqual(off.Fingerprint(), (off with { EnergyNetwork = true }).Fingerprint());
    }
}
