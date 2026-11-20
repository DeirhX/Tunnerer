using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Input;
using Emberward.Core.Types;
using Emberward.Desktop.Gui;

namespace Emberward.Tests;

/// <summary>
/// The machine upgrade highlight and panel the desktop and the browser share (<see cref="MachineUpgradeView"/>,
/// <see cref="MachineUpgradePanel"/>): what they say, and when the panel closes.
/// </summary>
public class MachineUpgradeViewTests
{
    private static (RelayHeatTests.Scenario S, Machine Relay) Setup(int ore)
    {
        var s = RelayHeatTests.CreateScenario(machineUpgrades: true);
        var relay = RelayHeatTests.PlantRelay(s);
        s.Tank.Position = s.RelayPos + new Offset(0, -s.DirY * 5);
        s.Base.Store.Ore = ore;
        return (s, relay);
    }

    private static void Press(World world, int path)
    {
        world.Advance(i => i == 0 ? new ControllerOutput { UpgradeMachine = true, Upgrade = path } : default);
        world.Advance(_ => default);
    }

    [Fact]
    public void TheView_LightsTheMachine_AmberWhenAffordable_DimWhenNot_AndNotAtAllWhenMaxed()
    {
        var (s, relay) = Setup(ore: 100);
        var view = MachineUpgradeViews.For(s.World, s.Tank)!;
        Assert.Same(relay, view.Machine);
        Assert.Equal(MachineUpgradeHighlight.Affordable, view.Highlight);
        Assert.Equal(HudPalette.Amber, view.HighlightColor);
        Assert.Equal("RELAY", view.Title);
        Assert.Equal([1, 2, 3], view.Choices.Select(c => c.Key));
        Assert.Equal(
            [MachineUpgradeChoiceState.Open, MachineUpgradeChoiceState.Open, MachineUpgradeChoiceState.Unavailable],
            view.Choices.Select(c => c.State));
        Assert.Equal($"{Tweaks.MachineUpgrades.LevelCosts[0]} ore", view.Choices[0].Price);

        Press(s.World, 1);
        s.Base.Store.Ore = 0;
        view = MachineUpgradeViews.For(s.World, s.Tank)!;
        Assert.Equal(MachineUpgradeHighlight.TooDear, view.Highlight);
        Assert.Equal("RELAY - TAP", view.Title);
        Assert.Equal(1, view.Level);
        Assert.Equal(MachineUpgradeChoiceState.TooDear, view.Choices[0].State);
        Assert.Equal(MachineUpgradeChoiceState.Locked, view.Choices[1].State);

        s.Base.Store.Ore = 100;
        Press(s.World, 1);
        view = MachineUpgradeViews.For(s.World, s.Tank)!;
        Assert.Equal(MachineUpgradeHighlight.None, view.Highlight);
        Assert.Equal(0u, view.HighlightColor);
        Assert.Equal(MachineUpgradeChoiceState.Maxed, view.Choices[0].State);
        Assert.Equal("maxed", view.Choices[0].Price);
    }

    [Fact]
    public void TheView_SaysWhyAPressWasRefused()
    {
        var (s, _) = Setup(ore: 0);
        Press(s.World, 1);
        Assert.Equal(MachineUpgradeViews.RefusalText(MachineUpgradeRefusal.NotEnoughOre),
            MachineUpgradeViews.For(s.World, s.Tank)!.Refusal);
    }

    [Fact]
    public void ThePanel_OpensOnlyInReach_AndClosesOnTheKey_OutOfReach_OrAfterAPurchase()
    {
        var (s, _) = Setup(ore: 100);
        var panel = new MachineUpgradePanel();
        var far = s.RelayPos + new Offset(0, -s.DirY * (Tweaks.MachineUpgrades.Reach + 2));
        var near = s.Tank.Position;

        s.Tank.Position = far;
        panel.Toggle(s.World, s.Tank);
        Assert.False(panel.IsOpen);

        s.Tank.Position = near;
        panel.Toggle(s.World, s.Tank);
        Assert.True(panel.IsOpen);
        panel.Toggle(s.World, s.Tank);
        Assert.False(panel.IsOpen);

        panel.Toggle(s.World, s.Tank);
        s.Tank.Position = far;
        Assert.Null(panel.Update(s.World, s.Tank));
        Assert.False(panel.IsOpen);

        s.Tank.Position = near;
        panel.Toggle(s.World, s.Tank);
        Assert.NotNull(panel.Update(s.World, s.Tank));
        Assert.True(panel.IsOpen);
        Press(s.World, 2);
        panel.Update(s.World, s.Tank);
        Assert.False(panel.IsOpen);
    }

    [Fact]
    public void OnlyALitMachineOrAnOpenPanel_CatchesATap()
    {
        var (s, _) = Setup(ore: 100);
        Assert.True(MachineUpgradeViews.For(s.World, s.Tank)!.CatchesTaps(panelOpen: false));

        s.Base.Store.Ore = 0;
        Assert.True(MachineUpgradeViews.For(s.World, s.Tank)!.CatchesTaps(panelOpen: false), "dim is still lit");

        s.Base.Store.Ore = 100;
        Press(s.World, 1);
        Press(s.World, 1);
        var maxed = MachineUpgradeViews.For(s.World, s.Tank)!;
        Assert.False(maxed.CatchesTaps(panelOpen: false), "a maxed machine is not lit: a tap near it fires");
        Assert.True(maxed.CatchesTaps(panelOpen: true), "a tap closes an open panel");
    }

    [Fact]
    public void ThePips_SitUnderTheRing_AwayFromTheChargeLabelAbove()
    {
        // Positive is down. The charge label grows up from PowerLabels.LiftCells above the machine; pips there hid
        // its flow line (seen in the browser). Under the ring they cover neither.
        float pips = MachineUpgradeView.PipDropCells, ring = MachineUpgradeView.RingCells;
        Assert.True(pips > ring, $"pips at {pips} cells down must clear the ring ({ring})");
    }

    [Theory]
    [InlineData(400f, 300f, 400f)] // room above and below: level with the machine
    [InlineData(780f, 300f, 646f)] // near the bottom: lifted until its bottom is in view
    [InlineData(80f, 300f, 254f)] // near the top: lowered until its top is in view
    [InlineData(400f, 900f, 554f)] // taller than the view: its top stays in view
    public void ThePanel_StaysInsideTheView(float machineY, float panelHeight, float expected)
    {
        const float top = 100f, bottom = 800f;
        Assert.Equal(expected, MachineUpgradeView.PanelCentreY(machineY, panelHeight, top, bottom));
    }
}
