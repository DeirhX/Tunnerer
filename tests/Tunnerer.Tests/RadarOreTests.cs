using Tunnerer.Core.Config;
using Tunnerer.Core.Sensing;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

/// <summary>
/// The radar and emberite (DESIGN.md §6.6): cool ore returns its own echo, a speck only from near and a deep deposit
/// from further; hot ore, and so a lump's warm heart, reads only as heat. Also the pulse's cadence under the radar
/// upgrade.
/// </summary>
public class RadarOreTests
{
    private static readonly RadarTarget[] NoTargets = [];
    private static readonly VisionSource[] None = [];
    private static readonly Position Origin = new(300, 300);

    private static TerrainGrid Dirt()
    {
        var terrain = new TerrainGrid(new Size(601, 601));
        terrain.Fill(TerrainPixel.Dirt);
        return terrain;
    }

    private static void Box(TerrainGrid terrain, int minX, int minY, int maxX, int maxY, TerrainPixel pixel)
    {
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                terrain.SetPixelRaw(new Position(x, y), pixel);
    }

    private static void Warm(TerrainGrid terrain, int minX, int minY, int maxX, int maxY, int temperature)
    {
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                terrain.AddHeat(new Position(x, y), temperature);
    }

    private static Radar Blind(TerrainGrid terrain) => new(new VisionMap(terrain));

    private static void Pulse(Radar radar, TerrainGrid terrain, RadarReach? reach = null)
    {
        for (int i = 0; i < Tweaks.Radar.PulsePeriodTicks; i++)
            radar.Advance(terrain, terrain, Origin, ownColor: 0, NoTargets, reach ?? RadarReach.Base);
    }

    private static List<RadarContact> Contacts(Radar radar)
    {
        var contacts = new List<RadarContact>();
        for (int i = 0; i < radar.ContactCount; i++)
            contacts.Add(radar.ContactAt(i));
        return contacts;
    }

    /// <summary>The returns of one pulse at a block of ore <paramref name="away"/> cells to the right of the tank.</summary>
    private static List<RadarContact> PingOre(int away, int thickness, RadarReach? reach = null, int temperature = 0)
    {
        var terrain = Dirt();
        int face = Origin.X + away;
        Box(terrain, face, Origin.Y - 2, face + thickness - 1, Origin.Y + 2, TerrainPixel.Emberite);
        if (temperature != 0)
            Warm(terrain, face, Origin.Y - 2, face + thickness - 1, Origin.Y + 2, temperature);
        var radar = Blind(terrain);
        Pulse(radar, terrain, reach);
        return Contacts(radar);
    }

    [Fact]
    public void ASpeck_IsSeenFromNear_ButNotFromFar()
    {
        int near = Tweaks.Radar.OreRangeMin - 5;
        var contacts = PingOre(near, thickness: 1);
        Assert.Contains(contacts, c => c.Echo == RadarEcho.Ore && c.Position.X == Origin.X + near);
        Assert.All(contacts, c => Assert.Equal(RadarEcho.Ore, c.Echo));

        Assert.Empty(PingOre(Tweaks.Radar.OreRangeMin + 15, thickness: 1));
    }

    [Fact]
    public void ADeepDeposit_IsSeenFromFurther_AndHidesWhatIsBehindItsFace()
    {
        int far = Tweaks.Radar.OreRangeMax - 5;
        var deep = PingOre(far, thickness: Tweaks.Radar.FormationDepth + 2);
        Assert.Contains(deep, c => c.Echo == RadarEcho.Ore && c.Position.X == Origin.X + far);
        Assert.All(deep, c => Assert.Equal(Origin.X + far, c.Position.X));

        Assert.Empty(PingOre(far, thickness: 2));
        Assert.Empty(PingOre(Tweaks.Radar.OreRangeMax + 10, thickness: Tweaks.Radar.FormationDepth + 2));
    }

    [Fact]
    public void Ore_IsNotTakenForRock()
    {
        var terrain = Dirt();
        // Ore in front of a rock formation answers as ore, and the rock is in its shadow.
        Box(terrain, Origin.X + 20, Origin.Y - 10, Origin.X + 20, Origin.Y + 10, TerrainPixel.Emberite);
        Box(terrain, Origin.X + 21, Origin.Y - 10, Origin.X + 40, Origin.Y + 10, TerrainPixel.Rock);
        var radar = Blind(terrain);

        Pulse(radar, terrain);

        var contacts = Contacts(radar);
        Assert.NotEmpty(contacts);
        Assert.All(contacts, c => Assert.Equal((RadarEcho.Ore, Origin.X + 20), (c.Echo, c.Position.X)));
    }

    [Fact]
    public void HotOre_ReturnsOnlyHeat()
    {
        var contacts = PingOre(20, thickness: 4, temperature: (int)Tweaks.Radar.HeatScrambleTemperature + 20);

        Assert.NotEmpty(contacts);
        Assert.DoesNotContain(contacts, c => c.Echo == RadarEcho.Ore);
        Assert.Contains(contacts, c => c.Echo == RadarEcho.Heat);
    }

