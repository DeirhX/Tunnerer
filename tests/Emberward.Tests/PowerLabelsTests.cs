namespace Emberward.Tests;

using System.Runtime.InteropServices;
using System.Text.Json;
using Emberward.Browser.Client;
using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Network;
using Emberward.Core.Types;
using Emberward.Desktop.Config;
using Emberward.Desktop.Gui;
using Emberward.Desktop.Rendering;
using Emberward.Desktop.Rendering.Entities;

/// <summary>
/// The charge and flow overlay (DESIGN.md §4.9): the shared words and colours (<see cref="PowerLabels"/>), the machines
/// it picks, and what the browser's HUD and render packet carry. Output only, from <see cref="World.MachinePowerOf"/> and
/// <see cref="World.BasePowerOf"/>.
/// </summary>
public class PowerLabelsTests
{
    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(0.4, "0")]
    [InlineData(-0.3, "0")]
    [InlineData(850.0, "850")]
    [InlineData(999.6, "1.0k")]
    [InlineData(2400.0, "2.4k")]
    [InlineData(3600.0, "3.6k")]
    [InlineData(9960.0, "10k")]
    [InlineData(36_000.0, "36k")]
    [InlineData(999_600.0, "1.0M")]
    [InlineData(1_234_567.0, "1.2M")]
    [InlineData(-1500.0, "-1.5k")]
    [InlineData(double.NaN, "0")]
    public void Compact_IsShortAndRoundsAcrossItsUnits(double value, string expected) =>
        Assert.Equal(expected, PowerLabels.Compact(value));

    [Fact]
    public void Flows_ShowTheirSigns()
    {
        Assert.Equal("+120 -40/s", PowerLabels.Flow(120.0, 40.0));
        Assert.Equal("+0 -2.4k/s", PowerLabels.Flow(-5.0, 2400.0));
        Assert.Equal("+900/s", PowerLabels.SignedRate(900.0));
        Assert.Equal("-1.2k/s", PowerLabels.SignedRate(-1200.0));
        Assert.Equal("0/s", PowerLabels.SignedRate(-0.2));
    }

    [Fact]
    public void AMachinesLabel_IsChargeOverCapacity_FlowAndAFedOrStarvedColour()
    {
        var fed = PowerLabels.Of(new MachinePower(2400.0, 3600.0, 120.0, 40.0, IsFed: true));
        Assert.Equal("2.4k/3.6k", fed.Charge);
        Assert.Equal("+120 -40/s", fed.Flow);
        Assert.Equal(2f / 3f, fed.Fill, precision: 5);
        Assert.Equal(StatusLevel.Ok, fed.Level);
        Assert.Equal(HudPalette.Good, fed.Color);

        var runningDown = PowerLabels.Of(new MachinePower(500.0, 3600.0, 0.0, 40.0, IsFed: false));
        Assert.Equal(StatusLevel.Warn, runningDown.Level);
        Assert.Equal(HudPalette.Warn, runningDown.Color);

        var flat = PowerLabels.Of(new MachinePower(0.0, 3600.0, 0.0, 0.0, IsFed: false));
        Assert.Equal(StatusLevel.Bad, flat.Level);
        Assert.Equal(HudPalette.Danger, flat.Color);
        Assert.Equal(0f, flat.Fill);
        Assert.Equal(0f, PowerLabels.Of(new MachinePower(10.0, 0.0, 0.0, 0.0, IsFed: true)).Fill);
    }

    [Fact]
    public void Production_GivesTheGeneratorItsOwnLabel_AndAddsItToTheBaseRow()
    {
        var generator = PowerLabels.Of(new MachinePower(2400.0, 4800.0, 120.0, 40.0, IsFed: true, ProductionPerSecond: 375.0));
        Assert.Equal(HudPalette.Producer, generator.Color);
        Assert.Equal("gen +375/s", generator.Flow);

        var home = PowerLabels.Of(new BasePower(12_000.0, 40_000.0, 1200.0, 300.0, GeneratorProductionPerSecond: 375.0));
        Assert.Equal("net +900/s · gen +375/s", home.Net);
    }

    [Fact]
    public void TheBaseRow_IsStoreProductionAgainstDraw_AndTheNet()
    {
        var gaining = PowerLabels.Of(new BasePower(12_000.0, 40_000.0, 1200.0, 300.0));
        Assert.Equal("12k/40k", gaining.Store);
        Assert.Equal("+1.2k -300/s", gaining.Flow);
        Assert.Equal("net +900/s", gaining.Net);
        Assert.Equal(0.3f, gaining.Fill, precision: 5);
        Assert.Equal(StatusLevel.Ok, gaining.Level);

        Assert.Equal(StatusLevel.Warn, PowerLabels.Of(new BasePower(20_000.0, 40_000.0, 0.0, 300.0)).Level);
        Assert.Equal(StatusLevel.Bad, PowerLabels.Of(new BasePower(5_000.0, 40_000.0, 0.0, 300.0)).Level);
        Assert.Equal(StatusLevel.Bad, PowerLabels.Of(new BasePower(0.0, 40_000.0, 500.0, 300.0)).Level);
        Assert.Equal("net -300/s", PowerLabels.Of(new BasePower(5_000.0, 40_000.0, 0.0, 300.0)).Net);
    }

    [Fact]
    public void Labels_FadeWithDistanceAndWithZoom()
    {
        Assert.Equal(1f, PowerLabels.DistanceFade(0f));
        Assert.Equal(1f, PowerLabels.DistanceFade(DesktopScreenTweaks.PowerLabelFullCells));
        Assert.Equal(0.5f, PowerLabels.DistanceFade(
            (DesktopScreenTweaks.PowerLabelFullCells + DesktopScreenTweaks.PowerLabelGoneCells) / 2f), precision: 5);
        Assert.Equal(0f, PowerLabels.DistanceFade(DesktopScreenTweaks.PowerLabelGoneCells));
        Assert.Equal(0f, PowerLabels.DistanceFade(float.NaN));

        Assert.Equal(1f, PowerLabels.ZoomFade(DesktopScreenTweaks.PixelScale));
        Assert.Equal(0f, PowerLabels.ZoomFade(DesktopScreenTweaks.PowerLabelZoomGone));
        Assert.Equal(1f, PowerLabels.ZoomFade(DesktopScreenTweaks.PowerLabelZoomFull));
    }

    [Fact]
    public void Hex_IsTheHudColourAsCss()
    {
        Assert.Equal("#76e680", HudPalette.Hex(HudPalette.Good));
        Assert.Equal("#ff5642", HudPalette.Hex(HudPalette.Danger));
        Assert.Equal(HudTheme.Good, HudPalette.Good);
    }

    [Fact]
    public void Collect_LabelsStandingMachinesNearThePlayer_OnlyWithTheEnergyNetwork()
    {
        var on = RelayHeatTests.CreateScenario(energyNetwork: true);
        var relay = RelayHeatTests.PlantRelay(on);
        var far = RelayHeatTests.PlantRelay(on, new Position(
            on.Tank.Position.X, on.Tank.Position.Y + on.DirY * (int)DesktopScreenTweaks.PowerLabelGoneCells + on.DirY * 4));
        on.World.Advance(_ => default);
        var labels = new List<MachinePowerLabel>();

        PowerLabels.Collect(on.World, on.Tank, vision: null, labels, new List<Machine>());

        var label = Assert.Single(labels);
        Assert.Same(relay, label.Machine);
        Assert.DoesNotContain(labels, l => ReferenceEquals(l.Machine, far));
        Assert.Equal(PowerLabels.Of(on.World.MachinePowerOf(relay)!.Value), label.Label);
        Assert.InRange(label.Opacity, 0.01f, 1f);

        var off = RelayHeatTests.CreateScenario();
        RelayHeatTests.PlantRelay(off);
        PowerLabels.Collect(off.World, off.Tank, vision: null, labels, new List<Machine>());
        Assert.Empty(labels);
    }

    [Fact]
    public void Collect_LeavesOutEnemyMachinesUnderFog_AsTheSpritesDo()
    {
        var s = RelayHeatTests.CreateScenario(energyNetwork: true);
        var vision = s.World.TrackVision(s.Tank);
        var enemy = s.World.TankList.Tanks.First(t => t.Base != null && !ReferenceEquals(t.Base, s.Base));
        Machine PlantEnemy(Position at)
        {
            var machine = new Machine(at, MachineType.Relay, enemy.Color);
            s.World.Machines.Add(machine);
            s.World.PlantMachine(machine, enemy.Base);
            return machine;
        }
        // One in the open tunnel just outside the base's door, one in the solid rock beside the base, both well within
        // label reach.
        var seen = PlantEnemy(s.Base.Position + new Offset(0, s.DirY * (Tweaks.Base.BaseSize / 2 + 4)));
        var hidden = PlantEnemy(s.Base.Position + new Offset(Tweaks.Base.BaseSize + 30, 0));
        for (int i = 0; i < 4; i++)
            s.World.Advance(_ => default);
        Assert.True(vision.IsVisible(seen.Position), "the tunnel relay should be in sight");
        Assert.False(vision.IsVisible(hidden.Position), "the relay in the rock should be under fog");

        var labels = new List<MachinePowerLabel>();
        PowerLabels.Collect(s.World, s.Tank, vision, labels, new List<Machine>());

        Assert.Contains(labels, l => ReferenceEquals(l.Machine, seen));
        Assert.DoesNotContain(labels, l => ReferenceEquals(l.Machine, hidden));
        PowerLabels.Collect(s.World, s.Tank, vision: null, labels, new List<Machine>());
        Assert.Contains(labels, l => ReferenceEquals(l.Machine, hidden));
    }
}

