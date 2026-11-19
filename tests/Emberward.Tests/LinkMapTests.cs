namespace Emberward.Tests;

using Emberward.Core.Config;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using Emberward.Core.Network;

/// <summary>
/// The link map keeps its links between refreshes and looks again only at what changed. These tests hold it to the
/// result a map built from nothing gives, and to doing nothing where nothing changed.
/// </summary>
public class LinkMapTests
{
    private static readonly TimeSpan Refresh = Tweaks.World.RefreshLinkMapInterval;

    private static TerrainGrid Open(int width = 400, int height = 200)
    {
        var terrain = new TerrainGrid(new Size(width, height));
        terrain.Fill(TerrainPixel.Blank);
        return terrain;
    }

    /// <summary>A base and a chain of points along row 100, each in live range of the next.</summary>
    private static (LinkMap Map, LinkPoint Base, LinkPoint[] Chain) Chain(TerrainGrid terrain, int count = 4)
    {
        var map = new LinkMap();
        var home = map.RegisterPoint(new Position(20, 100), LinkPointType.Base);
        var chain = new LinkPoint[count];
        for (int i = 0; i < count; i++)
            chain[i] = map.RegisterPoint(new Position(20 + 80 * (i + 1), 100), LinkPointType.Machine);
        map.Advance(terrain, Refresh);
        return (map, home, chain);
    }

    [Fact]
    public void NothingChanged_NoSightlineIsLookedAt()
    {
        var terrain = Open();
        var (map, _, chain) = Chain(terrain);
        Assert.All(chain, p => Assert.True(p.IsPowered));
        Assert.True(map.SightlinesCheckedInLastAdvance > 0, "the first refresh looks at every link");

        for (int i = 0; i < 10; i++)
        {
            map.Advance(terrain, Refresh);
            Assert.Equal(0, map.SightlinesCheckedInLastAdvance);
        }

        // Ground that changes away from every link, and a change on a link that blocks no differently, cost nothing.
        terrain.SetPixel(new Position(60, 20), TerrainPixel.Dirt);
        terrain.SetPixel(new Position(60, 100), TerrainPixel.Decal);
        map.Advance(terrain, Refresh);
        Assert.Equal(0, map.SightlinesCheckedInLastAdvance);
    }

    [Fact]
    public void GroundThatBlocksALink_CutsThePowerBehindIt_AndClearingItBringsThePowerBack()
    {
        var terrain = Open();
        var (map, _, chain) = Chain(terrain);
        var cut = new Position(20 + 80 + 40, 100); // between the first and the second point of the chain

        terrain.SetPixel(cut, TerrainPixel.Dirt);
        map.Advance(terrain, Refresh);
        Assert.InRange(map.SightlinesCheckedInLastAdvance, 1, 6);
        Assert.True(chain[0].IsPowered);
        Assert.All(chain.Skip(1), p => Assert.False(p.IsPowered));
        Assert.All(chain.Skip(1), p => Assert.Equal(-1, p.Hops));

        // However the cell is written, the map hears of it.
        terrain.SetPixelRaw(cut, TerrainPixel.Blank);
        map.Advance(terrain, Refresh);
        Assert.All(chain, p => Assert.True(p.IsPowered));
        Assert.Equal([1, 2, 3, 4], chain.Select(p => p.Hops));
    }

    [Fact]
    public void BetweenRefreshes_TheMapStaysAsItWas_ExceptWhenAMachinePointIsSwitchedOff()
    {
        var terrain = Open();
        var (map, _, chain) = Chain(terrain);
        var step = TimeSpan.FromTicks(Refresh.Ticks / 5);
        map.Advance(terrain, step); // a refresh is due on the first step; the next is four steps later

        terrain.SetPixel(new Position(20 + 40, 100), TerrainPixel.Dirt);
        map.Advance(terrain, step);
        Assert.True(chain[0].IsPowered, "a blocked line is noticed at the next refresh, not before");

        chain[2].IsEnabled = false;
        map.Advance(terrain, step);
        Assert.DoesNotContain(chain[2], map.Points);
        Assert.DoesNotContain(map.Links, l => l.From == chain[2] || l.To == chain[2]);
        // That refresh also saw the blocked line, so nothing still in the map has power.
        Assert.All(map.Points.Where(p => p.Type == LinkPointType.Machine), p => Assert.False(p.IsPowered));
    }

