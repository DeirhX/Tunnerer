using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

/// <summary>
/// The crack-the-core loop (DESIGN.md §6.3, §6.4): a lump's warm core keeps the ground around it too hot for
/// harvesters, only the tank's torch takes the core, and once it is out the lump cools and harvesters clear the rest.
/// </summary>
public class LumpHarvestTests
{
    private const int Radius = 6;

    /// <summary>
    /// A round lump 11 cells to the side of the scenario's relay position: about 110 plain cells round a core of 9,
    /// warmed for <paramref name="seconds"/> (the shell is near its steady temperature after a minute).
    /// </summary>
    private static (RelayHeatTests.Scenario S, Position Center, List<Position> Cells) WarmLump(int seconds = 60)
    {
        var s = RelayHeatTests.CreateScenario();
        var center = s.RelayPos + new Offset(11, 0);
        var cells = new List<Position>();
        for (int dy = -Radius; dy <= Radius; dy++)
            for (int dx = -Radius; dx <= Radius; dx++)
            {
                int d2 = dx * dx + dy * dy;
                var p = center + new Offset(dx, dy);
                if (d2 <= Radius * Radius + 3 && s.World.Terrain.PlaceOre(p, core: d2 <= 3, dirtOnly: false))
                    cells.Add(p);
            }
        Advance(s, seconds * Tweaks.Perf.TargetFps);
        return (s, center, cells);
    }

    private static void Advance(RelayHeatTests.Scenario s, int ticks)
    {
        for (int i = 0; i < ticks; i++)
            s.World.Advance(_ => default);
    }

    private static void CutTheCore(RelayHeatTests.Scenario s, List<Position> cells)
    {
        foreach (var c in cells)
            if (Pixel.IsOreCore(s.World.Terrain.GetPixelRaw(c)))
                s.World.Terrain.SetPixel(c, TerrainPixel.Blank);
    }

    /// <summary>What a harvester senses standing <paramref name="away"/> cells from the lump's centre, toward the tunnel.</summary>
    private static float Sense(RelayHeatTests.Scenario s, Position center, int away) =>
        MachineBehaviors.HarvesterSenseTemperature(s.World.Terrain, center + new Offset(-away, 0));

    /// <summary>The nearest a harvester (5x5) can stand to the lump's outer ore while that ore is there.</summary>
    private const int OuterEdge = Radius + 3;

    [Trait("Category", "Slow")]
    [Fact]
    public void AnIntactCore_KeepsTheLumpTooHotToHarvest_UntilTheTankCutsItOut()
    {
        var (s, center, cells) = WarmLump();
        float stall = Tweaks.Machine.HarvesterStallTemperature;
        Assert.True(Sense(s, center, OuterEdge) >= stall,
            $"a harvester at the lump's outer edge should stall. sensed={Sense(s, center, OuterEdge):0.0}");
        // Hot enough that the radar reads the shell as heat, not ore.
        Assert.All(cells, c => Assert.True(s.World.Terrain.GetHeatTemperature(c) >= Tweaks.Radar.HeatScrambleTemperature));

        CutTheCore(s, cells);
        Advance(s, 20 * Tweaks.Perf.TargetFps);
        Assert.True(Sense(s, center, OuterEdge) < stall,
            $"twenty seconds after the core is out, the edge should be workable. sensed={Sense(s, center, OuterEdge):0.0}");
    }

    /// <summary>
    /// In a class of its own so that xUnit runs it beside the other lump tests: each warms its own lump for a minute
    /// of game time first.
    /// </summary>
    public class CoolingTests
    {
        [Trait("Category", "Slow")]
        [Fact]
        public void ARelay_CoolsACutLump_WithinSeconds()
        {
            var (s, center, cells) = WarmLump();
            CutTheCore(s, cells);
            RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(-1, 0));

            Advance(s, 10 * Tweaks.Perf.TargetFps);

            for (int away = 0; away <= OuterEdge; away++)
                Assert.True(Sense(s, center, away) < Tweaks.Machine.HarvesterStallTemperature,
                    $"{away} cells from the centre sensed {Sense(s, center, away):0.0}");
        }
    }

    /// <summary>
    /// In a class of its own so that xUnit runs it beside the other lump tests: each warms its own lump for a minute
    /// of game time first.
    /// </summary>
    public class HarvestingTests
    {
        [Trait("Category", "Slow")]
        [Fact]
        public void AHarvester_ClearsTheShell_ButNeverTheCore()
        {
            var (s, _, cells) = WarmLump();
            // The tank's way in: an open bay from the tunnel to the lump, with a linked relay and a pylon to recharge at.
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -2; dx <= 4; dx++)
                    s.World.Terrain.SetPixel(s.RelayPos + new Offset(dx, dy), TerrainPixel.Blank);
            RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(-1, 0));
            var pylon = new Machine(s.RelayPos + new Offset(0, s.DirY * -8), MachineType.Pylon, s.Tank.Color);
            s.World.Machines.Add(pylon);
            s.World.PlantMachine(pylon, s.Base);
            var harvester = new Machine(s.RelayPos + new Offset(3, 0), MachineType.Harvester, s.Tank.Color);
            s.World.Machines.Add(harvester);
            s.World.PlantMachine(harvester, s.Base);
            int cores = cells.Count(c => Pixel.IsOreCore(s.World.Terrain.GetPixelRaw(c)));
            int plain = cells.Count - cores;

            Advance(s, 60 * Tweaks.Perf.TargetFps);

            int plainLeft = cells.Count(c => s.World.Terrain.GetPixelRaw(c) == TerrainPixel.Emberite);
            Assert.Equal(cores, cells.Count(c => Pixel.IsOreCore(s.World.Terrain.GetPixelRaw(c))));
            Assert.True(plain - plainLeft >= 40, $"a minute's work should clear a good part of the shell. mined={plain - plainLeft}");
        }
    }
}