/// <summary>What the browser's frame carries of the charge overlay; slow (two full browser matches), so a class of its own.</summary>
public class PowerLabelsFrameTests
{
    [Fact]
    [Trait("Category", "Slow")]
    public void TheBrowserFrame_CarriesChargeQuadsAndABaseReadout_InEnergyMatches()
    {
        var standard = new BrowserSession(1234);
        Assert.Null(BrowserFrameBuilder.Capture(standard, includeEvents: false).BasePower);
        Assert.Empty(RenderLabels(standard, cellSize: 6f));

        var session = new BrowserSession(1234, energyNetwork: true);
        var world = session.World;
        var home = session.Player.Base!;
        var spot = home.Position + new Offset(0, home.Position.Y < world.Terrain.Height / 2 ? 14 : -14);
        var relay = new Machine(spot, MachineType.Relay, session.Player.Color);
        world.Machines.Add(relay);
        world.PlantMachine(relay, home);
        session.Advance(default);

        var frame = BrowserFrameBuilder.Capture(session, includeEvents: false);
        var expected = PowerLabels.Of(world.BasePowerOf(home)!.Value);
        Assert.Equal(new BaseCharge(expected.Store, expected.Flow, expected.Net, expected.Fill, HudPalette.Hex(expected.Color), expected.Shield),
            frame.BasePower);

        string json = JsonSerializer.Serialize(frame, BrowserJsonContext.Default.BrowserFrame);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(expected.Store, parsed.RootElement.GetProperty("basePower").GetProperty("store").GetString());

        Assert.True(session.Vision.IsVisible(spot));
        var full = RenderLabels(session, cellSize: 6f);
        Assert.Contains(full, v => v.P2 > 0f);
        Assert.Contains(full, v => v.P2 == 0f);

        float halfZoom = (DesktopScreenTweaks.PowerLabelZoomGone + DesktopScreenTweaks.PowerLabelZoomFull) * 0.5f;
        var faded = RenderLabels(session, halfZoom);
        Assert.Equal(full.Length, faded.Length);
        Assert.Equal(full[0].P3 * 0.5f, faded[0].P3, precision: 5);
        Assert.Empty(RenderLabels(session, DesktopScreenTweaks.PowerLabelZoomGone));
        Assert.Empty(RenderLabels(session, cellSize: 0f));
    }

    private static EntityVertex[] RenderLabels(BrowserSession session, float cellSize)
    {
        session.Scene.Render(1280, 720, 6, alpha: 1f, delta: 0f, time: 0f, powerLabelCellSize: cellSize);
        return MemoryMarshal.Cast<byte, EntityVertex>(session.Scene.Vertices).ToArray()
            .Where(v => v.Kind == EntityShape.KindPowerLabel).ToArray();
    }
}
