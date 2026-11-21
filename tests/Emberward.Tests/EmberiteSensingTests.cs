using Emberward.Core;
using Emberward.Core.Collision;
using Emberward.Core.Config;
using Emberward.Core.Entities.Emberites;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Sensing;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using Emberward.Desktop.Gui.Guidance;

namespace Emberward.Tests;

/// <summary>
/// What emberites and the radar sense: rock stops a creature's sight and soil does not, a cool tank in sight stays
/// hunted and one behind rock is lost, the heat leash shortens as a tank cools, a cool tank gives no radar echo, and
/// the local side hears of a nest waking only when it sees or reads the nest.
/// </summary>
public class EmberiteSensingTests
{
    // Clear of both bases of the 320 x 200 test map, which stand at (130, 21) and (185, 178).
    private static readonly Position Centre = new(60, 100);

    private static (World World, Tank Tank) Arena(TerrainPixel ground)
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false, emberites: true);
        for (int y = -30; y <= 30; y++)
            for (int x = -60; x <= 60; x++)
                world.Terrain.SetPixel(Centre + new Offset(x, y), ground);
        return (world, world.TankList.Tanks[0]);
    }

    private static List<StepEvent> Tick(World world, Tank tank, int ticks, float heat)
    {
        var events = new List<StepEvent>();
        for (int i = 0; i < ticks; i++)
        {
            tank.Heat = heat;
            world.Advance(_ => default);
            events.AddRange(world.StepEvents.ToArray());
        }
        return events;
    }

    /// <summary>A wall of <paramref name="pixel"/> across the whole map between the tank (60) and a nest to its right.</summary>
    private static void Wall(World world, TerrainPixel pixel)
    {
        for (int y = 0; y < world.Terrain.Height; y++)
            for (int x = 70; x <= 74; x++)
                world.Terrain.SetPixel(new Position(x, y), pixel);
    }

    [Fact]
    public void ARockWall_KeepsADormantNestAsleep_ButDirtDoesNot()
    {
        var (rock, rockTank) = Arena(TerrainPixel.Blank);
        Wall(rock, TerrainPixel.Rock);
        var rockNest = rock.Emberites!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 6);
        rockTank.Position = Centre;
        Tick(rock, rockTank, 90, heat: 90f);
        Assert.False(rockNest.Awake);

        var (dirt, dirtTank) = Arena(TerrainPixel.Blank);
        Wall(dirt, TerrainPixel.Dirt);
        var dirtNest = dirt.Emberites!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 6);
        dirtTank.Position = Centre;
        Tick(dirt, dirtTank, 90, heat: 90f);
        Assert.True(dirtNest.Awake);
    }

    [Theory]
    [InlineData(TerrainPixel.Rock, false)]
    [InlineData(TerrainPixel.HotRock, false)]
    [InlineData(TerrainPixel.FrostRock, false)]
    [InlineData(TerrainPixel.Concrete, false)]
    [InlineData(TerrainPixel.Dirt, true)]
    [InlineData(TerrainPixel.DirtGrow, true)]
    [InlineData(TerrainPixel.Pyrite, true)]
    [InlineData(TerrainPixel.Blank, true)]
    public void OnlyStoneBlocksACreaturesLine(TerrainPixel pixel, bool clear)
    {
        var terrain = new TerrainGrid(new Size(40, 11));
        terrain.Fill(TerrainPixel.Blank);
        terrain.SetPixelRaw(new Position(20, 5), pixel);
        Assert.Equal(clear, Raycaster.HasCreatureLine(terrain, new Position(2, 5), new Position(38, 5)));
    }

    [Fact]
    public void ACoolTankInSight_StaysHunted()
    {
        var (world, tank) = Arena(TerrainPixel.Blank);
        world.Emberites!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 12);
        tank.Position = Centre;
        Tick(world, tank, 20, heat: 80f);
        Assert.True(world.Emberites.OutCount > 0);

        // Cooler than the heat leash allows at any range: only sight keeps it.
        var events = Tick(world, tank, 120, heat: 20f);
        Assert.Contains(events, e => e.Kind == StepEventKind.EmberiteBit);
    }

    [Fact]
    public void ACoolTankBehindRock_IsLost()
    {
        var (world, tank) = Arena(TerrainPixel.Blank);
        var nest = world.Emberites!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 12);
        tank.Position = Centre;
        Tick(world, tank, 8, heat: 80f);
        Wall(world, TerrainPixel.Rock);

        Tick(world, tank, 400, heat: 20f);
        var late = Tick(world, tank, 100, heat: 20f);

        Assert.DoesNotContain(late, e => e.Kind == StepEventKind.EmberiteBit);
        Assert.False(nest.Awake);
        Assert.Equal(0, world.Emberites.OutCount);
    }

    [Fact]
    public void TheHeatLeash_ShrinksAsTheTankCools()
    {
        Assert.Equal(0f, EmberiteSwarm.HeatLeash(Tweaks.Emberites.LoseScentHeat - 0.1f));
        Assert.Equal(Tweaks.Emberites.HuntLeashMinCells, EmberiteSwarm.HeatLeash(Tweaks.Emberites.LoseScentHeat));
        Assert.Equal(Tweaks.Emberites.HuntLeashCells, EmberiteSwarm.HeatLeash(Tweaks.Emberites.FullHeat));
        Assert.True(EmberiteSwarm.HeatLeash(80f) > EmberiteSwarm.HeatLeash(60f));
        Assert.True(EmberiteSwarm.HeatLeash(60f) > EmberiteSwarm.HeatLeash(40f));

        // Behind rock, only the heat leash can keep the nest on a tank: 80 cells off is inside it at 48 degrees, 100 is not.
        float leash = EmberiteSwarm.HeatLeash(48f);
        Assert.InRange(leash, 80f, 100f);
        foreach (var (x, kept) in new[] { (20, true), (0, false) })
        {
            var (world, tank) = Arena(TerrainPixel.Blank);
            var nest = world.Emberites!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 12);
            tank.Position = Centre;
            Tick(world, tank, 8, heat: 80f);
            Wall(world, TerrainPixel.Rock);
            tank.Position = new Position(x, 100);
            Tick(world, tank, 18, heat: 48f);
            Assert.Equal(kept, nest.Target == 0);
        }
    }

    [Fact]
    public void ACoolTank_GivesNoLifeEcho_AHotterOneAFurtherOne()
    {
        Assert.Equal(0f, TankSignature.Of(Tweaks.Radar.SignatureFloorHeat - 0.1f));
        Assert.Equal(1f, TankSignature.Of(Tweaks.Radar.SignatureFullHeat));
        Assert.True(TankSignature.Of(80f) > TankSignature.Of(60f));

        int cool = LifeEchoes(20f, distance: 10);
        int warm = LifeEchoes(60f, distance: 10);
        Assert.Equal(0, cool);
        Assert.True(warm > 0);

        // A tank at half signature shows from half the range, and a hot one from all of it.
        int distance = Tweaks.Radar.LifeRange - 8;
        float half = (Tweaks.Radar.SignatureFloorHeat + Tweaks.Radar.SignatureFullHeat) / 2f;
        Assert.Equal(0, LifeEchoes(half, distance));
        Assert.True(LifeEchoes(Tweaks.Radar.SignatureFullHeat, distance) > 0);
    }

    private static int LifeEchoes(float heat, int distance)
    {
        var terrain = new TerrainGrid(new Size(401, 401));
        terrain.Fill(TerrainPixel.Dirt);
        var radar = new Radar(new VisionMap(terrain));
        var origin = new Position(200, 200);
        RadarTarget[] targets = [new RadarTarget(new Position(200 + distance, 200), 3, Alive: true, Signature: TankSignature.Of(heat))];
        for (int i = 0; i < Tweaks.Radar.PulsePeriodTicks; i++)
            radar.Advance(terrain, terrain, origin, ownColor: 0, targets);
        int count = 0;
        for (int i = 0; i < radar.ContactCount; i++)
            if (radar.ContactAt(i).Echo == RadarEcho.Life)
                count++;
        return count;
    }

    [Fact]
    public void ANestIsCoveredOnlyBySightOrRadar()
    {
        var (world, player) = Arena(TerrainPixel.Blank);
        player.Position = Centre;
        var near = Centre + new Offset(30, 0);
        var mid = Centre + new Offset(55, 0);
        var far = Centre + new Offset(58, 20);
        world.Emberites!.AddNest(near, radius: 4, creatures: 3);
        world.Emberites.AddNest(mid, radius: 12, creatures: 3); // wide enough for the radar's rays to find
        world.TrackVision(player);
        Tick(world, player, 3, heat: 20f);

        // In sight (45 cells), and past it with no radar tracked: not covered.
        Assert.True(NestAlert.Covers(world, player, near));
        Assert.False(NestAlert.Covers(world, player, mid));
        Assert.False(NestAlert.Covers(world, player, far));

        // With the radar running, the nest 55 cells off shows as a life echo and is covered.
        world.TrackRadar(player);
        Tick(world, player, 3 * Tweaks.Radar.PulsePeriodTicks, heat: 20f);
        Assert.True(NestAlert.Covers(world, player, mid));
        Assert.False(NestAlert.Covers(world, player, far));
    }

    [Fact]
    public void ACreatureThatHasLookedForSight_RoundTripsThroughASnapshot()
    {
        var (world, tank) = Arena(TerrainPixel.Blank);
        world.Emberites!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 12);
        tank.Position = Centre;
        Tick(world, tank, 20, heat: 80f);
        // Cool: creatures now keep the tank by sight, so each has looked (SightTick, Sees) when the snapshot is taken.
        Tick(world, tank, 12, heat: 20f);
        Assert.True(world.Emberites.OutCount > 0);

        SnapshotRoundTrip.AssertPlaysOnAlike(world, [null, null],
            () => TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false, emberites: true), ticks: 60,
            (_, _) => default, firstFrame: 32, before: (w, _) => w.TankList.Tanks[0].Heat = 20f);
    }
}
