namespace Tunnerer.Tests;

using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Links;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

/// <summary>Links need direct visibility through open ground, and show the heat relays send home along them.</summary>
public class LinkVisibilityTests
{
    private static (TerrainGrid Terrain, LinkMap Map, LinkPoint Base, LinkPoint A, LinkPoint B) Build(bool dirtBetween)
    {
        var terrain = new TerrainGrid(new Size(64, 16));
        terrain.Fill(TerrainPixel.Blank);
        if (dirtBetween)
            terrain.SetPixel(new Position(20, 8), TerrainPixel.Dirt);

        var map = new LinkMap(deterministicSimulation: true);
        var home = map.RegisterPoint(new Position(4, 8), LinkPointType.Base);
        var a = map.RegisterPoint(new Position(30, 8), LinkPointType.Machine);
        var b = map.RegisterPoint(new Position(50, 8), LinkPointType.Machine);
        map.Advance(terrain, Tweaks.World.RefreshLinkMapInterval);
        return (terrain, map, home, a, b);
    }

    [Fact]
    public void Dirt_BlocksTheLineOfSight()
    {
        var open = Build(dirtBetween: false);
        var blocked = Build(dirtBetween: true);

        Assert.True(open.A.IsPowered);
        Assert.False(blocked.A.IsPowered);
        Assert.Contains(blocked.Map.Links, l => l.Type == LinkType.Blocked);
    }

    [Fact]
    public void PumpedHeat_FlowsAlongTheRouteToTheBase()
    {
        var (_, map, home, a, b) = Build(dirtBetween: false);
        Assert.True(b.IsPowered);

        map.ClearHeatFlow();
        LinkMap.CarryHeat(b, 300.0);

        // Every link on b's route home carries the heat, pointing toward the base.
        var point = b;
        int hops = 0;
        while (point.UpstreamLink is { } link)
        {
            Assert.Equal(300.0, link.HeatFlow);
            var downstream = link.FlowsFromTo ? link.From : link.To;
            Assert.Same(point, downstream);
            point = link.FlowsFromTo ? link.To : link.From;
            hops++;
        }
        Assert.Same(home, point);
        Assert.True(hops >= 1);

        map.ClearHeatFlow();
        Assert.All(map.Links, l => Assert.Equal(0.0, l.HeatFlow));
    }
}
