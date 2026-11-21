using Emberward.Core;
using Emberward.Core.Bots;
using Emberward.Core.Config;
using Emberward.Core.Entities.Bases;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Input;
using Emberward.Core.Types;
using static Emberward.Tests.SnapshotRoundTrip;

namespace Emberward.Tests;

/// <summary>
/// Per-machine upgrades (DESIGN.md §4.9, <see cref="MachineUpgradeSystem"/>): a tank next to its relay buys Tap, Sensor
/// or Conduit from its base's ore store, two levels on one path, and each path changes what it should.
/// </summary>
public class MachineUpgradeTests
{
    private static readonly int[] Costs = [.. Tweaks.MachineUpgrades.LevelCosts];

    /// <summary>A relay planted in the scenario's tunnel, the tank 5 cells from it, and the base store holding <paramref name="ore"/>.</summary>
    private static (RelayHeatTests.Scenario S, Machine Relay) Setup(bool energyNetwork = false, int ore = 100)
    {
        var s = RelayHeatTests.CreateScenario(energyNetwork: energyNetwork, machineUpgrades: true);
        var relay = RelayHeatTests.PlantRelay(s);
        s.Tank.Position = s.RelayPos + new Offset(0, -s.DirY * 5);
        s.Base.Store.Ore = ore;
        return (s, relay);
    }

    private static ControllerOutput Key(int path) => new() { UpgradeMachine = true, Upgrade = path };

    /// <summary>Presses machine upgrade key <paramref name="path"/> for one tick, then releases it for one.</summary>
    private static StepEvent[] Press(World world, int path)
    {
        world.Advance(i => i == 0 ? Key(path) : default);
        var events = world.StepEvents.ToArray();
        world.Advance(_ => default);
        return events;
    }

