namespace Emberward.Tests;

using System.Text.Json;
using Emberward.Browser.Client;
using Emberward.Core;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Network;
using Emberward.Core.Types;
using Emberward.Desktop.Config;
using Emberward.Desktop.Gui;

/// <summary>
/// The charge and flow overlay (DESIGN.md §4.9): the shared words and colours (<see cref="PowerLabels"/>), the machines
/// it picks, and what the browser's frame carries of it. Output only, from <see cref="World.MachinePowerOf"/> and
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
}

/// <summary>What the browser's frame carries of the charge overlay; slow (two full browser matches), so a class of its own.</summary>
public class PowerLabelsFrameTests
{
    [Fact]
    [Trait("Category", "Slow")]
    public void TheBrowserFrame_CarriesTheLabels_OnlyInAMatchWithTheEnergyNetwork()
    {
        Assert.Null(BrowserFrameBuilder.Capture(new BrowserSession(1234), includeEvents: false).Power);

        var session = new BrowserSession(1234, energyNetwork: true);
        var world = session.World;
        var home = session.Player.Base!;
        var spot = home.Position + new Offset(0, home.Position.Y < world.Terrain.Height / 2 ? 14 : -14);
        var relay = new Machine(spot, MachineType.Relay, session.Player.Color);
        world.Machines.Add(relay);
        world.PlantMachine(relay, home);
        session.Advance(default);

        var power = BrowserFrameBuilder.Capture(session, includeEvents: false).Power;

        Assert.NotNull(power);
        var expected = PowerLabels.Of(world.BasePowerOf(home)!.Value);
        Assert.Equal(new BaseCharge(expected.Store, expected.Flow, expected.Net, expected.Fill, HudPalette.Hex(expected.Color)),
            power.Base);
        Assert.Equal(HudPalette.Hex(HudPalette.Text), power.Text);
        Assert.Equal(DesktopScreenTweaks.PowerLabelZoomGone, power.ZoomGone);
        Assert.Equal(PowerLabels.LiftCells, power.LiftCells);
        var line = Assert.Single(power.Machines, m => m.Position == spot);
        var label = PowerLabels.Of(world.MachinePowerOf(relay)!.Value);
        Assert.Equal((label.Charge, label.Flow, label.Fill, HudPalette.Hex(label.Color)),
            (line.Charge, line.Flow, line.Fill, line.Color));
        Assert.All(power.Machines, m => Assert.True(session.Vision.IsVisible(m.Position)));

        // The page reads it as JSON, under "power".
        string json = JsonSerializer.Serialize(BrowserFrameBuilder.Capture(session, includeEvents: false),
            BrowserJsonContext.Default.BrowserFrame);
        using var parsed = JsonDocument.Parse(json);
        var machines = parsed.RootElement.GetProperty("power").GetProperty("machines");
        Assert.Equal(power.Machines.Length, machines.GetArrayLength());
        Assert.Equal(line.Charge, machines.EnumerateArray().First(m => m.GetProperty("position").GetProperty("x").GetInt32() == spot.X
            && m.GetProperty("position").GetProperty("y").GetInt32() == spot.Y).GetProperty("charge").GetString());
    }
}
