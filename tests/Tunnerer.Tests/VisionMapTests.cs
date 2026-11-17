using Tunnerer.Core.Config;
using Tunnerer.Core.Sensing;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

using Tunnerer.Core;
/// <summary>
/// Fog of war: a side sees along open lines of sight from its tank, base and machines, remembers the terrain as it
/// last saw it, and none of this changes the simulation.
/// </summary>
public class VisionMapTests
{
    private static readonly VisionSource[] None = [];

    private static TerrainGrid Grid(int w, int h, TerrainPixel fill)
    {
        var terrain = new TerrainGrid(new Size(w, h));
        terrain.Fill(fill);
        return terrain;
    }

    private static VisionMap See(TerrainGrid terrain, params VisionSource[] moving)
    {
        var map = new VisionMap(terrain);
        map.Update(terrain, None, moving);
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

        map.Update(terrain, None, [new VisionSource(left, 15, 0)]);
        Assert.True(map.IsVisible(left) && map.IsExplored(left));
        Assert.False(map.IsExplored(new Position(150, 10)));

        map.Update(terrain, None, [new VisionSource(new Position(150, 10), 15, 0)]);
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
        map.Update(terrain, None, eye);

        terrain.SetPixelRaw(seen, TerrainPixel.Rock);
        terrain.SetPixelRaw(unseen, TerrainPixel.Rock);
        map.Update(terrain, None, eye);

        Assert.Equal(TerrainPixel.Rock, map.Known.GetPixelRaw(seen));
        Assert.Equal(TerrainPixel.Blank, map.Known.GetPixelRaw(unseen));
        Assert.Equal([seen], map.KnownChanges);

        // Coming into sight brings the remembered pixel up to date; the change list holds only that update's cells.
        map.Update(terrain, None, [new VisionSource(new Position(145, 10), 15, 0)]);
        Assert.Equal(TerrainPixel.Rock, map.Known.GetPixelRaw(unseen));
        Assert.Equal([unseen], map.KnownChanges);

        map.Update(terrain, None, [new VisionSource(new Position(145, 10), 15, 0)]);
        Assert.Empty(map.KnownChanges);
    }

    [Fact]
    public void TheDirtyRect_CoversWhatChanged_AndStaysEmptyWhileNothingMoves()
    {
        var terrain = Grid(200, 41, TerrainPixel.Blank);
        var map = new VisionMap(terrain);
        VisionSource[] eye = [new VisionSource(new Position(30, 20), 10, 0)];

        map.Update(terrain, None, eye);
        Assert.True(map.TryGetDirtyRect(out Rect first));
        Assert.Equal(new Rect(20, 10, 21, 21), first);

        map.ClearDirtyRect();
        map.Update(terrain, None, eye);
        Assert.False(map.TryGetDirtyRect(out _));

        // One cell to the right: the rect spans both discs, not the map.
        map.Update(terrain, None, [new VisionSource(new Position(31, 20), 10, 0)]);
        Assert.True(map.TryGetDirtyRect(out Rect moved));
        Assert.Equal(new Rect(20, 10, 22, 21), moved);
    }

    [Fact]
    public void FixedSources_KeepTheirLastCast_OnUpdatesThatSkipThem()
    {
        var terrain = Grid(200, 21, TerrainPixel.Blank);
        var map = new VisionMap(terrain);
        var post = new Position(150, 10);

        map.Update(terrain, [new VisionSource(post, 10, 0)], None);
        map.Update(terrain, fixedSources: null, None);
        Assert.True(map.IsVisible(post));

        map.Update(terrain, None, None);
        Assert.False(map.IsVisible(post));
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
