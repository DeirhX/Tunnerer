using Tunnerer.Core.Config;
using Tunnerer.Core.Sensing;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

using Tunnerer.Core;
/// <summary>
/// Fog of war: a side sees along open lines of sight from its tank, base and machines, remembers the terrain as it
/// last saw it, pays only for what changes, and none of this changes the simulation.
/// </summary>
public class VisionMapTests
{
    private static readonly VisionSource[] None = [];
    private static readonly Position[] NoChanges = [];

    private static TerrainGrid Grid(int w, int h, TerrainPixel fill)
    {
        var terrain = new TerrainGrid(new Size(w, h));
        terrain.Fill(fill);
        return terrain;
    }

    private static VisionMap See(TerrainGrid terrain, params VisionSource[] sources)
    {
        var map = new VisionMap(terrain);
        map.Update(terrain, sources, NoChanges);
        return map;
    }

    [Fact]
    public void InOpenGround_EveryCellWithinTheRadiusIsSeen_AndNoneBeyond()
    {
        var terrain = Grid(121, 121, TerrainPixel.Blank);
        var eye = new Position(60, 60);
        const int radius = 45;
        var map = See(terrain, new VisionSource(eye, radius, 0));

        for (int y = 0; y < terrain.Height; y++)
        {
            for (int x = 0; x < terrain.Width; x++)
            {
                var cell = new Position(x, y);
                bool inside = Position.DistanceSquared(cell, eye) <= radius * radius;
                Assert.True(map.IsVisible(cell) == inside, $"cell {cell} visible={map.IsVisible(cell)}, inside={inside}");
            }
        }
    }

    [Fact]
    public void TheLevelIsFullNearTheSource_AndFadesTowardTheEdge()
    {
        var terrain = Grid(121, 121, TerrainPixel.Blank);
        var map = See(terrain, new VisionSource(new Position(60, 60), 45, 0));

        int At(int dx) => map.LevelAt(60 + dx + 60 * terrain.Width);
        Assert.Equal(255, At(0));
        Assert.Equal(255, At(45 - Tweaks.Vision.EdgeFade));
        Assert.InRange(At(43), 1, 254);
        Assert.True(At(44) < At(42), "the level should fall toward the edge");
        Assert.Equal(0, At(46));
    }

    [Fact]
    public void AWallIsSeenOnlySkinDeep_AndHidesWhatIsBehindIt()
    {
        var terrain = Grid(100, 21, TerrainPixel.Blank);
        for (int y = 0; y < terrain.Height; y++)
            for (int x = 40; x < 60; x++)
                terrain.SetPixelRaw(new Position(x, y), TerrainPixel.Rock);
        var map = See(terrain, new VisionSource(new Position(20, 10), 45, 0));

        Assert.True(map.IsVisible(new Position(39, 10)), "open ground in front of the wall");
        Assert.True(map.IsVisible(new Position(40, 10)), "the face of the wall");
        Assert.True(map.IsVisible(new Position(40 + Tweaks.Vision.WallSkin - 1, 10)), "the wall's skin");
        Assert.False(map.IsVisible(new Position(40 + Tweaks.Vision.WallSkin, 10)), "deeper than the skin");
        Assert.False(map.IsVisible(new Position(62, 10)), "open ground behind the wall, within the radius");
    }

    [Fact]
    public void TheNearRadiusSeesThroughSolidGround()
    {
        var terrain = Grid(61, 61, TerrainPixel.Dirt);
        var eye = new Position(30, 30);
        terrain.SetPixelRaw(eye, TerrainPixel.Blank);
        var map = See(terrain, new VisionSource(eye, 45, Tweaks.Vision.TankNearRadius));

        Assert.True(map.IsVisible(eye + new Offset(Tweaks.Vision.TankNearRadius, 0)));
        Assert.True(map.IsVisible(eye + new Offset(-4, 4)));
        Assert.False(map.IsVisible(eye + new Offset(Tweaks.Vision.TankNearRadius + 1, 0)));
    }

