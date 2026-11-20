using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Projectiles;
using Emberward.Core.Types;

namespace Emberward.Tests;

public class ProjectileBehaviorTests
{
    [Fact]
    public void AllProjectileTypes_HaveBehavior()
    {
        foreach (var type in Enum.GetValues<ProjectileType>())
        {
            int idx = (int)type;
            Assert.True(idx < ProjectileList.Behaviors.Length,
                $"Behavior array too small for {type} (index {idx})");
            Assert.NotNull(ProjectileList.Behaviors[idx]);
        }
    }

    [Fact]
    public void AllBehaviors_HaveAdvanceDelegate()
    {
        foreach (var type in Enum.GetValues<ProjectileType>())
        {
            var behavior = ProjectileList.Behaviors[(int)type];
            Assert.NotNull(behavior.Advance);
        }
    }

    [Fact]
    public void AllBehaviors_HaveNonDefaultDrawColor()
    {
        foreach (var type in Enum.GetValues<ProjectileType>())
        {
            var behavior = ProjectileList.Behaviors[(int)type];
            Assert.NotEqual(default, behavior.DrawColor);
        }
    }

    [Fact]
    public void Bullet_DrawColor_IsFireHot()
    {
        var behavior = ProjectileList.Behaviors[(int)ProjectileType.Bullet];
        Assert.Equal(Tweaks.Colors.FireHot, behavior.DrawColor);
    }

    [Fact]
    public void BehaviorArray_HasNoNullGaps()
    {
        for (int i = 0; i < ProjectileList.Behaviors.Length; i++)
            Assert.NotNull(ProjectileList.Behaviors[i]);
    }
}

public class MachineBehaviorTests
{
    [Fact]
    public void AllMachineTypes_HaveBehavior()
    {
        foreach (var type in Enum.GetValues<MachineType>())
        {
            var behavior = MachineBehaviors.Get(type);
            Assert.NotNull(behavior);
        }
    }

    [Fact]
    public void Get_InvalidType_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MachineBehaviors.Get((MachineType)999));
    }

    [Fact]
    public void Harvester_HasPositiveActionInterval()
    {
        Assert.True(MachineBehaviors.Harvester.ActionInterval > TimeSpan.Zero);
    }

    [Fact]
    public void Harvester_HasPerformAction()
    {
        Assert.NotNull(MachineBehaviors.Harvester.PerformAction);
    }

    [Fact]
    public void Harvester_BuildCost_IsThirtyOre()
    {
        Assert.Equal(Tweaks.Ore.HarvesterCost, MachineBehaviors.Harvester.OreCost);
        Assert.Equal(30, MachineBehaviors.Harvester.OreCost);
    }

    [Fact]
    public void Relay_BuildCost_IsFourOre()
    {
        Assert.Equal(Tweaks.Ore.RelayCost, MachineBehaviors.Relay.OreCost);
        Assert.Equal(4, MachineBehaviors.Relay.OreCost);
    }

    [Fact]
    public void Harvester_ActiveColor_IsGreen()
    {
        Assert.Equal(Tweaks.Colors.Harvester, MachineBehaviors.Harvester.ActiveColor);
    }

    [Fact]
    public void Machine_StoresBehavior_FromType()
    {
        var machine = new Machine(new Position(50, 50), MachineType.Harvester, 0);
        Assert.Same(MachineBehaviors.Harvester, machine.Behavior);

        var relay = new Machine(new Position(50, 50), MachineType.Relay, 0);
        Assert.Same(MachineBehaviors.Relay, relay.Behavior);
    }
}
