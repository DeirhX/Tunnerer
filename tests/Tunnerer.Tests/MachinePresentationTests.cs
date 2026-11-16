using Tunnerer.Core.Entities.Machines;
using Tunnerer.Desktop.Presentation;
using Tunnerer.Desktop.Rendering.Entities;

namespace Tunnerer.Tests;

/// <summary>Every machine can be built from the keyboard, shown in the build panel and drawn.</summary>
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

    [Fact]
    public void EveryMachine_HasASprite_DrawnWithAShaderShape()
    {
        var kinds = typeof(EntityShape).GetFields()
            .Where(f => f.Name.StartsWith("Kind", StringComparison.Ordinal))
            .Select(f => (float)f.GetValue(null)!)
            .ToHashSet();
        foreach (var p in MachinePresentations.All)
        {
            var sprite = p.Sprite;
            Assert.True(sprite is not null, $"{p.Type} needs a MachineSprite.");
            Assert.Contains(sprite.Kind, kinds);
            Assert.True(sprite.HalfLength > 0f && sprite.HalfWidth > 0f, $"{p.Type}'s sprite needs a size.");
            // An upright body shows a gauge in P1; a turning one uses P1 for its heading.
            Assert.True(sprite.FacesHeading == (sprite.Gauge is null), $"{p.Type}: set Gauge exactly when upright.");
            Assert.True(sprite.SenseRadius is null or > 0f);
            Assert.True(sprite.Reach is null || sprite.Reach.Radius > 0f);
        }
    }
}