    [Fact]
    public void APointRegisteredLater_IsLinkedAtTheNextRefresh_InTheOrderOfARebuild()
    {
        var terrain = Open();
        var (map, _, chain) = Chain(terrain, count: 2);
        var late = map.RegisterPoint(new Position(20 + 80 * 3, 100), LinkPointType.Machine);
        Assert.False(late.IsPowered);

        map.Advance(terrain, Refresh);

        Assert.True(late.IsPowered);
        Assert.Equal(3, late.Hops);
        Assert.Same(chain[1], late.UpstreamLink!.From);
        AssertSameAsBuiltFromNothing(map, terrain);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AMapKeptUpToDate_IsTheMapARebuildFromNothingGives(int seed)
    {
        // Points come and go and the ground between them is dug and filled; after every refresh the map must be the
        // one a new map makes of the same points and ground.
        var rng = new Random(seed);
        var terrain = new TerrainGrid(new Size(500, 300));
        terrain.Fill(TerrainPixel.Dirt);
        void Line(Position a, Position b, TerrainPixel pixel)
        {
            int steps = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
            for (int i = 0; i <= steps; i++)
            {
                var p = new Position(a.X + (b.X - a.X) * i / Math.Max(1, steps), a.Y + (b.Y - a.Y) * i / Math.Max(1, steps));
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        terrain.SetPixel(p + new Offset(dx, dy), pixel);
            }
        }

        var map = new LinkMap();
        var live = new List<LinkPoint>
        {
            map.RegisterPoint(new Position(60, 150), LinkPointType.Base),
            map.RegisterPoint(new Position(440, 150), LinkPointType.Base),
        };
        Position Somewhere() => new(rng.Next(20, 480), rng.Next(20, 280));

        for (int round = 0; round < 120; round++)
        {
            switch (rng.Next(5))
            {
                case 0 or 1:
                    // A new point, with a tunnel dug to a point that is already there.
                    var at = live[rng.Next(live.Count)].Position + new Offset(rng.Next(-70, 71), rng.Next(-70, 71));
                    at = new Position(Math.Clamp(at.X, 5, 494), Math.Clamp(at.Y, 5, 294));
                    Line(at, live[rng.Next(live.Count)].Position, TerrainPixel.Blank);
                    live.Add(map.RegisterPoint(at, LinkPointType.Machine));
                    break;
                case 2:
                    Line(Somewhere(), Somewhere(), TerrainPixel.Dirt);
                    break;
                case 3:
                    Line(live[rng.Next(live.Count)].Position, live[rng.Next(live.Count)].Position, TerrainPixel.Blank);
                    break;
                default:
                    if (live.Count > 2)
                    {
                        int doomed = rng.Next(2, live.Count);
                        live[doomed].IsEnabled = false;
                        live.RemoveAt(doomed);
                    }
                    break;
            }
            // Sometimes several things happen between two refreshes.
            if (rng.Next(3) == 0)
                continue;
            map.Advance(terrain, Refresh);
            Assert.Equal(live, map.Points);
            AssertSameAsBuiltFromNothing(map, terrain);
        }
    }

    private static void AssertSameAsBuiltFromNothing(LinkMap map, TerrainGrid terrain)
    {
        var fresh = new LinkMap();
        foreach (var point in map.Points)
            fresh.RegisterPoint(point.Position, point.Type);
        fresh.Advance(terrain, Refresh);

        static (Position, Position, LinkType)? Ends(Link? link) =>
            link == null ? null : (link.From.Position, link.To.Position, link.Type);
        Assert.Equal(fresh.Links.Select(Ends), map.Links.Select(Ends));
        Assert.Equal(
            fresh.Points.Select(p => (p.Position, p.IsPowered, p.Hops, Ends(p.UpstreamLink))),
            map.Points.Select(p => (p.Position, p.IsPowered, p.Hops, Ends(p.UpstreamLink))));
    }
}
