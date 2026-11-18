using System.Numerics;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Gui;
using Tunnerer.Desktop.Rendering;
using Tunnerer.Core.Network;
using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Desktop.Presentation;

namespace Tunnerer.Tests;

/// <summary>The HUD's decisions that do not need a window: which hint to show, the heat scale, the HUD's size.</summary>
public class HudLogicTests
{
    private static int Cost(MachineType type) => MachineBehaviors.Get(type).OreCost;

    /// <summary>A live tank out in the field with nothing to build and nothing going on.</summary>
    private static HintInputs Field(int ore = 0) => new(
        IsDead: false, HasLeftBase: true, Docked: false, InsideBase: false, RelayJustLinked: false,
        PumpingRelays: 0, PlantRefused: false, Carried: null, CarriedLink: LinkPreviewStatus.NoNetwork,
        HeatStoreFull: false, Harvesters: 0, SpendableOre: ore, RelayFromBaseStore: false,
        NotEnoughOre: false, CoolingDown: false);

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
        var (text, tone) = HintSelector.Select(Field(ore: 500) with { HasLeftBase = false, Docked = true });
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
        var rich = Field(ore: 500);
        Assert.StartsWith("Base heat store full", HintSelector.Select(rich with { HeatStoreFull = true }).Text);
        Assert.StartsWith("E: build relay", HintSelector.Select(rich).Text);
    }

    [Fact]
    public void Hint_BuildList_GrowsWithOreAndHarvesters()
    {
        int relay = Cost(MachineType.Relay), harvester = Cost(MachineType.Harvester), pylon = Cost(MachineType.Pylon);
        Assert.Equal($"E: build relay ({relay} ore)", HintSelector.Select(Field(ore: relay)).Text);

        int rich = Math.Max(relay, Math.Max(harvester, pylon));
        string all = HintSelector.Select(Field(ore: rich) with { Harvesters = 1 }).Text;
        Assert.Equal($"E: build relay ({relay} ore)   R: build harvester ({harvester} ore)   T: pylon ({pylon})", all);
        Assert.DoesNotContain("T: pylon", HintSelector.Select(Field(ore: rich)).Text);
    }

    [Fact]
    public void Hint_NotEnoughOre_IsBad_AndSaysWhereOreComesFrom()
    {
        var (text, tone) = HintSelector.Select(Field() with { NotEnoughOre = true });
        Assert.StartsWith("Not enough ore", text);
        Assert.Equal(HintTone.Bad, tone);
    }

    [Fact]
    public void Hint_RelayFromBaseStore_SaysSo()
    {
        int relay = Cost(MachineType.Relay);
        var docked = Field() with { Docked = true, SpendableOre = relay, RelayFromBaseStore = true };
        Assert.Equal($"E: build relay from the base store ({relay} ore)", HintSelector.Select(docked).Text);
    }

    [Fact]
    public void Hint_DockedAndCooling_SaysSo()
    {
        var (text, tone) = HintSelector.Select(Field() with { Docked = true, CoolingDown = true });
        Assert.Equal("Docked: cooling down and banking your ore", text);
        Assert.Equal(HintTone.Info, tone);
    }

    [Fact]
    public void Hint_Fallback_SaysHowMuchToMine()
    {
        int relay = Cost(MachineType.Relay);
        Assert.Equal($"Mine {relay - 3} more ore (teal crystal) to build a relay", HintSelector.Select(Field(ore: 3)).Text);
    }

    [Fact]
    public void HeatScale_FreezesTheOrbBelowAmbient_AndBoilsItTowardTheSafeMaximum()
    {
        Assert.Equal(0f, HeatScale.Frozen(0f));
        Assert.Equal(1f, HeatScale.Frozen(Tweaks.Tank.FrostbiteTemperature));
        Assert.Equal(0f, HeatScale.Boil(0.4f * Tweaks.Tank.HeatSafeMax));
        Assert.Equal(1f, HeatScale.Boil(Tweaks.Tank.HeatSafeMax), 5);
        Assert.Equal(HeatScale.Min, HeatScale.At(0f));
        Assert.Equal(HeatScale.Max, HeatScale.At(1f), 3);
    }

    [Theory]
    [InlineData(10, 4, true, null, BuildSlotState.Ready, "RELAY [E]", "4 ORE BASE")]
    [InlineData(1, 4, false, null, BuildSlotState.Filling, "ORE 1/4", "[E] RELAY")]
    [InlineData(10, 4, true, MachineType.Relay, BuildSlotState.Carrying, "PLANT [E]", null)]
    [InlineData(10, 4, true, MachineType.Harvester, BuildSlotState.Blocked, "RELAY [E]", "4 ORE BASE")]
    public void BuildSlotView_SaysWhatTheDesktopsSlotSays(int stock, int cost, bool afford, MachineType? carried,
        BuildSlotState state, string label, string? caption)
    {
        var quote = new BuildQuote(MachineType.Relay, cost, afford ? OrePayer.BaseStore : OrePayer.None, stock, InNetwork: true);
        var slot = BuildSlotView.Of(MachinePresentations.Relay, quote, carried);
        Assert.Equal((state, label, caption), (slot.State, slot.Label, slot.Caption));
        Assert.Equal(Math.Min(1f, stock / (float)cost), slot.Progress);
    }

    [Fact]
    public void HeatScale_RunsFromDeepFrostToPastTheSafeMaximum()
    {
        Assert.Equal(0f, HeatScale.Fraction(HeatScale.Min));
        Assert.Equal(1f, HeatScale.Fraction(Tweaks.Tank.HeatSafeMax * 1.25f));
        Assert.Equal(0f, HeatScale.Fraction(10f * HeatScale.Min));
        Assert.Equal(1f, HeatScale.Fraction(10f * Tweaks.Tank.HeatSafeMax));
        Assert.True(HeatScale.Fraction(0f) > 0f && HeatScale.Fraction(0f) < HeatScale.Fraction(Tweaks.Tank.HeatSafeMax));
    }

    [Fact]
    public void HeatColor_IsBlueCold_GreenAtAmbient_RedPastSafe()
    {
        Assert.Equal(new Vector3(0.35f, 0.75f, 1.00f), HeatScale.ColorRgb(HeatScale.Min));
        Assert.Equal(new Vector3(0.40f, 0.90f, 0.40f), HeatScale.ColorRgb(0f));
        Assert.Equal(new Vector3(1.00f, 0.15f, 0.10f), HeatScale.ColorRgb(Tweaks.Tank.HeatSafeMax));
        Assert.Equal(new Vector3(1.00f, 0.15f, 0.10f), HeatScale.ColorRgb(2f * Tweaks.Tank.HeatSafeMax));
        // Packed for ImGui as ABGR.
        Assert.Equal(0xFF66E566u, HudDraw.PackColor(HeatScale.ColorRgb(0f), 1f));
    }

    [Theory]
    [InlineData(1920, 1080, 1f)]
    [InlineData(3840, 2160, 2f)]
    public void HudScale_EnlargesOnTallDisplays_ShrinksOnlyWhenNarrow(int w, int h, float expected) =>
        Assert.Equal(expected, HudLayout.HudScale(w, h));

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(900, 1080)]
    public void HudScale_OnANarrowDisplay_KeepsTheGapBetweenTheParts(int w, int h)
    {
        float scale = HudLayout.HudScale(w, h);
        Assert.True(scale < 1f);
        Assert.Equal(w, (HudLayout.LeftW + HudLayout.RightW) * scale + HudLayout.MinCenterGap, 3);
    }

    [Theory]
    [InlineData(800)]
    [InlineData(404)]
    [InlineData(320)]
    [InlineData(1)]
    public void HudScale_StaysPositiveOnVeryNarrowDisplays_HalfTheWidthBeingTheGap(int w)
    {
        float scale = HudLayout.HudScale(w, 1080);
        Assert.True(scale > 0f);
        Assert.Equal(w * 0.5f, (HudLayout.LeftW + HudLayout.RightW) * scale, 3);
    }

    [Theory]
    [InlineData(10f, 1070f, true)]     // bottom-left: TEMP
    [InlineData(1910f, 1070f, true)]   // bottom-right: BUILD STRUCTURES
    [InlineData(960f, 1070f, false)]   // the gap in the middle is view
    [InlineData(10f, 800f, false)]     // above the strip
    public void Covers_IsTheTwoCornerParts_NotTheGapBetweenThem(float x, float y, bool expected) =>
        Assert.Equal(expected, HudLayout.Covers(1920, 1080, pixelDensity: 1f, x, y));

    [Fact]
    public void BottomPanelHeight_IsTheStripAtHudScale()
    {
        Assert.Equal(223, HudLayout.BottomPanelHeight(1920, 1080, pixelDensity: 1f));
        Assert.Equal(446, HudLayout.BottomPanelHeight(3840, 2160, pixelDensity: 1f));
    }

    [Theory]
    [InlineData(1728, 1117, 2f)] // MacBook Pro 16" default: 1.03 UI units high, 2 device pixels per point
    [InlineData(1512, 982, 2f)]
    [InlineData(2560, 1440, 2f)]
    public void ArtScale_SnapsToWholeDevicePixelsOnDenseDisplays(int pointsW, int pointsH, float density)
    {
        float ui = ImGuiPlatform.ComputeUiScale(pointsW, pointsH);
        float scale = HudLayout.ArtScale(pointsW / ui, pointsH / ui, ui, density);
        float devicePerArtPixel = scale * ui * density;

        Assert.True(MathF.Abs(devicePerArtPixel - MathF.Round(devicePerArtPixel)) < 1e-3f, $"{devicePerArtPixel} device px per art px");
        Assert.True(devicePerArtPixel >= 1f);
    }

    [Theory]
    [InlineData(1920, 1080, 1.5f)] // 1.5 device px per art px would snap to 1, a third smaller
    [InlineData(1280, 720, 2f)]    // the HUD shrunk to keep the gap: 1.71 would snap to 1, half size
    public void ArtScale_KeepsTheFractionalScaleBelowTheTextureDensity(int pointsW, int pointsH, float density)
    {
        float ui = ImGuiPlatform.ComputeUiScale(pointsW, pointsH);
        Assert.Equal(HudLayout.HudScale(pointsW / ui, pointsH / ui), HudLayout.ArtScale(pointsW / ui, pointsH / ui, ui, density));
    }

    [Fact]
    public void ArtScale_KeepsTheFractionalScaleOnStandardDisplays()
    {
        Assert.Equal(HudLayout.HudScale(1728, 1117), HudLayout.ArtScale(1728, 1117, 1f, 1f));
    }
}
