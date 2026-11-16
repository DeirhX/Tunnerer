using System.Numerics;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Links;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Gui;

namespace Tunnerer.Tests;

/// <summary>The HUD's decisions that do not need a window: which hint to show, the heat scale, the HUD's size.</summary>
public class HudLogicTests
{
    private static int Cost(MachineType type) => MachineBehaviors.Get(type).BuildCost.Dirt;

    /// <summary>A live tank out in the field with nothing to build and nothing going on.</summary>
    private static HintInputs Field(int dirt = 0) => new(
        IsDead: false, HasLeftBase: true, Docked: false, InsideBase: false, RelayJustLinked: false,
        PumpingRelays: 0, PlantRefused: false, Carried: null, CarriedLink: LinkPreviewStatus.NoNetwork,
        HeatStoreFull: false, Harvesters: 0, BuildableDirt: dirt, RelayFromBaseStore: false,
        BaseStoreDirt: 0, CoolingDown: false);

    [Fact]
    public void Hint_DeadTank_ShowsNothing()
    {
        var (text, tone) = HintSelector.Select(Field() with { IsDead = true, PlantRefused = true });
        Assert.Equal(string.Empty, text);
        Assert.Equal(HintTone.Dim, tone);
    }

    [Fact]
    public void Hint_BeforeLeavingBase_TeachesDriving()
    {
        var (text, tone) = HintSelector.Select(Field(dirt: 5000) with { HasLeftBase = false, Docked = true });
        Assert.StartsWith("WASD to drive", text);
        Assert.Equal(HintTone.Info, tone);
    }

    [Fact]
    public void Hint_RelayJustLinked_ReportsPumpingOrIdle_UnlessCarrying()
    {
        var linked = Field() with { RelayJustLinked = true };
        Assert.Equal("Relay linked - pumping heat into your base",
            HintSelector.Select(linked with { PumpingRelays = 1 }).Text);
        Assert.Equal("Relay linked - idle until the ground near it heats up", HintSelector.Select(linked).Text);
        Assert.DoesNotContain("linked -", HintSelector.Select(linked with { Carried = MachineType.Relay }).Text);
    }

    [Fact]
    public void Hint_RefusedPlant_IsBad()
    {
        var (text, tone) = HintSelector.Select(Field() with { PlantRefused = true, Carried = MachineType.Relay });
        Assert.StartsWith("Not planted", text);
        Assert.Equal(HintTone.Bad, tone);
    }

    [Theory]
    [InlineData(MachineType.Relay, "relay")]
    [InlineData(MachineType.Harvester, "harvester")]
    [InlineData(MachineType.Pylon, "pylon")]
    public void Hint_CarryingInsideBase_SaysLeaveTheBase(MachineType type, string name)
    {
        var (text, tone) = HintSelector.Select(Field() with { Carried = type, InsideBase = true });
        Assert.Equal($"Carrying {name} - leave the base to plant it", text);
        Assert.Equal(HintTone.Info, tone);
    }

    [Fact]
    public void Hint_CarriedHarvester_NeedsNoLink()
    {
        var (text, tone) = HintSelector.Select(Field() with { Carried = MachineType.Harvester });
        Assert.StartsWith("R: plant harvester here", text);
        Assert.Equal(HintTone.Good, tone);
    }

    [Theory]
    [InlineData(MachineType.Relay, LinkPreviewStatus.Live, "E: plant relay here - it will link (green line)", HintTone.Good)]
    [InlineData(MachineType.Pylon, LinkPreviewStatus.Live, "T: plant pylon here - it charges harvesters it can see", HintTone.Good)]
    [InlineData(MachineType.Relay, LinkPreviewStatus.Blocked, "Line to network blocked (red X) - dig it clear", HintTone.Bad)]
    [InlineData(MachineType.Pylon, LinkPreviewStatus.TooFar, "Too far from the network - move closer", HintTone.Warn)]
    [InlineData(MachineType.Relay, LinkPreviewStatus.NoNetwork, "No powered network to link to", HintTone.Warn)]
    public void Hint_CarriedLinkMachine_FollowsLinkPreview(
        MachineType type, LinkPreviewStatus link, string expected, HintTone expectedTone)
    {
        var (text, tone) = HintSelector.Select(Field() with { Carried = type, CarriedLink = link });
        Assert.Equal(expected, text);
        Assert.Equal(expectedTone, tone);
    }