    private static void Advance(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++)
            world.Advance(_ => default);
    }

    [Fact]
    public void WithTheFlagOff_TheKeyBuysNothing_AndThereAreNoAnswers()
    {
        var s = RelayHeatTests.CreateScenario();
        var relay = RelayHeatTests.PlantRelay(s);
        s.Tank.Position = s.RelayPos + new Offset(0, -s.DirY * 5);
        s.Base.Store.Ore = 100;

        var events = Press(s.World, 1);

        Assert.Null(s.World.MachineUpgradeOfferFor(s.Tank));
        Assert.Equal(default, s.World.MachineUpgradeOf(relay));
        Assert.Equal(100, s.Base.Store.Ore);
        Assert.DoesNotContain(events, e => e.Kind is StepEventKind.MachineUpgraded or StepEventKind.MachineUpgradeRefused);
    }

    [Fact]
    public void TheMachineKey_BuysNoBaseUpgrade_EvenDocked()
    {
        var s = RelayHeatTests.CreateScenario();
        s.Tank.Position = s.Base.SpawnPosition;
        s.Base.Store.Ore = 500;
        Assert.True(s.Tank.IsDockedAtOwnBase);

        Press(s.World, 1);

        Assert.Equal(0, s.Base.Upgrades.Level((BaseUpgrade)0));
        Assert.Equal(500, s.Base.Store.Ore);
    }

    [Fact]
    public void BuyingTwoLevels_PaysFromTheBaseStore_OncePerPress()
    {
        var (s, relay) = Setup();
        var offer = s.World.MachineUpgradeOfferFor(s.Tank)!.Value;
        Assert.Same(relay, offer.Machine);
        Assert.Equal(Costs[0], offer.NextCost);
        Assert.True(offer.CanAfford);

        // Held for many ticks, the key still buys once.
        for (int i = 0; i < 10; i++)
            s.World.Advance(t => t == 0 ? Key(1) : default);
        Assert.Equal(new MachineUpgradeLevel(MachineUpgradePath.Tap, 1), s.World.MachineUpgradeOf(relay));
        Assert.Equal(100 - Costs[0], s.Base.Store.Ore);
        s.World.Advance(_ => default);

        var events = Press(s.World, 1);
        Assert.Equal(new MachineUpgradeLevel(MachineUpgradePath.Tap, 2), s.World.MachineUpgradeOf(relay));
        Assert.Equal(100 - Costs[0] - Costs[1], s.Base.Store.Ore);
        var bought = Assert.Single(events, e => e.Kind == StepEventKind.MachineUpgraded);
        Assert.Equal((relay.Position, s.Tank.Color, MachineType.Relay, 2), (bought.Position, bought.Color, bought.Machine, bought.Count));
        Assert.True(s.World.MachineUpgradeOfferFor(s.Tank)!.Value.IsMaxed);
    }

    [Fact]
    public void APressIsRefused_WhenMaxed_WhenTheStoreIsShort_OrOutOfReach()
    {
        var (s, relay) = Setup(ore: Costs[0]);
        Press(s.World, 1);
        Assert.Equal(1, s.World.MachineUpgradeOf(relay).Level);
        Assert.Equal(0, s.Base.Store.Ore);

        var events = Press(s.World, 1);
        var refused = Assert.Single(events, e => e.Kind == StepEventKind.MachineUpgradeRefused);
        Assert.Equal(MachineUpgradeRefusal.NotEnoughOre, (MachineUpgradeRefusal)refused.Count);
        Assert.Equal(MachineUpgradeRefusal.NotEnoughOre, s.World.MachineUpgradeRefusalOf(s.Tank).Refusal);
        Assert.Equal(1, s.World.MachineUpgradeOf(relay).Level);

        s.Base.Store.Ore = 100;
        Press(s.World, 1);
        Press(s.World, 1);
        Assert.Equal(MachineUpgradeRefusal.Maxed, s.World.MachineUpgradeRefusalOf(s.Tank).Refusal);
        Assert.Equal(100 - Costs[1], s.Base.Store.Ore);

        s.Tank.Position = s.RelayPos + new Offset(0, -s.DirY * (Tweaks.MachineUpgrades.Reach + 1));
        Assert.Null(s.World.MachineUpgradeOfferFor(s.Tank));
        Press(s.World, 2);
        Assert.Equal(MachineUpgradeRefusal.OutOfReach, s.World.MachineUpgradeRefusalOf(s.Tank).Refusal);

        // A refusal is said for a while, then forgotten.
        Advance(s.World, Tweaks.MachineUpgrades.RefusalTicks);
        Assert.Equal((MachineUpgradeRefusal.None, 0), s.World.MachineUpgradeRefusalOf(s.Tank));
    }

    [Fact]
    public void TheFirstPurchase_FixesThePath_AndConduitNeedsTheEnergyNetwork()
    {
        var (s, relay) = Setup();
        Press(s.World, 3);
        Assert.Equal(MachineUpgradeRefusal.Unavailable, s.World.MachineUpgradeRefusalOf(s.Tank).Refusal);
        Assert.Equal(default, s.World.MachineUpgradeOf(relay));

        Press(s.World, 2);
        Press(s.World, 1);

        Assert.Equal(MachineUpgradeRefusal.OtherPath, s.World.MachineUpgradeRefusalOf(s.Tank).Refusal);
        Assert.Equal(new MachineUpgradeLevel(MachineUpgradePath.Sensor, 1), s.World.MachineUpgradeOf(relay));
        Assert.Equal(100 - Costs[0], s.Base.Store.Ore);
    }

    [Fact]
    public void TheNearestOwnMachine_IsChosen_AndATieGoesToTheEarliestBuilt()
    {
        var s = RelayHeatTests.CreateScenario(machineUpgrades: true);
        s.Tank.Position = s.RelayPos;
        var first = RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, 6));
        var second = RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, -6));
        Assert.Same(first, s.World.MachineUpgradeOfferFor(s.Tank)!.Value.Machine);

        // An enemy relay closer still is not the tank's to upgrade, and neither is a harvester (no upgrade paths).
        var enemyBase = s.World.TankBases.Bases.Single(b => b != s.Base);
        var enemy = new Machine(s.RelayPos + new Offset(0, 2), MachineType.Relay, enemyBase.Color);
        s.World.Machines.Add(enemy);
        s.World.PlantMachine(enemy, enemyBase);
        var harvester = new Machine(s.RelayPos + new Offset(1, 0), MachineType.Harvester, s.Tank.Color);
        s.World.Machines.Add(harvester);
        s.World.PlantMachine(harvester, s.Base);
        Assert.Same(first, s.World.MachineUpgradeOfferFor(s.Tank)!.Value.Machine);

        var nearer = RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, -3));
        Assert.Same(nearer, s.World.MachineUpgradeOfferFor(s.Tank)!.Value.Machine);
        Assert.NotSame(second, s.World.MachineUpgradeOfferFor(s.Tank)!.Value.Machine);
    }

    [Fact]
    public void Tap_PumpsMoreHeat()
    {
        double Pumped(bool tap)
        {
            var (s, relay) = Setup();
            Advance(s.World, 30);
            Assert.True(relay.LinkPoint!.IsPowered);
            if (tap)
                Press(s.World, 1);
            s.World.Terrain.AddHeatRadius(s.RelayPos, 200, Tweaks.Machine.RelayCoolingRadius);
            s.World.Advance(_ => default);
            return relay.LastPumpedEnergy;
        }

        double plain = Pumped(tap: false), tapped = Pumped(tap: true);
        Assert.True(plain > 0.0);
        Assert.Equal(plain * (1.0 + Tweaks.MachineUpgrades.TapPumpPerLevel), tapped, precision: 6);
    }

    [Fact]
    public void Tap_DrawsMoreIdleCharge_AndHeatPlusChargeStillBalance()
    {
        var (s, relay) = Setup(energyNetwork: true);
        var ledger = s.World.Terrain.Ledger;
        double start = s.World.SumSystemThermalEnergy() + s.World.SumSystemCharge();
        double startExternal = ledger.Injected - ledger.TotalSunk;
        double maxResidual = 0.0;
        void Check()
        {
            double now = s.World.SumSystemThermalEnergy() + s.World.SumSystemCharge();
            maxResidual = Math.Max(maxResidual, Math.Abs((now - start) - (ledger.Injected - ledger.TotalSunk - startExternal)));
        }

        Press(s.World, 1);
        Check();
        for (int i = 0; i < Tweaks.Perf.TargetFps * 12; i++)
        {
            s.World.Advance(_ => default);
            Check();
        }

        double idle = Tweaks.Machine.RelayIdleDraw + Tweaks.MachineUpgrades.TapIdleDrawPerLevel;
        var power = s.World.MachinePowerOf(relay)!.Value;
        Assert.True(power.IsFed);
        Assert.Equal(idle * Tweaks.Perf.TargetFps, power.OutPerSecond, precision: 6);
        Assert.Equal(idle * Tweaks.Perf.TargetFps, s.World.BasePowerOf(s.Base)!.Value.DrawPerSecond, precision: 6);
        Assert.True(maxResidual < 1.0, $"heat and charge must add up. residual={maxResidual:0.0000}");
    }

    [Fact]
    public void Conduit_GrowsTheCapacitor_WhichFillsFromTheIsland_AndRaisesWhatTheRelayPassesOn()
    {
        var (s, relay) = Setup(energyNetwork: true);
        Advance(s.World, Tweaks.Perf.TargetFps);
        double limit = Tweaks.Throughput.NodeLimitPerTick * Tweaks.Perf.TargetFps;
        Assert.Equal(limit, s.World.MachinePowerOf(relay)!.Value.LimitPerSecond, precision: 6);

        Press(s.World, 3);
        Assert.Equal(Tweaks.Machine.RelayCapacitor * 1.5, relay.ChargeCapacity, precision: 6);
        Assert.Equal(limit * 1.5, s.World.MachinePowerOf(relay)!.Value.LimitPerSecond, precision: 6);
        Advance(s.World, Tweaks.Perf.TargetFps * 2);
        Assert.Equal(Tweaks.Machine.RelayCapacitor * 1.5, s.World.MachinePowerOf(relay)!.Value.Charge, precision: 6);

        Press(s.World, 3);
        Advance(s.World, Tweaks.Perf.TargetFps * 3);
        var power = s.World.MachinePowerOf(relay)!.Value;
        Assert.Equal(Tweaks.Machine.RelayCapacitor * 2.0, power.Capacity, precision: 6);
        Assert.Equal(Tweaks.Machine.RelayCapacitor * 2.0, power.Charge, precision: 6);
        Assert.Equal(limit * 2.0, power.LimitPerSecond, precision: 6);
    }

    [Fact]
    public void Sensor_WidensTheRelaysSight()
    {
        var (s, relay) = Setup();
        var vision = s.World.TrackVision(s.Tank);
        int sight = s.Base.Upgrades.NodeSightRadius;
        // Sideways toward the farther side of the map: the base backs onto a border, which may be beside it.
        int dirX = s.World.Terrain.Width - relay.Position.X >= relay.Position.X ? 1 : -1;
        var beyond = relay.Position + new Offset(dirX * (sight + 4), 0);
        Advance(s.World, 2);
        Assert.False(vision.IsVisible(beyond), "the cell must lie beyond the relay's sight before the upgrade");

        Press(s.World, 2);

        Assert.True(vision.IsVisible(beyond));
    }

    [Fact]
    public void AWreckedMachine_LosesItsUpgrade()
    {
        var (s, relay) = Setup();
        Press(s.World, 1);
        Assert.Equal(1, s.World.MachineUpgradeOf(relay).Level);

        relay.TakeDamage(Tweaks.Machine.ReactorHealthCapacity);
        s.World.Advance(_ => default);

        Assert.Equal(default, s.World.MachineUpgradeOf(relay));
        Assert.Null(s.World.MachineUpgradeOfferFor(s.Tank));
    }

    [Fact]
    public void ASnapshotWithUpgradesAndAHeldRefusedKey_PlaysOnAlike()
    {
        var (s, relay) = Setup(energyNetwork: true);
        BotTankAI?[] noBots = [null, null];
        // Tap bought at frame 5; from frame 30 the Sensor key is held (refused: the path is Tap) across the load at 40;
        // the second Tap level is bought after it.
        Script script = (frame, tank) => tank != 0 ? default
            : frame is >= 5 and < 8 ? Key(1)
            : frame is >= 30 and < 60 ? Key(2)
            : frame is >= 70 and < 72 ? Key(1)
            : default;
        Run(s.World, noBots, 0, 40, script);
        Assert.Equal(new MachineUpgradeLevel(MachineUpgradePath.Tap, 1), s.World.MachineUpgradeOf(relay));
        Assert.Equal(MachineUpgradeRefusal.OtherPath, s.World.MachineUpgradeRefusalOf(s.Tank).Refusal);

        var loaded = AssertPlaysOnAlike(s.World, noBots,
            () => RelayHeatTests.CreateScenario(energyNetwork: true, machineUpgrades: true).World, ticks: 50, script, firstFrame: 40);

        var loadedRelay = loaded.Machines.Machines.Single(m => m.Type == MachineType.Relay);
        Assert.Equal(new MachineUpgradeLevel(MachineUpgradePath.Tap, 2), loaded.MachineUpgradeOf(loadedRelay));
        Assert.Equal(Tweaks.Machine.RelayCapacitor, loadedRelay.ChargeCapacity, precision: 6);
    }
}