    [Fact]
    public void GroundOnceSeen_StaysExplored_AfterTheSourceMovesAway()
    {
        var terrain = Grid(200, 21, TerrainPixel.Blank);
        var map = new VisionMap(terrain);
        var left = new Position(20, 10);

        map.Update(terrain, [new VisionSource(left, 15, 0)], NoChanges);
        Assert.True(map.IsVisible(left) && map.IsExplored(left));
        Assert.False(map.IsExplored(new Position(150, 10)));

        map.Update(terrain, [new VisionSource(new Position(150, 10), 15, 0)], NoChanges);
        Assert.False(map.IsVisible(left));
        Assert.True(map.IsExplored(left));
        Assert.True(map.IsVisible(new Position(150, 10)));
    }

    [Fact]
    public void KnownTerrain_FollowsTheWorldInSight_AndKeepsTheLastSeenPixelOutOfSight()
    {
        var terrain = Grid(200, 21, TerrainPixel.Blank);
        var map = new VisionMap(terrain);
        var seen = new Position(25, 10);
        var unseen = new Position(150, 10);
        VisionSource[] eye = [new VisionSource(new Position(20, 10), 15, 0)];
        map.Update(terrain, eye, NoChanges);

        terrain.SetPixelRaw(seen, TerrainPixel.Rock);
        terrain.SetPixelRaw(unseen, TerrainPixel.Rock);
        map.Update(terrain, eye, [seen, unseen]);

        Assert.Equal(TerrainPixel.Rock, map.Known.GetPixelRaw(seen));
        Assert.Equal(TerrainPixel.Blank, map.Known.GetPixelRaw(unseen));
        Assert.Equal([seen], map.KnownChanges);

        // Coming into sight brings the remembered pixel up to date; the change list holds only that update's cells.
        map.Update(terrain, [new VisionSource(new Position(145, 10), 15, 0)], NoChanges);
        Assert.Equal(TerrainPixel.Rock, map.Known.GetPixelRaw(unseen));
        Assert.Equal([unseen], map.KnownChanges);

        map.Update(terrain, [new VisionSource(new Position(145, 10), 15, 0)], NoChanges);
        Assert.Empty(map.KnownChanges);
    }

    [Fact]
    public void TheDirtyRect_CoversWhatChanged_AndStaysEmptyWhileNothingMoves()
    {
        var terrain = Grid(200, 41, TerrainPixel.Blank);
        var map = new VisionMap(terrain);
        VisionSource[] eye = [new VisionSource(new Position(30, 20), 10, 0)];

        map.Update(terrain, eye, NoChanges);
        Assert.True(map.TryGetDirtyRect(out Rect first));
        Assert.Equal(new Rect(20, 10, 21, 21), first);

        map.ClearDirtyRect();
        map.Update(terrain, eye, NoChanges);
        Assert.False(map.TryGetDirtyRect(out _));

        // One cell to the right: the rect spans both discs, not the map.
        map.Update(terrain, [new VisionSource(new Position(31, 20), 10, 0)], NoChanges);
        Assert.True(map.TryGetDirtyRect(out Rect moved));
        Assert.Equal(new Rect(20, 10, 22, 21), moved);
    }

    [Fact]
    public void ASourceNoLongerListed_StopsSeeing_AndTheOthersKeepWhatTheySee()
    {
        var terrain = Grid(200, 21, TerrainPixel.Blank);
        var map = new VisionMap(terrain);
        var post = new VisionSource(new Position(150, 10), 10, 0);
        var neighbour = new VisionSource(new Position(160, 10), 10, 0);
        var between = new Position(155, 10);

        map.Update(terrain, [post, neighbour], NoChanges);
        byte both = map.LevelAt(between.X + between.Y * terrain.Width);

        map.Update(terrain, [neighbour], NoChanges);
        Assert.False(map.IsVisible(new Position(141, 10)), "only the lost source saw this");
        Assert.Equal(both, map.LevelAt(between.X + between.Y * terrain.Width));

        map.Update(terrain, None, NoChanges);
        Assert.False(map.IsVisible(between));
    }

