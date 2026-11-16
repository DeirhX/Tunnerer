using Tunnerer.Core.Config;
using Tunnerer.Core.Sensing;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

/// <summary>
/// The tank's radar: a turning beam whose rays pass through dirt and open ground and leave a fading blip on the
/// first hard thing they meet, which hides whatever is behind it.
/// </summary>
public class RadarTests
{
    private static readonly RadarTarget[] NoTargets = [];
    private static readonly VisionSource[] None = [];
    private static readonly Position Origin = new(200, 200);

    private static TerrainGrid Dirt()
    {
        var terrain = new TerrainGrid(new Size(401, 401));
        terrain.Fill(TerrainPixel.Dirt);
        return terrain;
    }

    private static void Box(TerrainGrid terrain, int minX, int minY, int maxX, int maxY, TerrainPixel pixel)
    {
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                terrain.SetPixelRaw(new Position(x, y), pixel);
    }

    /// <summary>A radar whose side sees nothing, so every return is kept.</summary>
    private static Radar Blind(TerrainGrid terrain) => new(new VisionMap(terrain));

    private static void Revolve(Radar radar, TerrainGrid terrain, Position origin, RadarTarget[]? targets = null, int revolutions = 1)
    {
        int ticks = revolutions * Tweaks.Radar.RaysPerRevolution / Tweaks.Radar.RaysPerTick;
        for (int i = 0; i < ticks; i++)
            radar.Sweep(terrain, terrain, origin, ownColor: 0, targets ?? NoTargets);
    }

    private static List<RadarContact> Contacts(Radar radar)
    {
        var contacts = new List<RadarContact>();
        for (int i = 0; i < radar.ContactCount; i++)
            contacts.Add(radar.ContactAt(i));
        return contacts;
    }

    [Fact]
    public void PlainDirt_ReturnsNothing()
    {
        var terrain = Dirt();
        var radar = Blind(terrain);

        Revolve(radar, terrain, Origin);

        Assert.Equal(0, radar.ContactCount);
    }

    [Fact]
    public void ARock_ReturnsBlipsOnlyFromTheSideFacingTheTank()
    {
        var terrain = Dirt();
        Box(terrain, 260, 180, 290, 220, TerrainPixel.Rock);
        var radar = Blind(terrain);

        Revolve(radar, terrain, Origin);

        var contacts = Contacts(radar);
        Assert.True(contacts.Count >= 8, $"the near face should return several blips, got {contacts.Count}");
        Assert.All(contacts, c =>
        {
            Assert.Equal(RadarEcho.Solid, c.Echo);
            Assert.Equal(260, c.Position.X);
            Assert.InRange(c.Position.Y, 180, 220);
        });
    }

    [Fact]
    public void ARock_HidesWhatIsBehindIt_UntilPingedFromAnotherSide()
    {
        var terrain = Dirt();
        Box(terrain, 240, 180, 250, 220, TerrainPixel.Rock);
        Box(terrain, 280, 198, 284, 202, TerrainPixel.Gold);
        var radar = Blind(terrain);

        Revolve(radar, terrain, Origin);
        Assert.DoesNotContain(Contacts(radar), c => c.Echo == RadarEcho.Metal);

        // From below, the gold is in the clear.
        Revolve(radar, terrain, new Position(282, 290));
        Assert.Contains(Contacts(radar), c => c.Echo == RadarEcho.Metal && c.Position.Y == 202);
    }

    [Fact]
    public void ACave_GivesAWeakReturn_AndLetsTheRayOn()
    {
        var terrain = Dirt();
        Box(terrain, 230, 190, 240, 210, TerrainPixel.Blank);
        Box(terrain, 270, 180, 280, 220, TerrainPixel.Rock);
        var radar = Blind(terrain);

        Revolve(radar, terrain, Origin);

        var contacts = Contacts(radar);
        Assert.Contains(contacts, c => c.Echo == RadarEcho.Cavity && c.Position.X == 230);
        Assert.Contains(contacts, c => c.Echo == RadarEcho.Solid && c.Position.X == 270 && c.Position.Y == 200);
    }

    [Fact]
    public void AMachineOrTank_ReflectsAsMetal()
    {
        var terrain = Dirt();
        var radar = Blind(terrain);
        RadarTarget[] targets = [new RadarTarget(new Position(200, 120), 2)];

        Revolve(radar, terrain, Origin, targets);

        var contacts = Contacts(radar);
        Assert.NotEmpty(contacts);
        Assert.All(contacts, c =>
        {
            Assert.Equal(RadarEcho.Metal, c.Echo);
            Assert.InRange(c.Position.X, 198, 202);
            Assert.InRange(c.Position.Y, 118, 122);
        });
    }

