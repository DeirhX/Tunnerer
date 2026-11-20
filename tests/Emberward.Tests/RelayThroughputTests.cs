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
/// Throughput on the energy network (DESIGN.md §4.9, <c>EnergyNetwork.Throughput</c>): a node passes on at most its
/// limit, so a chain is as fast as its slowest node; Conduit raises the limit; what a node passes on loses a share as heat
/// at the node; a hot node throttles, and hotter still loses health.
/// </summary>
public class RelayThroughputTests
{
    private static readonly double Limit = Tweaks.Throughput.NodeLimitPerTick;

    /// <summary>
    /// A base at the left end of a row of open ground, <paramref name="relays"/> relays in a line from it, and
    /// <paramref name="harvesters"/> empty harvesters taking their charge through the last relay, six cells off it. The
    /// relays hold no capacitor here (one is sized only when a world plants it), so they only pass charge on.
    /// </summary>
    private sealed class Chain
    {
        public readonly TerrainGrid Terrain;
        public readonly LinkMap Links = new();
        public readonly TankBase Home;
        public readonly EnergyNetwork Network = new(SimulationSettings.FromTweaks());
        public readonly MachineList Machines = new();
        public readonly List<Machine> Relays = new();
        public readonly List<Machine> Harvesters = new();
        public readonly StepEventLog Events = new();
        private readonly List<Machine> _near = new();

        public Chain(int relays, int harvesters)
        {
            int spacing = (int)(Tweaks.World.MaximumLiveLinkDistance * 0.8f);
            Terrain = new TerrainGrid(new Size(spacing * (relays + 1) + 3, 17));
            Terrain.Fill(TerrainPixel.Blank);
            Home = new TankBase(new Position(1, 8), 0);
            Home.LinkPoint = Links.RegisterPoint(Home.Position, LinkPointType.Base);
            for (int i = 1; i <= relays; i++)
            {
                var position = new Position(1 + spacing * i, 8);
                var relay = Machines.Add(new Machine(position, MachineType.Relay, Home.Color));
                relay.State = MachineState.Planted;
                relay.OwnerBase = Home;
                relay.LinkPoint = Links.RegisterPoint(position, LinkPointType.Machine);
                Network.Register(relay);
                Relays.Add(relay);
            }
            Links.Advance(Terrain, Tweaks.World.RefreshLinkMapInterval);
            Network.Refresh(Links, [Home]);

            var last = Relays[^1];
            for (int i = 0; i < harvesters; i++)
            {
                var harvester = Machines.Add(new Machine(last.Position + new Offset(0, i % 2 == 0 ? 6 : -6), MachineType.Harvester, Home.Color));
                harvester.State = MachineState.Planted;
                harvester.OwnerBase = Home;
                Network.Register(harvester);
                Network.SetCovered(harvester, new TankCoverage(true, 1f, last.Position, last.LinkPoint), Machines, Terrain, _near);
                Harvesters.Add(harvester);
            }
        }

        public void Advance(int ticks = 1)
        {
            for (int i = 0; i < ticks; i++)
                Network.Advance(Terrain, events: Events);
        }

        /// <summary>What each harvester took in one tick.</summary>
        public double[] TakenInOneTick()
        {
            var before = Harvesters.Select(h => h.Charge).ToArray();
            Advance();
            return Harvesters.Select((h, i) => h.Charge - before[i]).ToArray();
        }

        public double StoredEnergy() =>
            Home.Charge + Machines.Machines.Sum(m => m.Charge) + Network.StoredCharge() + Terrain.SumTotalThermalEnergy();