    [Fact]
    public void SourcesThatStandStill_InGroundThatStaysAsItIs_CostNothing()
    {
        var terrain = TunnelThroughDirt();
        var map = new VisionMap(terrain);
        VisionSource[] sources = [Tank(new Position(40, 30)), Relay(new Position(80, 30)), Relay(new Position(120, 30))];

        map.Update(terrain, sources, NoChanges);
        Assert.True(map.CellsCastInLastUpdate > 0);
        map.ClearDirtyRect();

        map.Update(terrain, sources, NoChanges);
        Assert.Equal(0, map.CellsCastInLastUpdate);
        Assert.False(map.TryGetDirtyRect(out _));
    }

    [Fact]
    public void APixelChangeThatBlocksSightNoDifferently_CastsNothing_ButIsRemembered()
    {
        var terrain = TunnelThroughDirt();
        var map = new VisionMap(terrain);
        VisionSource[] sources = [Tank(new Position(40, 30)), Relay(new Position(80, 30))];
        map.Update(terrain, sources, NoChanges);

        // A scorch mark on open ground, and dirt that turns to rock: neither opens or closes a line of sight.
        var scorched = new Position(60, 30);
        var hardened = new Position(80, 40);
        terrain.SetPixelRaw(scorched, TerrainPixel.Decal);
        terrain.SetPixelRaw(hardened, TerrainPixel.Rock);
        map.Update(terrain, sources, [scorched, hardened]);

        Assert.Equal(0, map.CellsCastInLastUpdate);
        Assert.Equal(TerrainPixel.Decal, map.Known.GetPixelRaw(scorched));
        Assert.Equal(TerrainPixel.Rock, map.Known.GetPixelRaw(hardened));
    }

    [Fact]
    public void DiggingBesideAMachine_RepairsOnlyTheCellsAroundTheDig()
    {
        var terrain = TunnelThroughDirt();
        var map = new VisionMap(terrain);
        var relay = Relay(new Position(80, 30));
        // The other relay is far too far away for the dig to reach it.
        VisionSource[] sources = [relay, Relay(new Position(160, 30))];
        map.Update(terrain, sources, NoChanges);

        var dug = new Position(84, 36);
        terrain.SetPixelRaw(dug, TerrainPixel.Blank);
        map.Update(terrain, sources, [dug]);

        int window = 2 * (Tweaks.Vision.WallSkin + 1) + 1;
        Assert.Equal(window * window, map.CellsCastInLastUpdate);
        AssertSameAsSeenAfresh(terrain, map, sources);
    }

    [Fact]
    public void GroundThatChangesWhereASourceLooks_IsSeenAsItNowIs()
    {
        var terrain = TunnelThroughDirt();
        var map = new VisionMap(terrain);
        VisionSource[] sources = [Tank(new Position(40, 30))];
        var behind = new Position(70, 30);
        map.Update(terrain, sources, NoChanges);
        Assert.True(map.IsVisible(behind));

        // The tunnel caves in between the tank and the cell, and is dug out again.
        var plug = new List<Position>();
        for (int y = 26; y <= 34; y++)
            for (int x = 55; x <= 57; x++)
                plug.Add(new Position(x, y));
        foreach (Position cell in plug)
            terrain.SetPixelRaw(cell, TerrainPixel.Dirt);
        map.Update(terrain, sources, plug);
        Assert.False(map.IsVisible(behind));
        AssertSameAsSeenAfresh(terrain, map, sources);

        foreach (Position cell in plug)
            terrain.SetPixelRaw(cell, TerrainPixel.Blank);
        map.Update(terrain, sources, plug);
        Assert.True(map.IsVisible(behind));
        AssertSameAsSeenAfresh(terrain, map, sources);
    }

