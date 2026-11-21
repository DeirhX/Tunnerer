using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

namespace Emberward.Tests;

/// <summary>
/// A harvester does not freeze where it stands: hot ore (the shell of a lump whose core is in) is passed over for ore
/// it can work, and being off the network or on hot ground stops its work but not its crawl toward ore (DESIGN.md §6.4).
/// </summary>
public class HarvesterHotLumpTests
{
    private const int LumpRadius = 6;

    private static int ActionTicks(int actions) =>
        (int)Math.Ceiling(actions * Tweaks.Machine.HarvesterIntervalMs / 1000.0 * Tweaks.Perf.TargetFps) + 2;

    private static void Advance(RelayHeatTests.Scenario s, int ticks)
    {
        for (int i = 0; i < ticks; i++)
            s.World.Advance(_ => default);
    }

    private static Machine PlantHarvester(RelayHeatTests.Scenario s, Position at)
    {
        var harvester = new Machine(at, MachineType.Harvester, s.Tank.Color);
        s.World.Machines.Add(harvester);
        s.World.PlantMachine(harvester, s.Base);
        return harvester;
    }

    /// <summary>An open bay beside the scenario's relay position, <paramref name="reach"/> cells either way across the tunnel.</summary>
    private static void OpenBay(RelayHeatTests.Scenario s, int fromX, int toX, int half)
    {
        for (int dy = -half; dy <= half; dy++)
            for (int dx = fromX; dx <= toX; dx++)
                s.World.Terrain.SetPixel(s.RelayPos + new Offset(dx, dy), TerrainPixel.Blank);
    }

    [Trait("Category", "Slow")]
    [Fact]
    public void AHarvester_PassesOverALumpWhoseCoreIsIn_ForOreItCanWork()
    {
        var s = RelayHeatTests.CreateScenario();
        OpenBay(s, -18, 4, 12);
        // A round lump with its core in, 11 cells along the bay, warmed until its shell is hot.
        var center = s.RelayPos + new Offset(11, 0);
        var lump = new List<Position>();
        for (int dy = -LumpRadius; dy <= LumpRadius; dy++)
            for (int dx = -LumpRadius; dx <= LumpRadius; dx++)
            {
                int d2 = dx * dx + dy * dy;
                var p = center + new Offset(dx, dy);
                if (d2 <= LumpRadius * LumpRadius + 3 && s.World.Terrain.PlaceOre(p, core: d2 <= 3, dirtOnly: false))
                    lump.Add(p);
            }
        Advance(s, 12 * Tweaks.Perf.TargetFps);
        Assert.Contains(lump, p => s.World.Terrain.GetPixelRaw(p) == TerrainPixel.Pyrite &&
            s.World.Terrain.GetHeatTemperature(p) >= Tweaks.Machine.HarvesterHotOreTemperature);

        // A pocket the other way, farther from the harvester than the lump is, and out of reach of the heat a core
        // that hot sends into the bay.
        var pocket = new List<Position>();
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var p = s.RelayPos + new Offset(-14 + dx, dy);
                s.World.Terrain.SetPixel(p, TerrainPixel.Pyrite);
                pocket.Add(p);
            }
        // Up the tunnel, out of the lump's way: it keeps the harvester on the network without cooling the shell.
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * -12));
        var harvester = PlantHarvester(s, s.RelayPos + new Offset(2, 0));
        int plainBefore = lump.Count(p => s.World.Terrain.GetPixelRaw(p) == TerrainPixel.Pyrite);
        int storeBefore = s.Base.Store.Ore;

        // The harvester starts three cells from a hot lump and first crawls away from it; the bay's air is warm from the
        // core, so it works slowly. Run until the pocket is cleared (or a minute has passed).
        for (int tick = 0; tick < 60 * Tweaks.Perf.TargetFps &&
            pocket.Any(p => s.World.Terrain.GetPixelRaw(p) != TerrainPixel.Blank); tick++)
            Advance(s, 1);

        Assert.All(pocket, p => Assert.Equal(TerrainPixel.Blank, s.World.Terrain.GetPixelRaw(p)));
        Assert.Equal(storeBefore + pocket.Count, s.Base.Store.Ore);
        Assert.False(harvester.IsStalled, "it went for the pocket and left the hot ground");
        // It left the hot shell for the tank: nothing of the lump was worked.
        Assert.Equal(plainBefore, lump.Count(p => s.World.Terrain.GetPixelRaw(p) == TerrainPixel.Pyrite));
        Assert.Equal(CoreCells(s, lump), lump.Count(p => Pixel.IsOreCore(s.World.Terrain.GetPixelRaw(p))));
    }

    private static int CoreCells(RelayHeatTests.Scenario s, List<Position> lump) =>
        lump.Count(p => Pixel.IsOreCore(s.World.Terrain.GetPixelRaw(p)));

    [Fact]
    public void AHarvester_OffTheNetwork_StillCrawlsToOre_ButWorksNone()
    {
        var s = RelayHeatTests.CreateScenario();
        OpenBay(s, -14, 14, 6);
        var harvester = PlantHarvester(s, s.RelayPos);
        var ore = s.RelayPos + new Offset(11, 0);
        s.World.Terrain.SetPixel(ore, TerrainPixel.Pyrite);
        int storeBefore = s.Base.Store.Ore;

        Advance(s, ActionTicks(30));

        Assert.True(harvester.IsOffNetwork, "no relay reaches it here");
        Assert.True(Position.DistanceSquared(harvester.Position, ore) <=
            Tweaks.Ore.HarvesterReach * Tweaks.Ore.HarvesterReach, $"it should have crawled to the ore. at {harvester.Position}");
        Assert.Equal(TerrainPixel.Pyrite, s.World.Terrain.GetPixelRaw(ore));
        Assert.Equal(storeBefore, s.Base.Store.Ore);
        Assert.Equal(HarvesterState.OffNetwork, MachineBehaviors.HarvesterStateOf(harvester));
    }

    [Fact]
    public void AHarvester_Crawls_AtLeastSixCellsASecond()
    {
        var s = RelayHeatTests.CreateScenario();
        OpenBay(s, -3, 20, 6);
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(-1, 0));
        var harvester = PlantHarvester(s, s.RelayPos + new Offset(2, 0));
        s.World.Terrain.SetPixel(s.RelayPos + new Offset(19, 0), TerrainPixel.Pyrite);

        Advance(s, Tweaks.Perf.TargetFps * 2);

        Assert.True(harvester.Position.X - s.RelayPos.X >= 2 + 12, $"two seconds of crawling reached {harvester.Position}");
    }
}
