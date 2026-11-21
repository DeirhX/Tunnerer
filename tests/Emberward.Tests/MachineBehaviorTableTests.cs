using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Types;

namespace Emberward.Tests;

/// <summary>The machine table: one behavior per <see cref="MachineType"/>, and the traits Core reads from it.</summary>
public class MachineBehaviorTableTests
{
    [Fact]
    public void EveryMachineType_HasItsOwnBehavior()
    {
        foreach (var type in Enum.GetValues<MachineType>())
            Assert.Equal(type, MachineBehaviors.Get(type).Type);
        Assert.Equal(Enum.GetValues<MachineType>().Length, MachineBehaviors.All.Count);
    }

    [Fact]
    public void EveryBuildKey_BuildsOneMachine()
    {
        foreach (var key in Enum.GetValues<BuildKey>())
            Assert.Single(MachineBehaviors.All, b => b.Key == key);
        Assert.Equal(MachineType.Relay, MachineBehaviors.ForKey(BuildKey.Primary).Type);
        Assert.Equal(MachineType.Harvester, MachineBehaviors.ForKey(BuildKey.Secondary).Type);
        Assert.Equal(MachineType.Generator, MachineBehaviors.ForKey(BuildKey.Tertiary).Type);
        Assert.Equal(MachineType.Turret, MachineBehaviors.ForKey(BuildKey.Quaternary).Type);
        Assert.Equal(MachineType.Gatling, MachineBehaviors.ForKey(BuildKey.Quinary).Type);
    }

    [Fact]
    public void Traits_MatchTheGameRules()
    {
        Assert.True(MachineBehaviors.Harvester.RunsOnCharge);
        Assert.False(MachineBehaviors.Harvester.IsLinkNode);
        Assert.False(MachineBehaviors.Harvester.RequiresLinkToPlant);

        // The relay is the only node a tank must link to plant (DESIGN.md §4.9: the pylon is gone).
        Assert.Equal([MachineType.Relay], MachineBehaviors.All.Where(b => b.RequiresLinkToPlant).Select(b => b.Type).ToArray());
        Assert.Equal([MachineType.Relay, MachineType.Generator],
            MachineBehaviors.All.Where(b => b.IsLinkNode).Select(b => b.Type).ToArray());
        Assert.Single(MachineBehaviors.All, b => b.IsMiner);
        Assert.True(MachineBehaviors.Harvester.IsMiner);
        var generator = Assert.Single(MachineBehaviors.All, b => b.IsGenerator);
        Assert.True(generator.IsLinkNode);
        Assert.False(generator.RequiresLinkToPlant);

        Assert.Single(MachineBehaviors.All, b => b.IsHeatPump);
        Assert.True(MachineBehaviors.Relay.IsHeatPump);

        // A turret and a gatling are no link nodes: they join their island through coverage, as a harvester does, and
        // only on the network. The gatling is the one whose rounds are for creatures.
        Assert.Equal([MachineType.Turret, MachineType.Gatling],
            MachineBehaviors.All.Where(b => b.IsTurret).Select(b => b.Type).ToArray());
        Assert.All(MachineBehaviors.All.Where(b => b.IsTurret), turret =>
        {
            Assert.False(turret.IsLinkNode);
            Assert.False(turret.RequiresLinkToPlant);
            Assert.Equal(PowerTier.Turrets, turret.Capacitor.Tier);
        });
        Assert.Same(MachineBehaviors.Gatling, Assert.Single(MachineBehaviors.All, b => b.HuntsCreatures));
        Assert.Equal([MachineType.Generator, MachineType.Turret, MachineType.Gatling],
            MachineBehaviors.All.Where(b => b.NeedsEnergyNetwork).Select(b => b.Type).ToArray());

        // The relay, the turret and the gatling take per-machine upgrades, on the keys 1-3 in this order.
        Assert.Equal([MachineUpgradePath.Tap, MachineUpgradePath.Sensor, MachineUpgradePath.Conduit],
            MachineBehaviors.Relay.UpgradePaths.ToArray());
        Assert.Equal([MachineUpgradePath.Rapid, MachineUpgradePath.LongRange], MachineBehaviors.Turret.UpgradePaths.ToArray());
        Assert.Equal([MachineUpgradePath.Spin, MachineUpgradePath.Scope], MachineBehaviors.Gatling.UpgradePaths.ToArray());
        Assert.Equal([MachineType.Relay, MachineType.Turret, MachineType.Gatling],
            MachineBehaviors.All.Where(b => !b.UpgradePaths.IsEmpty).Select(b => b.Type).ToArray());
    }

    [Fact]
    public void A_Machine_IsBuiltWithoutACapacitor_UntilTheNetworkPlantsIt()
    {
        foreach (var type in Enum.GetValues<MachineType>())
        {
            var machine = new Machine(new Position(10, 10), type, 0);
            Assert.Equal(0.0, machine.ChargeCapacity);
        }
        Assert.Equal((double)Tweaks.Machine.HarvesterCapacitor, MachineBehaviors.Harvester.Capacitor.Capacity);
    }

    [Fact]
    public void EveryMachine_HasACapacitor_WithAnIdleDraw()
    {
        foreach (var behavior in MachineBehaviors.All)
        {
            var capacitor = behavior.Capacitor;
            Assert.True(capacitor.Capacity > 0.0, $"{behavior.Type} needs a capacity");
            Assert.True(capacitor.TransferPerTick > 0.0, $"{behavior.Type} needs a transfer rate");
            Assert.True(capacitor.IdleDrawPerTick > 0.0, $"{behavior.Type} needs an idle draw");
            Assert.True(Enum.IsDefined(capacitor.Tier), $"{behavior.Type} needs a tier");
            // Cut off, a full capacitor lasts at least a minute on its idle draw alone.
            Assert.True(capacitor.Capacity / capacitor.IdleDrawPerTick >= 60 * Tweaks.Perf.TargetFps, $"{behavior.Type} drains too fast");
        }
        // Only the harvester runs on charge.
        Assert.Single(MachineBehaviors.All, b => b.RunsOnCharge);
        // Topped up, a working harvester keeps up: its transfer rate beats what work spends a tick.
        double workPerTick = Tweaks.Machine.HarvesterHeatPerHarvest / (Tweaks.Machine.HarvesterIntervalMs / 1000.0 * Tweaks.Perf.TargetFps);
        Assert.True(MachineBehaviors.Harvester.Capacitor.TransferPerTick > workPerTick);
    }
}
