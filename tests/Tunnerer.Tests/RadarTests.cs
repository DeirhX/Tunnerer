using Tunnerer.Core.Config;
using Tunnerer.Core.Sensing;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

/// <summary>
/// The tank's radar: pulses whose rays run outward through dirt and open ground and leave a fading blip on the
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

    /// <summary>Runs the radar for <paramref name="pulses"/> whole pulse periods: each sends a pulse that finishes.</summary>
    private static void Pulse(Radar radar, TerrainGrid terrain, Position origin, RadarTarget[]? targets = null, int pulses = 1) =>
        Run(radar, terrain, origin, pulses * Tweaks.Radar.PulsePeriodTicks, targets);

    private static void Run(Radar radar, TerrainGrid terrain, Position origin, int ticks, RadarTarget[]? targets = null)
    {
        for (int i = 0; i < ticks; i++)
            radar.Advance(terrain, terrain, origin, ownColor: 0, targets ?? NoTargets);
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

        Pulse(radar, terrain, Origin);

        Assert.Equal(0, radar.ContactCount);
    }

    [Fact]
    public void ARock_ReturnsBlipsOnlyFromTheSideFacingTheTank()
    {
        var terrain = Dirt();
        Box(terrain, 260, 180, 290, 220, TerrainPixel.Rock);
        var radar = Blind(terrain);

        Pulse(radar, terrain, Origin);

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

        Pulse(radar, terrain, Origin);
        Assert.DoesNotContain(Contacts(radar), c => c.Echo == RadarEcho.Metal);

        // From below, the gold is in the clear.
        Pulse(radar, terrain, new Position(282, 290));
        Assert.Contains(Contacts(radar), c => c.Echo == RadarEcho.Metal && c.Position.Y == 202);
    }

    [Fact]
    public void ACave_GivesAWeakReturn_AndLetsTheRayOn()
    {
        var terrain = Dirt();
        Box(terrain, 230, 190, 240, 210, TerrainPixel.Blank);
        Box(terrain, 270, 180, 280, 220, TerrainPixel.Rock);
        var radar = Blind(terrain);

        Pulse(radar, terrain, Origin);

        var contacts = Contacts(radar);
        Assert.Contains(contacts, c => c.Echo == RadarEcho.Cavity && c.Position.X == 230);
        Assert.Contains(contacts, c => c.Echo == RadarEcho.Solid && c.Position.X == 270 && c.Position.Y == 200);
    }

    [Fact]
    public void AMachineOrTank_ReflectsAsMetal()
    {
        var terrain = Dirt();
        var radar = Blind(terrain);
        RadarTarget[] targets = [new RadarTarget(new Position(200, 140), 2)];

        Pulse(radar, terrain, Origin, targets);

        var contacts = Contacts(radar);
        Assert.NotEmpty(contacts);
        Assert.All(contacts, c =>
        {
            Assert.Equal(RadarEcho.Metal, c.Echo);
            Assert.InRange(c.Position.X, 198, 202);
            Assert.InRange(c.Position.Y, 138, 142);
        });
    }

    [Fact]
    public void NothingBeyondTheRange_IsFound()
    {
        var terrain = Dirt();
        int beyond = Origin.X + Tweaks.Radar.Range + 3;
        Box(terrain, beyond, 0, beyond + 5, 400, TerrainPixel.Rock);
        var radar = Blind(terrain);

        Pulse(radar, terrain, Origin);

        Assert.Equal(0, radar.ContactCount);
    }

    [Fact]
    public void TheTanksOwnBaseWalls_DoNotReflect_AnotherSidesDo()
    {
        var terrain = Dirt();
        Box(terrain, 220, 180, 222, 220, TerrainPixel.BaseMin);
        var radar = Blind(terrain);

        Pulse(radar, terrain, Origin);
        Assert.Equal(0, radar.ContactCount);

        var other = Blind(terrain);
        for (int i = 0; i < Tweaks.Radar.PulsePeriodTicks; i++)
            other.Advance(terrain, terrain, Origin, ownColor: 1, NoTargets);
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
        vision.Update(terrain, [new VisionSource(Origin, 45, 0)], changedCells: []);
        Assert.True(vision.IsVisible(new Position(230, 200)));
        var radar = new Radar(vision);

        Pulse(radar, terrain, Origin);

        Assert.DoesNotContain(Contacts(radar), c => vision.IsVisible(c.Position));
    }

    [Fact]
    public void ALivingTarget_ReturnsLife_AndAMachineMetal()
    {
        var terrain = Dirt();
        var radar = Blind(terrain);
        RadarTarget[] targets =
        [
            new RadarTarget(new Position(260, 200), 3, Alive: true),
            new RadarTarget(new Position(200, 260), 3),
        ];

        Pulse(radar, terrain, Origin, targets);

        var contacts = Contacts(radar);
        Assert.Contains(contacts, c => c.Echo == RadarEcho.Life);
        Assert.Contains(contacts, c => c.Echo == RadarEcho.Metal);
        Assert.All(contacts.Where(c => c.Echo == RadarEcho.Life), c => Assert.InRange(c.Position.X, 257, 263));
        Assert.All(contacts.Where(c => c.Echo == RadarEcho.Metal), c => Assert.InRange(c.Position.Y, 257, 263));

        // The tank drives off: the next pulse finds it in its new place and drops the old blips.
        targets[0] = new RadarTarget(new Position(240, 160), 3, Alive: true);
        Pulse(radar, terrain, Origin, targets);
        var life = Contacts(radar).Where(c => c.Echo == RadarEcho.Life).ToList();
        Assert.NotEmpty(life);
        Assert.All(life, c => Assert.InRange(c.Position.Y, 157, 163));
    }

    [Fact]
    public void HotGroundInSight_DoesNotUseUpTheHeatMarks()
    {
        var terrain = Dirt();
        Warm(terrain, 201, 199, 260, 201, temperature: 15);
        var vision = new VisionMap(terrain);
        // The side sees the first stretch of the warm strip, more cells than a ray may mark.
        vision.Update(terrain, [new VisionSource(Origin, 0, 30)], changedCells: []);
        Assert.True(vision.IsVisible(new Position(225, 200)) && !vision.IsVisible(new Position(235, 200)));
        var radar = new Radar(vision);

        Pulse(radar, terrain, Origin);

        var onAxis = Contacts(radar).Where(c => c.Echo == RadarEcho.Heat && c.Position.Y == Origin.Y).ToList();
        Assert.Equal(Tweaks.Radar.HeatReturnsPerRay, onAxis.Count);
        Assert.All(onAxis, c => Assert.False(vision.IsVisible(c.Position)));
    }

    [Fact]
    public void ALargeFormation_IsSeenFromAfar_AThinWallOnlyFromNear()
    {
        static List<RadarContact> Ping(int faceX, int thickness)
        {
            var terrain = Dirt();
            Box(terrain, faceX, 150, faceX + thickness - 1, 250, TerrainPixel.Rock);
            var radar = Blind(terrain);
            Pulse(radar, terrain, Origin);
            return Contacts(radar);
        }

        int far = Origin.X + 130, near = Origin.X + Tweaks.Radar.SolidRangeMin - 10;
        Assert.Contains(Ping(far, thickness: 30), c => c.Echo == RadarEcho.Solid && c.Position.X == far);
        Assert.Empty(Ping(far, thickness: 2));
        Assert.Contains(Ping(near, thickness: 2), c => c.Echo == RadarEcho.Solid && c.Position.X == near);
        // Half the depth of a full formation carries about half the way from the near range to the full one.
        int mid = Origin.X + (Tweaks.Radar.SolidRangeMin + Tweaks.Radar.Range) / 2;
        Assert.NotEmpty(Ping(mid - 15, thickness: Tweaks.Radar.FormationDepth / 2 + 1));
        Assert.Empty(Ping(mid + 15, thickness: Tweaks.Radar.FormationDepth / 2 + 1));
    }

    [Fact]
    public void ATankBeyondItsRange_ReturnsNothing_ButStillShadowsWhatIsBehindIt()
    {
        var terrain = Dirt();
        Box(terrain, 300, 150, 330, 250, TerrainPixel.Rock);
        var radar = Blind(terrain);
        int away = Tweaks.Radar.LifeRange + 20;
        RadarTarget[] targets = [new RadarTarget(new Position(Origin.X + away, Origin.Y), 3, Alive: true)];

        Pulse(radar, terrain, Origin, targets);

        var contacts = Contacts(radar);
        Assert.DoesNotContain(contacts, c => c.Echo == RadarEcho.Life);
        Assert.Contains(contacts, c => c.Echo == RadarEcho.Solid && c.Position.Y is >= 170 and <= 190);
        Assert.DoesNotContain(contacts, c => c.Position.Y is >= 197 and <= 203);
    }

    [Fact]
    public void ABlip_GoesOnceItsCellHasBeenSeen_AndDoesNotComeBack()
    {
        var terrain = Dirt();
        Box(terrain, 300, 180, 310, 220, TerrainPixel.Rock);
        Warm(terrain, 240, 199, 250, 201, temperature: 20);
        var vision = new VisionMap(terrain);
        var radar = new Radar(vision);
        Pulse(radar, terrain, Origin);
        Assert.Contains(Contacts(radar), c => c.Position == new Position(300, 200));
        Assert.Contains(Contacts(radar), c => c.Echo == RadarEcho.Heat);

        // The side looks at the rock's face and the warm ground, then away again.
        vision.Update(terrain, [new VisionSource(new Position(280, 200), 0, 45)], changedCells: []);
        Run(radar, terrain, Origin, 1);
        Assert.DoesNotContain(Contacts(radar), c => vision.IsVisible(c.Position));
        vision.Update(terrain, None, changedCells: []);
        Run(radar, terrain, Origin, 1);

        Assert.DoesNotContain(Contacts(radar), c => c.Position == new Position(300, 200));
        Assert.DoesNotContain(Contacts(radar), c => c.Echo == RadarEcho.Heat);
    }

    [Fact]
    public void RememberedGround_BlipsOnlyWhereItHasChanged()
    {
        var terrain = Dirt();
        Box(terrain, 240, 180, 250, 220, TerrainPixel.Rock);
        var vision = new VisionMap(terrain);
        // The side has seen the rock's near face, then looked away.
        vision.Update(terrain, [new VisionSource(new Position(236, 200), 0, 30)], changedCells: []);
        vision.Update(terrain, None, changedCells: []);
        Assert.True(vision.IsExplored(new Position(240, 200)) && !vision.IsVisible(new Position(240, 200)));
        var radar = new Radar(vision);

        Pulse(radar, terrain, Origin);
        Assert.Equal(0, radar.ContactCount);

        // Something tunnels through the remembered dirt in front of it: news.
        Box(terrain, 225, 190, 230, 210, TerrainPixel.Blank);
        Pulse(radar, terrain, Origin);
        var contacts = Contacts(radar);
        Assert.NotEmpty(contacts);
        Assert.All(contacts, c => Assert.Equal(RadarEcho.Cavity, c.Echo));
    }

    [Fact]
    public void APulse_RunsOutward_FindingNearThingsFirst()
    {
        var terrain = Dirt();
        Box(terrain, 190, 175, 210, 177, TerrainPixel.Rock);
        Box(terrain, 320, 180, 330, 220, TerrainPixel.Rock);
        var radar = Blind(terrain);

        Run(radar, terrain, Origin, 1);
        Assert.True(radar.PulseInFlight);
        Assert.Equal(Origin, radar.PulseOrigin);
        Assert.Equal(Tweaks.Radar.FrontCellsPerTick, radar.PulseRadius);

        // The front has run 30 cells: the rock 23 cells away has answered, the one 120 away has not.
        Run(radar, terrain, Origin, 30 / Tweaks.Radar.FrontCellsPerTick - 1);
        Assert.Equal(30, radar.PulseRadius);
        var early = Contacts(radar);
        Assert.NotEmpty(early);
        Assert.All(early, c => Assert.Equal(177, c.Position.Y));

        Run(radar, terrain, Origin, 110 / Tweaks.Radar.FrontCellsPerTick);
        Assert.Contains(Contacts(radar), c => c.Position.X == 320);
    }

    [Fact]
    public void Pulses_FinishBeforeTheNextIsSent()
    {
        var terrain = Dirt();
        var radar = Blind(terrain);
        int trip = (Tweaks.Radar.Range + 1) / Tweaks.Radar.FrontCellsPerTick + 1;
        Assert.True(trip < Tweaks.Radar.PulsePeriodTicks, "a pulse must reach full range before the next is due");
        Assert.True(Tweaks.Radar.ContactLifetimeTicks > Tweaks.Radar.PulsePeriodTicks,
            "a blip must last until the next pulse can refresh it");

        Run(radar, terrain, Origin, trip);
        Assert.False(radar.PulseInFlight);
        Run(radar, terrain, Origin, Tweaks.Radar.PulsePeriodTicks - trip);
        Assert.False(radar.PulseInFlight);
        Run(radar, terrain, Origin, 1);
        Assert.True(radar.PulseInFlight);
    }

    [Fact]
    public void APulse_KeepsTheOriginItWasSentFrom_WhileTheTankDrivesOn()
    {
        var terrain = Dirt();
        Box(terrain, 260, 180, 290, 220, TerrainPixel.Rock);
        var radar = Blind(terrain);
        var elsewhere = new Position(120, 120);

        Run(radar, terrain, Origin, 5);
        Run(radar, terrain, elsewhere, Tweaks.Radar.PulsePeriodTicks - 5);

        var contacts = Contacts(radar);
        Assert.NotEmpty(contacts);
        Assert.All(contacts, c => Assert.Equal(260, c.Position.X));
        Run(radar, terrain, elsewhere, 1);
        Assert.Equal(elsewhere, radar.PulseOrigin);
    }

    [Fact]
    public void ABlip_OfSomethingThatLeft_IsClearedByTheNextPulse()
    {
        var terrain = Dirt();
        var radar = Blind(terrain);
        RadarTarget[] there = [new RadarTarget(new Position(260, 200), 2)];
        Pulse(radar, terrain, Origin, there);
        Assert.NotEmpty(Contacts(radar));

        Pulse(radar, terrain, Origin);

        Assert.Equal(0, radar.ContactCount);
    }

    [Fact]
    public void ABlip_InTheShadowOfANewReflector_StaysUntilItFades()
    {
        var terrain = Dirt();
        var radar = Blind(terrain);
        RadarTarget[] there = [new RadarTarget(new Position(260, 200), 2)];
        Pulse(radar, terrain, Origin, there);
        var metal = Contacts(radar);
        Assert.NotEmpty(metal);

        // The target leaves behind a rock: the next pulse cannot see that it has gone.
        Box(terrain, 230, 185, 232, 215, TerrainPixel.Rock);
        Pulse(radar, terrain, Origin);
        Assert.Equal(metal, Contacts(radar).Where(c => c.Echo == RadarEcho.Metal));

        for (int i = 0; i < Tweaks.Radar.ContactLifetimeTicks; i++)
            radar.Idle();
        Assert.Equal(0, radar.ContactCount);
        Assert.False(radar.PulseInFlight);
    }

    [Fact]
    public void AReturnInTheSameCell_ReplacesTheOldOne()
    {
        var terrain = Dirt();
        Box(terrain, 260, 180, 290, 220, TerrainPixel.Rock);
        var radar = Blind(terrain);

        Pulse(radar, terrain, Origin, pulses: 2);

        var contacts = Contacts(radar);
        Assert.Equal(contacts.Count, contacts.Select(c => c.Position).Distinct().Count());
    }

    [Fact]
    public void LaterPulses_HitDifferentCells_SoAShapeFillsIn()
    {
        var terrain = Dirt();
        Box(terrain, 320, 100, 330, 300, TerrainPixel.Rock);
        var once = Blind(terrain);
        var thrice = Blind(terrain);

        Pulse(once, terrain, Origin);
        Pulse(thrice, terrain, Origin, pulses: 3);

        int Distinct(Radar radar) => Contacts(radar).Select(c => c.Position).Distinct().Count();
        Assert.True(Distinct(thrice) > Distinct(once) * 2,
            $"three pulses should hit far more cells than one: {Distinct(once)} then {Distinct(thrice)}");
    }

    [Fact]
    public void ARay_CannotSlipBetweenCellsThatTouchAtACorner()
    {
        var terrain = Dirt();
        // A rock line one cell thick whose cells touch only at their corners, and more rock behind it.
        for (int x = 170; x <= 270; x++)
            terrain.SetPixelRaw(new Position(x, 440 - x), TerrainPixel.Rock);
        Box(terrain, 232, 232, 260, 260, TerrainPixel.Rock);
        var radar = Blind(terrain);

        Pulse(radar, terrain, Origin, pulses: 4);

        var contacts = Contacts(radar);
        Assert.NotEmpty(contacts);
        Assert.All(contacts, c => Assert.Equal(440, c.Position.X + c.Position.Y));
    }

    [Fact]
    public void AReturnThatIsNotNews_StillStopsTheRay()
    {
        var terrain = Dirt();
        Box(terrain, 300, 60, 300, 340, TerrainPixel.Rock);
        Box(terrain, 303, 150, 306, 250, TerrainPixel.Gold);
        var vision = new VisionMap(terrain);
        // The side has seen the rock wall, and only the wall, then looked away.
        var wall = Enumerable.Range(60, 281).Select(y => new VisionSource(new Position(299, y), 0, 1)).ToArray();
        vision.Update(terrain, wall, changedCells: []);
        vision.Update(terrain, None, changedCells: []);
        Assert.True(vision.IsExplored(new Position(300, 200)) && !vision.IsExplored(new Position(303, 200)));
        var radar = new Radar(vision);

        Pulse(radar, terrain, Origin);

        Assert.Equal(0, radar.ContactCount);
    }

    [Fact]
    public void ACave_InTheShadowOfAReflector_GivesNoReturn()
    {
        var terrain = Dirt();
        Box(terrain, 230, 180, 233, 220, TerrainPixel.Rock);
        Box(terrain, 250, 195, 260, 205, TerrainPixel.Blank);
        var radar = Blind(terrain);

        Pulse(radar, terrain, Origin, pulses: 3);

        var contacts = Contacts(radar);
        Assert.NotEmpty(contacts);
        Assert.All(contacts, c => Assert.Equal((RadarEcho.Solid, 230), (c.Echo, c.Position.X)));
    }

    [Fact]
    public void ATarget_ReflectsExactlyLikeGoldInTheSameCells()
    {
        // Small targets scattered round the tank (all within a machine's range), seen from a dozen nearby places: a
        // ray that grazes one must still find it, whatever culling picks the targets a ray can touch.
        RadarTarget[] targets = Enumerable.Range(0, 60)
            .Select(i => new RadarTarget(new Position(170 + i * 97 % 60, 170 + i * 53 % 60), i % 3))
            .ToArray();
        var plain = Dirt();
        var gilded = Dirt();
        foreach (var t in targets)
            Box(gilded, t.Position.X - t.HalfSize, t.Position.Y - t.HalfSize,
                t.Position.X + t.HalfSize, t.Position.Y + t.HalfSize, TerrainPixel.Gold);
        var byTarget = Blind(plain);
        var byGold = Blind(gilded);

        foreach (var origin in Enumerable.Range(0, 12).Select(k => new Position(185 + k * 3, 190 + k * 7 % 25)))
        {
            Pulse(byTarget, plain, origin, targets);
            Pulse(byGold, gilded, origin);
        }

        Assert.NotEmpty(Contacts(byTarget));
        Assert.Equal(Contacts(byGold), Contacts(byTarget));
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
    public void HotGround_IsMarkedCellByCell_UntilItHasAbsorbedTheRay()
    {
        var terrain = Dirt();
        Box(terrain, 300, 180, 310, 220, TerrainPixel.Rock);
        Warm(terrain, 240, 150, 270, 250, temperature: 60);
        var radar = Blind(terrain);

        Run(radar, terrain, Origin, Tweaks.Radar.PulsePeriodTicks / 2);
        Assert.True(radar.HeatLoss is 0f, "the loss is reported once a pulse has finished");
        Run(radar, terrain, Origin, Tweaks.Radar.PulsePeriodTicks - Tweaks.Radar.PulsePeriodTicks / 2);

        var contacts = Contacts(radar);
        Assert.DoesNotContain(contacts, c => c.Echo == RadarEcho.Solid);
        Assert.NotEmpty(contacts);
        // 60 degrees takes 0.2 a cell of path: a ray gets five or six cells of path into the ground, and no further.
        Assert.All(contacts, c =>
        {
            Assert.Equal(RadarEcho.Heat, c.Echo);
            Assert.InRange(c.Position.X, 240, 247);
            Assert.Equal(1f, c.Strength);
        });
        // The ray along the axis marks each cell it went through (the sixth is float rounding of 5 * 0.2).
        Assert.Equal([240, 241, 242, 243, 244, 245],
            contacts.Where(c => c.Position.Y == Origin.Y).Select(c => c.Position.X).Order());
        Assert.InRange(radar.HeatLoss, 0.05f, 0.5f);
    }

    [Fact]
    public void WarmGround_IsPenetratedFurtherThanHotGround_AndReadsFainter()
    {
        static (int Deepest, float Strength) Probe(int temperature)
        {
            var terrain = Dirt();
            Warm(terrain, 240, 150, 300, 250, temperature);
            var radar = Blind(terrain);
            Pulse(radar, terrain, Origin);
            var onAxis = Contacts(radar).Where(c => c.Position.Y == Origin.Y).ToList();
            Assert.All(onAxis, c => Assert.Equal(RadarEcho.Heat, c.Echo));
            return (onAxis.Max(c => c.Position.X), onAxis[0].Strength);
        }

        var hot = Probe(60);
        var warm = Probe(25);
        Assert.Equal(245, hot.Deepest);
        // 25 degrees takes 0.06 a cell, so the ray would last 17 cells: the marks stop at the cap instead.
        Assert.Equal(240 + Tweaks.Radar.HeatReturnsPerRay - 1, warm.Deepest);
        Assert.Equal(1f, hot.Strength);
        Assert.InRange(warm.Strength, 0.25f, 0.35f);
    }

    [Fact]
    public void HeatMarks_OfGroundThatHasCooled_AreClearedByTheNextPulse()
    {
        var terrain = Dirt();
        Box(terrain, 300, 180, 310, 220, TerrainPixel.Rock);
        Warm(terrain, 240, 150, 270, 250, temperature: 60);
        var radar = Blind(terrain);
        Pulse(radar, terrain, Origin);
        Assert.Contains(Contacts(radar), c => c.Echo == RadarEcho.Heat);

        for (int y = 150; y <= 250; y++)
            for (int x = 240; x <= 270; x++)
                terrain.AddHeat(new Position(x, y), -60);
        Pulse(radar, terrain, Origin);

        var contacts = Contacts(radar);
        Assert.DoesNotContain(contacts, c => c.Echo == RadarEcho.Heat);
        Assert.Contains(contacts, c => c.Echo == RadarEcho.Solid);
        Assert.Equal(0f, radar.HeatLoss);
    }

    [Fact]
    public void WarmGround_LetsTheRaysThroughWeaker()
    {
        var terrain = Dirt();
        Box(terrain, 300, 180, 310, 220, TerrainPixel.Rock);
        Warm(terrain, 240, 199, 250, 201, temperature: 20);
        var radar = Blind(terrain);

        Pulse(radar, terrain, Origin);

        var contacts = Contacts(radar);
        Assert.All(contacts.Where(c => c.Echo == RadarEcho.Heat),
            c => Assert.True(c.Position.X is >= 240 and <= 250 && c.Position.Y is >= 199 and <= 201));
        Assert.All(contacts.Where(c => c.Echo != RadarEcho.Heat), c => Assert.Equal(RadarEcho.Solid, c.Echo));
        RadarContact throughWarm = contacts.Single(c => c.Position == new Position(300, 200));
        RadarContact throughCool = contacts.First(c => c.Echo == RadarEcho.Solid && c.Position.Y < 195);
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

        Pulse(radar, terrain, Origin);

        var contacts = Contacts(radar);
        Assert.NotEmpty(contacts);
        Assert.All(contacts, c =>
        {
            Assert.Equal(RadarEcho.Heat, c.Echo);
            Assert.Equal(260, c.Position.X);
        });
    }

    [Fact]
    public void ColdGround_DoesNotWeakenTheRays()
    {
        var terrain = Dirt();
        Box(terrain, 300, 180, 310, 220, TerrainPixel.Rock);
        Warm(terrain, 240, 150, 270, 250, temperature: -60);
        var radar = Blind(terrain);

        Pulse(radar, terrain, Origin);

        var contacts = Contacts(radar);
        Assert.NotEmpty(contacts);
        Assert.All(contacts, c => Assert.Equal((RadarEcho.Solid, 1f), (c.Echo, c.Strength)));
    }

    [Fact]
    public void AWorld_RunsTheRadar_WithoutChangingTheSimulation()
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