    [Fact]
    public void NothingBeyondTheRange_IsFound()
    {
        var terrain = Dirt();
        int beyond = Origin.X + Tweaks.Radar.Range + 3;
        Box(terrain, beyond, 0, beyond + 5, 400, TerrainPixel.Rock);
        var radar = Blind(terrain);

        Revolve(radar, terrain, Origin);

        Assert.Equal(0, radar.ContactCount);
    }

    [Fact]
    public void TheTanksOwnBaseWalls_DoNotReflect_AnotherSidesDo()
    {
        var terrain = Dirt();
        Box(terrain, 220, 180, 222, 220, TerrainPixel.BaseMin);
        var radar = Blind(terrain);

        Revolve(radar, terrain, Origin);
        Assert.Equal(0, radar.ContactCount);

        var other = Blind(terrain);
        for (int i = 0; i < Tweaks.Radar.RaysPerRevolution / Tweaks.Radar.RaysPerTick; i++)
            other.Sweep(terrain, terrain, Origin, ownColor: 1, NoTargets);
        Assert.True(other.ContactCount > 0);
    }

    [Fact]
    public void WhatTheSideAlreadySees_IsNotReported()
    {
        var terrain = Dirt();
        Box(terrain, 201, 190, 229, 210, TerrainPixel.Blank);
        terrain.SetPixelRaw(Origin, TerrainPixel.Blank);
        Box(terrain, 230, 180, 240, 220, TerrainPixel.Rock);
        var vision = new VisionMap(terrain);
        vision.Update(terrain, None, [new VisionSource(Origin, 45, 0)]);
        Assert.True(vision.IsVisible(new Position(230, 200)));
        var radar = new Radar(vision);

        Revolve(radar, terrain, Origin);

        Assert.DoesNotContain(Contacts(radar), c => vision.IsVisible(c.Position));
    }

    [Fact]
    public void RememberedGround_BlipsOnlyWhereItHasChanged()
    {
        var terrain = Dirt();
        Box(terrain, 300, 180, 310, 220, TerrainPixel.Rock);
        var vision = new VisionMap(terrain);
        // The side has seen the rock's near face, then looked away.
        vision.Update(terrain, None, [new VisionSource(new Position(296, 200), 0, 30)]);
        vision.Update(terrain, None, None);
        Assert.True(vision.IsExplored(new Position(300, 200)) && !vision.IsVisible(new Position(300, 200)));
        var radar = new Radar(vision);

        Revolve(radar, terrain, Origin);
        Assert.Equal(0, radar.ContactCount);

        // Something tunnels through the remembered dirt in front of it: news.
        Box(terrain, 285, 190, 290, 210, TerrainPixel.Blank);
        Revolve(radar, terrain, Origin);
        var contacts = Contacts(radar);
        Assert.NotEmpty(contacts);
        Assert.All(contacts, c => Assert.Equal(RadarEcho.Cavity, c.Echo));
    }

    [Fact]
    public void Blips_FadeAway_AndStayWhereTheyWereMade()
    {
        var terrain = Dirt();
        var radar = Blind(terrain);
        RadarTarget[] there = [new RadarTarget(new Position(260, 200), 2)];
        Revolve(radar, terrain, Origin, there);
        var first = Contacts(radar);
        Assert.NotEmpty(first);

        // The target leaves; its blips stay put until they expire.
        Revolve(radar, terrain, Origin);
        Assert.Equal(first, Contacts(radar));

        for (int i = 0; i < Tweaks.Radar.ContactLifetimeTicks; i++)
            radar.Idle();
        Assert.Equal(0, radar.ContactCount);
    }

    [Fact]
    public void LaterRevolutions_HitDifferentCells_SoAShapeFillsIn()
    {
        var terrain = Dirt();
        Box(terrain, 320, 100, 330, 300, TerrainPixel.Rock);
        var once = Blind(terrain);
        var thrice = Blind(terrain);

        Revolve(once, terrain, Origin);
        Revolve(thrice, terrain, Origin, revolutions: 3);

        int Distinct(Radar radar) => Contacts(radar).Select(c => c.Position).Distinct().Count();
        Assert.True(Distinct(thrice) > Distinct(once) * 2,
            $"three sweeps should hit far more cells than one: {Distinct(once)} then {Distinct(thrice)}");
    }