        /// <summary>Brings the ground a relay's heat check reads (the square of <c>Energy.IdleHeatRadius</c>) to <paramref name="degrees"/>.</summary>
        public void SetGround(Machine relay, float degrees)
        {
            int r = Tweaks.Energy.IdleHeatRadius;
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    var cell = relay.Position + new Offset(dx, dy);
                    Terrain.AddHeat(cell, (int)MathF.Round(degrees - Terrain.GetHeatTemperature(cell)));
                }
        }
    }

    [Fact]
    public void OneHarvesterBehindAChain_RefillsAtItsOwnRate_LessNothing()
    {
        var chain = new Chain(relays: 3, harvesters: 1);

        double taken = chain.TakenInOneTick().Single();

        Assert.Equal(Tweaks.Machine.HarvesterTransferPerTick, taken, precision: 9);
        var first = chain.Network.PowerOf(chain.Relays[0])!.Value;
        Assert.Equal(Limit * Tweaks.Perf.TargetFps, first.LimitPerSecond, precision: 9);
    }

    [Fact]
    public void AChain_IsAsFastAsItsSlowestNode()
    {
        var chain = new Chain(relays: 3, harvesters: 2);

        var taken = chain.TakenInOneTick();

        // Two harvesters ask 800 a tick; each relay passes on 400, split evenly.
        Assert.All(taken, t => Assert.Equal(Limit / 2, t, precision: 9));

        // Conduit on every node but the middle one: the middle one still holds the chain back.
        foreach (var relay in chain.Relays)
            chain.Network.SetThroughputScale(relay, 2.0);
        chain.Network.SetThroughputScale(chain.Relays[1], 1.0);
        Assert.All(chain.TakenInOneTick(), t => Assert.Equal(Limit / 2, t, precision: 9));

        chain.Network.SetThroughputScale(chain.Relays[1], 1.5);
        Assert.All(chain.TakenInOneTick(), t => Assert.Equal(Limit * 1.5 / 2, t, precision: 9));
    }

    [Fact]
    public void ConduitOnOneRelay_ChangesNothing_UntilTheWholeTrunkIsUpgraded()
    {
        var chain = new Chain(relays: 3, harvesters: 2);
        var before = chain.TakenInOneTick();

        chain.Network.SetThroughputScale(chain.Relays[^1], 2.0);
        Assert.Equal(before, chain.TakenInOneTick());
        chain.Network.SetThroughputScale(chain.Relays[1], 2.0);
        Assert.Equal(before, chain.TakenInOneTick());

        chain.Network.SetThroughputScale(chain.Relays[0], 2.0);
        Assert.All(chain.TakenInOneTick(), t => Assert.Equal(Tweaks.Machine.HarvesterTransferPerTick, t, precision: 9));
    }

    [Fact]
    public void Conduit_IsCappedByTheCeiling()
    {
        var chain = new Chain(relays: 1, harvesters: 0);

        chain.Network.SetThroughputScale(chain.Relays[0], 100.0);

        Assert.Equal(Tweaks.Throughput.CeilingPerTick * Tweaks.Perf.TargetFps,
            chain.Network.PowerOf(chain.Relays[0])!.Value.LimitPerSecond, precision: 9);
    }

    [Fact]
    public void TheConduitUpgrade_RaisesTheLimit_ByItsScale()
    {
        Assert.Equal(1.0, MachineUpgrades.ThroughputScaleOf(default));
        Assert.Equal(1.5, MachineUpgrades.ThroughputScaleOf(new MachineUpgradeLevel(MachineUpgradePath.Conduit, 1)));
        Assert.Equal(2.0, MachineUpgrades.ThroughputScaleOf(new MachineUpgradeLevel(MachineUpgradePath.Conduit, 2)));
        Assert.Equal(1.0, MachineUpgrades.ThroughputScaleOf(new MachineUpgradeLevel(MachineUpgradePath.Tap, 2)));
    }

    [Fact]
    public void WhatARelayPassesOn_LosesAShareAsHeatAtTheRelay_AndNothingIsLost()
    {
        var chain = new Chain(relays: 2, harvesters: 1);
        var ledger = chain.Terrain.Ledger;
        double start = chain.StoredEnergy();
        double toChargeBefore = ledger.ConvertedToCharge;
        double storeBefore = chain.Home.Charge;

        int ticks = Tweaks.Energy.SettlePeriodTicks * 2;
        chain.Advance(ticks);

        // The base paid the idle draws, and for each unit that reached the harvester the losses of both relays.
        double idle = ticks * chain.Machines.Machines.Sum(m => m.Behavior.Capacitor.IdleDrawPerTick);
        double delivered = chain.Harvesters[0].Charge;
        double drawn = storeBefore - chain.Home.Charge - idle;
        double loss = Tweaks.Throughput.LossFraction;
        Assert.Equal(delivered * ((1 + loss) * (1 + loss)), drawn, precision: 3);
        Assert.Equal(start, chain.StoredEnergy(), precision: 3);
        Assert.True(ledger.ConvertedToCharge < toChargeBefore, "settling turned the loss into heat");

        // The heat lies at the relays, and the one nearer the base, which passed on more, lost more.
        float far = chain.Terrain.SampleAverageHeat(chain.Relays[1].Position, Tweaks.Energy.IdleHeatRadius);
        float near = chain.Terrain.SampleAverageHeat(chain.Relays[0].Position, Tweaks.Energy.IdleHeatRadius);
        Assert.True(far > 0f && near > far, $"near={near} far={far}");
        Assert.Equal(0f, chain.Terrain.SampleAverageHeat(new Position(chain.Relays[0].Position.X / 2, 8), 1));

        var power = chain.Network.PowerOf(chain.Relays[0])!.Value;
        Assert.True(power.PassedPerSecond > 0.0);
    }

    [Fact]
    public void AHotRelay_Throttles_RecoversBelowTheHysteresis_AndOverheatedLosesHealth()
    {
        // One relay, so that no other node's loss heat (which no heat step spreads here) runs it hot.
        var chain = new Chain(relays: 1, harvesters: 2);
        var hot = chain.Relays[0];
        int period = Tweaks.Energy.SettlePeriodTicks;
        chain.Advance(period);
        Assert.Equal(ThroughputHeat.Normal, chain.Network.PowerOf(hot)!.Value.Heat);

        float throttle = Tweaks.Throughput.ThrottleTemperature, hysteresis = Tweaks.Throughput.ThrottleHysteresis;
        chain.SetGround(hot, throttle + 5);
        chain.Advance(period);
        Assert.Equal(ThroughputHeat.Throttled, chain.Network.PowerOf(hot)!.Value.Heat);
        Assert.All(chain.TakenInOneTick(), t => Assert.Equal(Limit * Tweaks.Throughput.ThrottledScale / 2, t, precision: 9));

        // Just under the throttle temperature it stays throttled; under it by more than the hysteresis it recovers.
        chain.SetGround(hot, throttle - hysteresis + 5);
        chain.Advance(period);
        Assert.Equal(ThroughputHeat.Throttled, chain.Network.PowerOf(hot)!.Value.Heat);
        chain.SetGround(hot, throttle - hysteresis - 5);
        chain.Advance(period);
        Assert.Equal(ThroughputHeat.Normal, chain.Network.PowerOf(hot)!.Value.Heat);
        Assert.All(chain.TakenInOneTick(), t => Assert.Equal(Limit / 2, t, precision: 9));

        int health = hot.Reactor.Health;
        chain.Events.Clear();
        chain.SetGround(hot, Tweaks.Throughput.OverheatTemperature + 5);
        chain.Advance(period);
        Assert.Equal(ThroughputHeat.Overheated, chain.Network.PowerOf(hot)!.Value.Heat);
        Assert.Equal(health - Tweaks.Throughput.OverheatDamage, hot.Reactor.Health);
        var damaged = Assert.Single(chain.Events.Events.ToArray());
        Assert.Equal(StepEventKind.MachineDamaged, damaged.Kind);
        Assert.Equal(hot.Position, damaged.Position);
        Assert.All(chain.TakenInOneTick(), t => Assert.Equal(Limit * Tweaks.Throughput.OverheatedScale / 2, t, precision: 9));
    }

    [Fact]
    public void AnOverheatedRelayThatStopsCarrying_StaysHot_ButTakesNoDamage_UntilItsGroundCools()
    {
        var chain = new Chain(relays: 1, harvesters: 1);
        var hot = chain.Relays[0];
        int period = Tweaks.Energy.SettlePeriodTicks;
        chain.SetGround(hot, Tweaks.Throughput.OverheatTemperature + 5);
        chain.Advance(period);
        Assert.Equal(ThroughputHeat.Overheated, chain.Network.PowerOf(hot)!.Value.Heat);

        // The harvester fills up, so nothing passes through for a whole period: still hot, no further damage.
        while (chain.Network.FillingCount > 0)
            chain.Advance();
        chain.Advance(period);
        int health = hot.Reactor.Health;
        chain.SetGround(hot, Tweaks.Throughput.OverheatTemperature + 5);
        chain.Advance(period);
        Assert.Equal(ThroughputHeat.Overheated, chain.Network.PowerOf(hot)!.Value.Heat);
        Assert.Equal(health, hot.Reactor.Health);

        chain.SetGround(hot, 0f);
        chain.Advance(period);
        Assert.Equal(ThroughputHeat.Normal, chain.Network.PowerOf(hot)!.Value.Heat);
    }

    [Fact]
    public void AHotRelayBelowItsLimit_IsNeitherThrottledNorDamaged_UntilItIsSaturated()
    {
        // One harvester asks 400 a tick through a relay that passes on 800 (Conduit level 2): not saturated.
        var chain = new Chain(relays: 1, harvesters: 1);
        var relay = chain.Relays[0];
        chain.Network.SetThroughputScale(relay, 2.0);
        int period = Tweaks.Energy.SettlePeriodTicks;
        int health = relay.Reactor.Health;
        chain.SetGround(relay, Tweaks.Throughput.OverheatTemperature + 50);
        chain.Advance(period);
        Assert.True(chain.Network.PowerOf(relay)!.Value.PassedPerSecond > 0.0, "it carries charge");
        Assert.Equal(ThroughputHeat.Normal, chain.Network.PowerOf(relay)!.Value.Heat);
        Assert.Equal(health, relay.Reactor.Health);

        // Back to its plain limit, the same demand saturates it, and the same ground overheats it.
        chain.Network.SetThroughputScale(relay, 1.0);
        chain.SetGround(relay, Tweaks.Throughput.OverheatTemperature + 50);
        chain.Advance(period);
        Assert.Equal(ThroughputHeat.Overheated, chain.Network.PowerOf(relay)!.Value.Heat);
        Assert.Equal(health - Tweaks.Throughput.OverheatDamage, relay.Reactor.Health);
    }

    [Fact]
    public void AHotRelayThatCarriesNothing_IsNeitherThrottledNorDamaged()
    {
        var chain = new Chain(relays: 2, harvesters: 0);
        var relay = chain.Relays[0];
        int health = relay.Reactor.Health;

        chain.SetGround(relay, Tweaks.Throughput.OverheatTemperature + 50);
        chain.Advance(Tweaks.Energy.SettlePeriodTicks * 2);

        Assert.Equal(ThroughputHeat.Normal, chain.Network.PowerOf(relay)!.Value.Heat);
        Assert.Equal(health, relay.Reactor.Health);
    }

    [Fact]
    public void ANetworkAtRest_WorksOutNoTree()
    {
        var chain = new Chain(relays: 3, harvesters: 1);
        while (chain.Network.FillingCount > 0)
            chain.Advance();
        double passed = chain.Network.PowerOf(chain.Relays[0])!.Value.PassedPerSecond;

        chain.Advance(Tweaks.Energy.FlowWindowTicks * 2);

        Assert.True(passed > 0.0);
        Assert.Equal(0.0, chain.Network.PowerOf(chain.Relays[0])!.Value.PassedPerSecond);
        Assert.Equal(0, chain.Network.FillingCount);
    }

    private static World ChainWorld()
    {
        // Our base at x 60; relays at 140 and 220 hang off it in a line, and a harvester sits beside the far one.
        var size = new Size(430, 140);
        var generated = new TerrainGrid(size);
        generated.Fill(TerrainPixel.LevelGenDirt);
        var world = new World(size, rules: new MatchRules { EnergyNetwork = true, MachineUpgrades = true });
        world.Initialize(generated, [new Position(60, 65), new Position(355, 65)]);
        world.Terrain.Fill(TerrainPixel.Blank);
        var home = world.TankBases.Bases[0];
        foreach (int x in new[] { 140, 220 })
        {
            var relay = world.Machines.Add(new Machine(new Position(x, 65), MachineType.Relay, home.Color));
            world.PlantMachine(relay, home);
        }
        var harvester = world.Machines.Add(new Machine(new Position(225, 72), MachineType.Harvester, home.Color));
        world.PlantMachine(harvester, home);
        harvester.Charge *= 0.1;
        return world;
    }

    [Fact]
    public void InAWorld_ChargePassedOn_HeatsTheRelays_AndTheLedgerBalancesEveryTick()
    {
        var world = ChainWorld();
        var near = world.Machines.Machines[0];
        var ledger = world.Terrain.Ledger;
        double start = world.SumSystemThermalEnergy() + world.SumSystemCharge();
        double startExternal = ledger.Injected - ledger.TotalSunk;
        double maxResidual = 0.0, maxPassed = 0.0;
        for (int i = 0; i < Tweaks.Perf.TargetFps * 12; i++)
        {
            world.Advance(_ => default);
            double now = world.SumSystemThermalEnergy() + world.SumSystemCharge();
            maxResidual = Math.Max(maxResidual, Math.Abs((now - start) - (ledger.Injected - ledger.TotalSunk - startExternal)));
            maxPassed = Math.Max(maxPassed, world.MachinePowerOf(near)!.Value.PassedPerSecond);
        }

        Assert.True(maxPassed > 0.0, "the near relay should have passed charge on to the harvester");
        Assert.True(maxResidual < 1.0, $"heat and charge must add up. residual={maxResidual:0.0000}");
    }

    [Fact]
    public void AWorldWithChargeInFlight_SavedMidway_PlaysOnAlike()
    {
        var world = ChainWorld();
        Run(world, [null, null], 0, 40, (_, _) => default);

        AssertPlaysOnAlike(world, [null, null], ChainWorld, ticks: 150, (_, _) => default, firstFrame: 40);
    }
}