    /// <summary>
    /// Whatever happens (ground dug, filled and scorched, sources moving, appearing and leaving), a map that was
    /// kept up to date step by step shows exactly what a map that looks at the same world for the first time shows,
    /// and remembers every cell in sight as it is.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AMapKeptUpToDate_ShowsWhatAFreshLookShows(int seed)
    {
        var random = new Random(seed);
        var terrain = Grid(160, 110, TerrainPixel.Dirt);
        for (int i = 0; i < 40; i++)
            Blob(terrain, random, TerrainPixel.Blank, changed: null);
        var map = new VisionMap(terrain);

        var sources = new List<VisionSource>
        {
            Tank(new Position(50, 50)),
            new(new Position(30, 30), Tweaks.Coverage.BaseRadius, Tweaks.Vision.MachineNearRadius),
        };
        Position Anywhere() => new(random.Next(-5, terrain.Width + 5), random.Next(-5, terrain.Height + 5));
        for (int i = 0; i < 12; i++)
            sources.Add(i % 3 == 0 ? Harvester(Anywhere()) : Relay(Anywhere()));

        var changed = new List<Position>();
        for (int step = 0; step < 120; step++)
        {
            changed.Clear();
            int blobs = random.Next(0, 4);
            for (int i = 0; i < blobs; i++)
                Blob(terrain, random, random.Next(4) switch
                {
                    0 => TerrainPixel.Dirt,
                    1 => TerrainPixel.Decal,
                    2 => TerrainPixel.Rock,
                    _ => TerrainPixel.Blank,
                }, changed);

            // The tank drives, a harvester crawls, and now and then a machine is planted or lost.
            sources[0] = sources[0] with { Position = sources[0].Position + new Offset(random.Next(-1, 2), random.Next(-1, 2)) };
            int crawler = 2 + random.Next(sources.Count - 2);
            if (sources[crawler].Radius == Tweaks.Vision.MachineNearRadius)
                sources[crawler] = sources[crawler] with { Position = sources[crawler].Position + new Offset(1, 0) };
            if (step % 10 == 3)
                sources.RemoveAt(2 + random.Next(sources.Count - 2));
            if (step % 10 == 7)
                sources.Add(Relay(Anywhere()));

            map.Update(terrain, sources, changed);
            AssertSameAsSeenAfresh(terrain, map, sources);
        }
    }

    private static VisionSource Tank(Position at) => new(at, Tweaks.Vision.TankRadius, Tweaks.Vision.TankNearRadius);

    private static VisionSource Relay(Position at) => new(at, Tweaks.Coverage.NodeRadius, Tweaks.Coverage.NodeRadius);

    private static VisionSource Harvester(Position at) =>
        new(at, Tweaks.Vision.MachineNearRadius, Tweaks.Vision.MachineNearRadius);

    /// <summary>Dirt with one tunnel, 9 cells high, along the middle.</summary>
    private static TerrainGrid TunnelThroughDirt()
    {
        var terrain = Grid(200, 61, TerrainPixel.Dirt);
        for (int y = 26; y <= 34; y++)
            for (int x = 10; x < 190; x++)
                terrain.SetPixelRaw(new Position(x, y), TerrainPixel.Blank);
        return terrain;
    }