    /// <summary>Brings the ground in the box, which starts at 0, to <paramref name="temperature"/> degrees.</summary>
    private static void Warm(TerrainGrid terrain, int minX, int minY, int maxX, int maxY, int temperature)
    {
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                terrain.AddHeat(new Position(x, y), temperature);
        Assert.Equal(temperature, terrain.GetHeatTemperature(new Position(minX, minY)), 3);
    }

    [Fact]
    public void HotGround_AbsorbsTheBeam_AndLeavesHeatNoiseWhereItDied()
    {
        var terrain = Dirt();
        Box(terrain, 300, 180, 310, 220, TerrainPixel.Rock);
        Warm(terrain, 240, 150, 270, 250, temperature: 60);
        var radar = Blind(terrain);

        Revolve(radar, terrain, Origin);

        var contacts = Contacts(radar);
        Assert.DoesNotContain(contacts, c => c.Echo == RadarEcho.Solid);
        Assert.NotEmpty(contacts);
        // 60 degrees takes 0.2 a cell: the ray dies on its fifth hot cell, well short of the rock.
        Assert.All(contacts, c =>
        {
            Assert.Equal(RadarEcho.Heat, c.Echo);
            Assert.InRange(c.Position.X, 240, 250);
            Assert.Equal(1f, c.Strength);
        });
        Assert.True(radar.HeatLoss is 0f, "the loss is reported once a revolution has finished");
        Revolve(radar, terrain, Origin);
        Assert.InRange(radar.HeatLoss, 0.05f, 0.5f);
    }

    [Fact]
    public void WarmGround_LetsTheBeamThroughWeaker()
    {
        var terrain = Dirt();
        Box(terrain, 300, 180, 310, 220, TerrainPixel.Rock);
        Warm(terrain, 240, 199, 250, 201, temperature: 20);
        var radar = Blind(terrain);

        Revolve(radar, terrain, Origin);

        var contacts = Contacts(radar);
        Assert.All(contacts, c => Assert.Equal(RadarEcho.Solid, c.Echo));
        RadarContact throughWarm = contacts.Single(c => c.Position == new Position(300, 200));
        RadarContact throughCool = contacts.First(c => c.Position.Y == 185);
        // 11 cells at 10 degrees over the threshold: 11 * 10 * 0.004 = 0.44 lost.
        Assert.InRange(throughWarm.Strength, 0.5f, 0.62f);
        Assert.Equal(1f, throughCool.Strength);
        Assert.Equal(0f, radar.HeatLoss);
    }

    [Fact]
    public void AHotReflector_ReturnsOnlyHeatNoise()
    {
        var terrain = Dirt();
        Box(terrain, 260, 190, 270, 210, TerrainPixel.Rock);
        Warm(terrain, 260, 190, 270, 210, temperature: 100);
        var radar = Blind(terrain);

        Revolve(radar, terrain, Origin);

        var contacts = Contacts(radar);
        Assert.NotEmpty(contacts);
        Assert.All(contacts, c =>
        {
            Assert.Equal(RadarEcho.Heat, c.Echo);
            Assert.Equal(260, c.Position.X);
        });
    }

    [Fact]
    public void ColdGround_DoesNotWeakenTheBeam()
    {
        var terrain = Dirt();
        Box(terrain, 300, 180, 310, 220, TerrainPixel.Rock);
        Warm(terrain, 240, 150, 270, 250, temperature: -60);
        var radar = Blind(terrain);

        Revolve(radar, terrain, Origin);

        var contacts = Contacts(radar);
        Assert.NotEmpty(contacts);
        Assert.All(contacts, c => Assert.Equal((RadarEcho.Solid, 1f), (c.Echo, c.Strength)));
    }

    [Fact]
    public void AWorld_TurnsTheRadar_WithoutChangingTheSimulation()
    {
        static ulong Run(bool track, out int contacts)
        {
            var world = TestHelpers.CreateSeededWorld(seed: 777);
            Radar? radar = track ? world.TrackRadar(world.TankList.Tanks[0]) : null;
            for (int i = 0; i < 150; i++)
                world.Advance(_ => new Tunnerer.Core.Input.ControllerOutput { MoveSpeed = new Offset(1, 0) });
            contacts = radar?.ContactCount ?? 0;
            return TestSimulation.ComputeWorldStateHash(world);
        }

        ulong plain = Run(track: false, out _);
        ulong tracked = Run(track: true, out int found);

        Assert.Equal(plain, tracked);
        Assert.True(found > 0, "a generated map should give the radar something to find");
    }
}
