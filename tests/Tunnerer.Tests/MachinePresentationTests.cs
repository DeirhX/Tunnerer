using Tunnerer.Core.Entities.Machines;
using Tunnerer.Desktop.Presentation;

namespace Tunnerer.Tests;

/// <summary>Every machine can be built from the keyboard and shown in the build panel.</summary>
public class MachinePresentationTests
{
    // The build panel's top row has three slots.
    private const int BuildSlotCount = 3;

    [Fact]
    public void EveryMachineType_HasAPresentation()
    {
        foreach (var type in Enum.GetValues<MachineType>())
            Assert.Equal(type, MachinePresentations.Get(type).Type);
        Assert.Equal(Enum.GetValues<MachineType>().Length, MachinePresentations.All.Count);
    }

    [Fact]
    public void KeysAndSlots_AreDistinct_AndFitThePanel()
    {
        var all = MachinePresentations.All;
        Assert.Equal(all.Count, all.Select(p => p.Key).Distinct().Count());
        Assert.Equal(all.Count, all.Select(p => p.BuildSlot).Distinct().Count());
        Assert.All(all, p => Assert.InRange(p.BuildSlot, 0, BuildSlotCount - 1));
        Assert.Equal(all.OrderBy(p => p.BuildSlot), MachinePresentations.InSlotOrder);
    }

    [Fact]
    public void KeyLabel_NamesTheBoundKey()
    {
        foreach (var p in MachinePresentations.All)
            Assert.Equal(p.Key.ToString(), p.KeyLabel);
    }

    [Fact]
    public void CostAndBuildKey_ComeFromCore()
    {
        foreach (var p in MachinePresentations.All)
        {
            Assert.Equal(MachineBehaviors.Get(p.Type).BuildCost.Dirt, p.DirtCost);
            Assert.Same(MachineBehaviors.ForKey(p.BuildKey), p.Behavior);
        }
    }
}