    private static void Blob(TerrainGrid terrain, Random random, TerrainPixel pixel, List<Position>? changed)
    {
        int cx = random.Next(terrain.Width), cy = random.Next(terrain.Height), r = random.Next(1, 7);
        for (int y = Math.Max(0, cy - r); y <= Math.Min(terrain.Height - 1, cy + r); y++)
            for (int x = Math.Max(0, cx - r); x <= Math.Min(terrain.Width - 1, cx + r); x++)
            {
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > r * r)
                    continue;
                terrain.SetPixelRaw(new Position(x, y), pixel);
                changed?.Add(new Position(x, y));
            }
    }

    private static void AssertSameAsSeenAfresh(TerrainGrid terrain, VisionMap map, IReadOnlyList<VisionSource> sources)
    {
        var fresh = new VisionMap(terrain);
        fresh.Update(terrain, sources, NoChanges);
        for (int offset = 0; offset < terrain.Width * terrain.Height; offset++)
        {
            if (map.LevelAt(offset) != fresh.LevelAt(offset))
                Assert.Fail($"cell ({offset % terrain.Width}, {offset / terrain.Width}): level {map.LevelAt(offset)}, " +
                    $"a fresh look gives {fresh.LevelAt(offset)}");
            if (map.IsVisible(offset) && map.Known.GetPixelRaw(offset) != terrain.GetPixelRaw(offset))
                Assert.Fail($"cell ({offset % terrain.Width}, {offset / terrain.Width}) is in sight but remembered as " +
                    $"{map.Known.GetPixelRaw(offset)}, not {terrain.GetPixelRaw(offset)}");
            Assert.True(map.IsExplored(offset) || !map.IsVisible(offset));
        }
    }

    [Fact]
    public void AWorld_ShowsItsSideTheBaseTheTankAndRelays()
    {
        var s = RelayHeatTests.CreateScenario();
        var relay = RelayHeatTests.PlantRelay(s);
        var far = s.RelayPos + new Offset(0, s.DirY * 15);
        s.Tank.Position = s.Base.Position;

        VisionMap map = s.World.TrackVision(s.Tank);
        Assert.True(map.IsVisible(s.Base.Position), "the base sees itself");
        Assert.False(relay.LinkPoint is { IsPowered: true });
        Assert.True(map.IsVisible(s.RelayPos));
        Assert.True(map.IsVisible(far), "a relay sees along the tunnel, linked or not");
        Assert.True(map.IsVisible(s.RelayPos + new Offset(10, 10)), "and its whole circle, through the ground");
        Assert.False(map.IsVisible(s.RelayPos + new Offset(Tweaks.Coverage.NodeRadius + 2, 0)));
        Assert.Same(map, s.World.TrackVision(s.Tank));
    }

    [Fact]
    public void AWorld_ShowsGroundDugInSight_AndPaysNothingOnATickWhereNothingChanges()
    {
        var s = RelayHeatTests.CreateScenario();
        RelayHeatTests.PlantRelay(s);
        s.Tank.Position = s.Base.Position;
        VisionMap map = s.World.TrackVision(s.Tank);
        var terrain = s.World.Terrain;
        var dug = s.RelayPos + new Offset(10, 3);
        Assert.Equal(TerrainPixel.Dirt, terrain.GetPixelRaw(dug));
        Assert.True(map.IsVisible(dug), "the relay sees its whole circle");

        terrain.SetPixel(dug, TerrainPixel.Blank);
        s.World.Advance(_ => TestSimulation.Idle);
        Assert.Equal(TerrainPixel.Blank, map.Known.GetPixelRaw(dug));
        Assert.Contains(dug, map.KnownChanges);
        Assert.InRange(map.CellsCastInLastUpdate, 1, 2000);

        // The change list is kept to one step's worth, so the dig is not looked at again.
        Assert.Contains(dug, terrain.GetChangeList());
        int quietTicks = 0;
        for (int i = 0; i < 10; i++)
        {
            s.World.Advance(_ => TestSimulation.Idle);
            Assert.True(terrain.GetChangeList().Count < 500, "the change list must not pile up step after step");
            if (terrain.GetChangeList().Count == 0)
            {
                Assert.Equal(0, map.CellsCastInLastUpdate);
                quietTicks++;
            }
        }
        Assert.True(quietTicks > 0, "the scenario should have ticks on which no pixel changes");
    }

    [Fact]
    public void TrackingVision_DoesNotChangeTheSimulation()
    {
        static ulong Run(bool track)
        {
            var world = TestHelpers.CreateSeededWorld(seed: 777);
            if (track)
                world.TrackVision(world.TankList.Tanks[0]);
            for (int i = 0; i < 150; i++)
                world.Advance(_ => new Tunnerer.Core.Input.ControllerOutput { MoveSpeed = new Offset(1, 0), ShootPrimary = i % 20 == 0 });
            return WorldStateHash.Compute(world);
        }

        Assert.Equal(Run(track: false), Run(track: true));
    }
}