    [Fact]
    public void ALumpsWarmHeart_LendsItsRimNoReach()
    {
        // A deep deposit whose cells behind the face are warm, as round a lump's core: from beyond the speck range
        // the radar reports heat at most, never ore.
        var terrain = Dirt();
        int face = Origin.X + Tweaks.Radar.OreRangeMin + 20;
        Box(terrain, face, Origin.Y - 6, face + 14, Origin.Y + 6, TerrainPixel.Emberite);
        Box(terrain, face + 5, Origin.Y - 2, face + 9, Origin.Y + 2, TerrainPixel.EmberiteCore);
        Warm(terrain, face + 1, Origin.Y - 6, face + 14, Origin.Y + 6, 30);
        var radar = Blind(terrain);

        Pulse(radar, terrain);

        Assert.DoesNotContain(Contacts(radar), c => c.Echo == RadarEcho.Ore);
    }

    [Fact]
    public void AnOreBlip_GoesOnceTheOreIsMined()
    {
        var terrain = Dirt();
        var speck = new Position(Origin.X + 25, Origin.Y);
        terrain.SetPixelRaw(speck, TerrainPixel.Emberite);
        var radar = Blind(terrain);
        Pulse(radar, terrain);
        Assert.Contains(Contacts(radar), c => c.Echo == RadarEcho.Ore && c.Position == speck);

        terrain.SetPixelRaw(speck, TerrainPixel.Blank);
        Pulse(radar, terrain);

        // The hole it left is a cave now, and reads as one.
        Assert.DoesNotContain(Contacts(radar), c => c.Echo == RadarEcho.Ore);
        Assert.Contains(Contacts(radar), c => c.Echo == RadarEcho.Cavity && c.Position == speck);

        // Ore that is gone without a hole (dirt has fallen in) is checked by the next pulse and dropped.
        terrain.SetPixelRaw(speck, TerrainPixel.Emberite);
        Pulse(radar, terrain);
        Assert.Contains(Contacts(radar), c => c.Echo == RadarEcho.Ore && c.Position == speck);
        terrain.SetPixelRaw(speck, TerrainPixel.Dirt);
        Pulse(radar, terrain);
        Assert.DoesNotContain(Contacts(radar), c => c.Position == speck);
    }

    [Fact]
    public void AnOreBlip_GoesOnceItsCellIsSeen_AndRememberedOreIsNoNews()
    {
        var terrain = Dirt();
        var speck = new Position(Origin.X + 25, Origin.Y);
        terrain.SetPixelRaw(speck, TerrainPixel.Emberite);
        var vision = new VisionMap(terrain);
        var radar = new Radar(vision);
        Pulse(radar, terrain);
        Assert.Contains(Contacts(radar), c => c.Echo == RadarEcho.Ore && c.Position == speck);

        vision.Update(terrain, [new VisionSource(speck, 0, 8)], changedCells: []);
        radar.Advance(terrain, terrain, Origin, ownColor: 0, NoTargets);
        Assert.DoesNotContain(Contacts(radar), c => c.Position == speck);

        // Out of sight again the side remembers the ore where it is, so the next pulse does not report it.
        vision.Update(terrain, None, changedCells: []);
        Pulse(radar, terrain);
        Assert.DoesNotContain(Contacts(radar), c => c.Position == speck);
    }

    [Fact]
    public void TheRadarUpgrade_LengthensTheOreRanges()
    {
        int away = Tweaks.Radar.OreRangeMin + 5;
        Assert.Empty(PingOre(away, thickness: 1));
        Assert.Contains(PingOre(away, thickness: 1, RadarReach.Scaled(1.2f, 1f)), c => c.Echo == RadarEcho.Ore);

        int far = Tweaks.Radar.OreRangeMax + 10;
        int deep = Tweaks.Radar.FormationDepth + 2;
        Assert.Empty(PingOre(far, deep));
        Assert.Contains(PingOre(far, deep, RadarReach.Scaled(1.2f, 1f)), c => c.Echo == RadarEcho.Ore);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.2f)]
    [InlineData(1.6f)]
    public void AnUpgradedRadar_PulsesAsOften_ItsFrontRunningFaster(float scale)
    {
        var terrain = Dirt();
        var radar = Blind(terrain);
        var reach = RadarReach.Scaled(scale, 1f);
        int pulses = 0, longestTrip = 0, trip = 0;
        bool wasInFlight = false;
        const int periods = 4;

        for (int i = 0; i < periods * Tweaks.Radar.PulsePeriodTicks; i++)
        {
            radar.Advance(terrain, terrain, Origin, ownColor: 0, NoTargets, reach);
            if (radar.PulseInFlight && !wasInFlight)
            {
                pulses++;
                trip = 0;
            }
            if (radar.PulseInFlight)
                longestTrip = Math.Max(longestTrip, ++trip);
            wasInFlight = radar.PulseInFlight;
        }

        Assert.Equal(periods, pulses);
        Assert.Equal(Tweaks.Radar.Range * scale, radar.PulseRange, 3);
        // The trip to full range takes the same ticks whatever the scale (give or take one for rounding).
        int baseTrip = Tweaks.Radar.Range / Tweaks.Radar.FrontCellsPerTick;
        Assert.InRange(longestTrip, baseTrip - 1, baseTrip + 2);
        Assert.Equal(Tweaks.Radar.FrontCellsPerTick * scale, radar.PulseSpeed);
    }
}
