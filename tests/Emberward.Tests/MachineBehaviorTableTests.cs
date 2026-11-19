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
        Assert.Equal(MachineType.Pylon, MachineBehaviors.ForKey(BuildKey.Tertiary).Type);
        Assert.Equal(MachineType.Generator, MachineBehaviors.ForKey(BuildKey.Quaternary).Type);
    }

    [Fact]
    public void Traits_MatchTheGameRules()
    {
        Assert.True(MachineBehaviors.Harvester.HasBattery);
        Assert.False(MachineBehaviors.Harvester.IsLinkNode);
        Assert.False(MachineBehaviors.Harvester.RequiresLinkToPlant);

        foreach (var linked in new[] { MachineBehaviors.Relay, MachineBehaviors.Pylon })
        {
            Assert.False(linked.HasBattery);
            Assert.True(linked.IsLinkNode);
            Assert.True(linked.RequiresLinkToPlant);
        }

        Assert.Single(MachineBehaviors.All, b => b.IsCharger);
        Assert.True(MachineBehaviors.Pylon.IsCharger);
        Assert.Single(MachineBehaviors.All, b => b.IsMiner);
        Assert.True(MachineBehaviors.Harvester.IsMiner);
        var generator = Assert.Single(MachineBehaviors.All, b => b.IsGenerator);
        Assert.True(generator.IsLinkNode);
        Assert.False(generator.RequiresLinkToPlant);

        Assert.Single(MachineBehaviors.All, b => b.IsHeatPump);
        Assert.True(MachineBehaviors.Relay.IsHeatPump);

        // Only the relay takes per-machine upgrades so far, on the keys 1-3 in this order.
        Assert.Equal([MachineUpgradePath.Tap, MachineUpgradePath.Sensor, MachineUpgradePath.Conduit],
            MachineBehaviors.Relay.UpgradePaths.ToArray());
        Assert.Single(MachineBehaviors.All, b => !b.UpgradePaths.IsEmpty);
    }

    [Fact]
    public void A_Machine_TakesItsBatteryFromItsBehavior()
    {
        foreach (var type in Enum.GetValues<MachineType>())
        {
            var machine = new Machine(new Position(10, 10), type, 0);
            Assert.Equal(MachineBehaviors.Get(type).BatteryCapacity, machine.ChargeCapacity);
        }
        Assert.Equal((double)Tweaks.Machine.HarvesterBatteryCapacity, MachineBehaviors.Harvester.BatteryCapacity);
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
        // The harvester's battery is its capacitor, and only it runs on charge.
        Assert.Single(MachineBehaviors.All, b => b.RunsOnCharge);
        Assert.Equal(MachineBehaviors.Harvester.BatteryCapacity, MachineBehaviors.Harvester.Capacitor.Capacity);
        // Topped up, a working harvester keeps up: its transfer rate beats what work spends a tick.
        double workPerTick = Tweaks.Machine.HarvesterHeatPerHarvest / (Tweaks.Machine.HarvesterIntervalMs / 1000.0 * Tweaks.Perf.TargetFps);
        Assert.True(MachineBehaviors.Harvester.Capacitor.TransferPerTick > workPerTick);
    }
}