    [Fact]
    public void Hint_FullHeatStore_Warns()
    {
        var rich = Field(dirt: 5000);
        Assert.StartsWith("Base heat store full", HintSelector.Select(rich with { HeatStoreFull = true }).Text);
        Assert.StartsWith("E: build relay", HintSelector.Select(rich).Text);
    }

    [Fact]
    public void Hint_BuildList_GrowsWithDirtAndHarvesters()
    {
        int relay = Cost(MachineType.Relay), harvester = Cost(MachineType.Harvester), pylon = Cost(MachineType.Pylon);
        Assert.Equal($"E: build relay ({relay} dirt)", HintSelector.Select(Field(dirt: relay)).Text);

        int rich = Math.Max(relay, Math.Max(harvester, pylon));
        string all = HintSelector.Select(Field(dirt: rich) with { Harvesters = 1 }).Text;
        Assert.Equal($"E: build relay ({relay} dirt)   R: build harvester ({harvester} dirt)   T: pylon ({pylon})", all);
        Assert.DoesNotContain("T: pylon", HintSelector.Select(Field(dirt: rich)).Text);
    }

    [Fact]
    public void Hint_RelayFromBaseStore_SaysSo()
    {
        int relay = Cost(MachineType.Relay);
        var docked = Field() with { Docked = true, BuildableDirt = relay, RelayFromBaseStore = true };
        Assert.Equal($"E: build relay from the base store ({relay} dirt)", HintSelector.Select(docked).Text);
    }

    [Fact]
    public void Hint_DockedAndCooling_SaysSo()
    {
        var (text, tone) = HintSelector.Select(Field() with { Docked = true, CoolingDown = true });
        Assert.Equal("Docked: cooling down and storing your dirt", text);
        Assert.Equal(HintTone.Info, tone);
    }

    [Fact]
    public void Hint_Fallback_SaysHowMuchToDig()
    {
        int relay = Cost(MachineType.Relay);
        Assert.Equal($"Dig {relay - 3} more dirt to build a relay", HintSelector.Select(Field(dirt: 3)).Text);
        Assert.Equal($"Dig {relay} more dirt, or build a relay at base from the store",
            HintSelector.Select(Field() with { BaseStoreDirt = relay }).Text);
    }

    [Fact]
    public void HeatScale_RunsFromFullCoolantToPastTheSafeMaximum()
    {
        Assert.Equal(0f, HeatScale.Fraction(-Tweaks.Tank.CoolantCapacity));
        Assert.Equal(1f, HeatScale.Fraction(Tweaks.Tank.HeatSafeMax * 1.25f));
        Assert.Equal(0f, HeatScale.Fraction(-10f * Tweaks.Tank.CoolantCapacity));
        Assert.Equal(1f, HeatScale.Fraction(10f * Tweaks.Tank.HeatSafeMax));
        Assert.True(HeatScale.Fraction(0f) > 0f && HeatScale.Fraction(0f) < HeatScale.Fraction(Tweaks.Tank.HeatSafeMax));
    }

    [Fact]
    public void HeatColor_IsBlueCold_GreenAtAmbient_RedPastSafe()
    {
        Assert.Equal(new Vector3(0.35f, 0.75f, 1.00f), HeatScale.ColorRgb(-Tweaks.Tank.CoolantCapacity));
        Assert.Equal(new Vector3(0.40f, 0.90f, 0.40f), HeatScale.ColorRgb(0f));
        Assert.Equal(new Vector3(1.00f, 0.15f, 0.10f), HeatScale.ColorRgb(Tweaks.Tank.HeatSafeMax));
        Assert.Equal(new Vector3(1.00f, 0.15f, 0.10f), HeatScale.ColorRgb(2f * Tweaks.Tank.HeatSafeMax));
        // Packed for ImGui as ABGR.
        Assert.Equal(0xFF66E566u, HeatScale.Color(0f));
    }

    [Theory]
    [InlineData(1920, 1080, 1f)]
    [InlineData(1280, 720, 1f)]
    [InlineData(3840, 2160, 2f)]
    [InlineData(512, 1080, 0.5f)]
    public void HudScale_EnlargesOnTallDisplays_ShrinksOnlyWhenNarrow(int w, int h, float expected) =>
        Assert.Equal(expected, HudLayout.HudScale(w, h));

    [Fact]
    public void BottomPanelHeight_IsTheStripAtHudScale()
    {
        Assert.Equal(223, HudLayout.BottomPanelHeight(1920, 1080));
        Assert.Equal(446, HudLayout.BottomPanelHeight(3840, 2160));
    }
}
