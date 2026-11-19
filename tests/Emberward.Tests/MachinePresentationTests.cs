using Emberward.Core.Entities.Machines;
using Emberward.Desktop.Audio;
using Emberward.Desktop.Input;
using Emberward.Desktop.Presentation;
using Emberward.Desktop.Rendering.Entities;

namespace Emberward.Tests;

/// <summary>Every machine can be built from the keyboard, shown in the build panel and drawn.</summary>
public class MachinePresentationTests
{
    // The build panel has two rows of three slots.
    private const int BuildSlotCount = 6;

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
        Assert.Equal(all.Count, all.Select(p => p.KeyLabel).Distinct().Count());
        Assert.Equal(all.Count, all.Select(p => p.BuildSlot).Distinct().Count());
        Assert.All(all, p => Assert.InRange(p.BuildSlot, 0, BuildSlotCount - 1));
        Assert.Equal(all.OrderBy(p => p.BuildSlot), MachinePresentations.InSlotOrder);
    }

    [Fact]
    public void EveryMachine_HasItsOwnBuildSound()
    {
        var sounds = Enum.GetValues<MachineType>().Select(StepEventSounds.BuildSound).ToArray();
        Assert.Equal(sounds.Length, sounds.Distinct().Count());
        Assert.All(sounds, s => Assert.False(SoundBank.IsLoop(s), $"{s} is a loop, not a build sound."));
    }

    [Fact]
    public void KeyLabel_NamesTheBoundKey()
    {
        foreach (var p in MachinePresentations.All)
            Assert.Equal(p.KeyLabel, KeyboardController.BuildKeys[(int)p.Type].ToString());
    }

    [Fact]
    public void CostAndBuildKey_ComeFromCore()
    {
        foreach (var p in MachinePresentations.All)
        {
            Assert.Equal(MachineBehaviors.Get(p.Type).OreCost, p.OreCost);
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
        Assert.Equal(Enum.GetValues<MachineType>().Length, MachineSprites.All.Count);
        foreach (var p in MachinePresentations.All)
        {
            var sprite = MachineSprites.Get(p.Type);
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
