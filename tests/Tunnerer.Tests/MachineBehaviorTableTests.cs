using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

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
}
