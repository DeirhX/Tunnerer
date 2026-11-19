using Emberward.Core.Match;
using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.LevelGen;
using Emberward.Core.Terrain;
using Emberward.Core.Thermal;
using Emberward.Core.Types;

namespace Emberward.Tests;

/// <summary>Emberite ore as terrain: the pixels, the warm core, and how deposits are seeded (DESIGN.md §6.3).</summary>
public class OreDepositTests
{
    private static readonly float CoreStart = Tweaks.Ore.CoreStartTemperature;
    private static readonly Size MediumMap = new(800, 400);

    [Theory]
    [InlineData(TerrainPixel.Emberite, false, ThermalMaterial.Stone)]
    [InlineData(TerrainPixel.EmberiteCore, true, ThermalMaterial.EmberiteCore)]
    public void Ore_BlocksMovement_AndIsNoOtherKindOfGround(TerrainPixel pixel, bool core, ThermalMaterial material)
    {
        Assert.True(Pixel.IsOre(pixel));
        Assert.Equal(core, Pixel.IsOreCore(pixel));
        Assert.True(Pixel.IsBlockingCollision(pixel));
        Assert.False(Pixel.IsDiggable(pixel), "the dig has its own rule for ore");
        Assert.False(Pixel.IsTorchable(pixel));
        Assert.False(Pixel.IsRock(pixel), "shots break rock; they must not break ore");
        Assert.False(Pixel.IsMineral(pixel));
        Assert.False(Pixel.IsDirt(pixel), "ore never regrows or counts as dirt");
        Assert.Equal(material, Pixel.GetThermalMaterial(pixel));
        Assert.Equal(core ? Tweaks.Ore.CoreCellUnits : Tweaks.Ore.PlainCellUnits, Pixel.OreUnits(pixel));
        Assert.False(Pixel.IsOre(TerrainPixel.Rock));
        Assert.Equal(0, Pixel.OreUnits(TerrainPixel.Concrete));
    }

    [Fact]
    public void Ore_IsFinite_SeededWithHeat_AndNothingIsPinned()
    {
        var engine = new TerrainHeatEngine();
        Assert.False(engine.IsFixedTemperatureMaterial(Pixel.GetThermalMaterial(TerrainPixel.EmberiteCore)));
        Assert.False(engine.IsFixedTemperatureMaterial(Pixel.GetThermalMaterial(TerrainPixel.Emberite)));
        Assert.True(CoreStart > Tweaks.Tank.HeatSafeMax * 2f, "a core starts far above what a tank can bear");
        Assert.True(Tweaks.Ore.PlainStartTemperature > Tweaks.World.ThermalAmbientTemperature);
        Assert.True(Tweaks.Ore.PlainStartTemperature <= Tweaks.Radar.HeatThreshold, "the radar must still read fresh plain ore");
    }

    [Fact]
    public void SeededOreHeat_IsBookedInTheLedger_AsInjectedEnergy()
    {
        var t = new TerrainGrid(new Size(30, 30));
        t.Fill(TerrainPixel.Rock);
        double before = t.SumTotalThermalEnergy();
        Assert.True(t.PlaceOre(new Position(10, 10), core: true, dirtOnly: false));
        Assert.True(t.PlaceOre(new Position(12, 10), core: false, dirtOnly: false));

        double expected = Tweaks.Ore.CoreHeatCapacity * CoreStart
            + Tweaks.World.ThermalMaterials[ThermalMaterial.Stone].HeatCapacity * Tweaks.Ore.PlainStartTemperature;
        Assert.Equal(expected, t.SumTotalThermalEnergy() - before, tolerance: 0.5);
        Assert.Equal(expected, t.Ledger.NetExternal, tolerance: 0.5);
    }

    [Fact]
    public void PlaceOre_SpecksTakeOnlyDirt_DepositsAlsoTakeRock()
    {
        var t = new TerrainGrid(new Size(20, 20));
        t.SetPixelRaw(new Position(5, 5), TerrainPixel.Dirt);
        t.SetPixelRaw(new Position(6, 5), TerrainPixel.Rock);
        t.SetPixelRaw(new Position(7, 5), TerrainPixel.Blank);
        t.SetPixelRaw(new Position(8, 5), TerrainPixel.HotRock);

        Assert.False(t.PlaceOre(new Position(6, 5), core: false, dirtOnly: true));
        Assert.True(t.PlaceOre(new Position(5, 5), core: false, dirtOnly: true));
        Assert.True(t.PlaceOre(new Position(6, 5), core: true, dirtOnly: false));
        Assert.False(t.PlaceOre(new Position(7, 5), core: false, dirtOnly: false));
        Assert.False(t.PlaceOre(new Position(8, 5), core: false, dirtOnly: false));
        Assert.Equal(TerrainPixel.Emberite, t.GetPixelRaw(new Position(5, 5)));
        Assert.Equal(TerrainPixel.EmberiteCore, t.GetPixelRaw(new Position(6, 5)));
        Assert.Equal(CoreStart, t.GetHeatTemperature(new Position(6, 5)));
    }

    /// <summary>A rock block with a tunnel row, and a 3x3 core lump bordering it.</summary>
    private static TerrainGrid CoreNextToTunnel(out Position core, out int tunnelY)
    {
        var t = new TerrainGrid(new Size(40, 30));
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 40; x++)
                t.SetPixelRaw(new Position(x, y), TerrainPixel.Rock);
        tunnelY = 16;
        for (int x = 1; x < 39; x++)
            t.SetPixelRaw(new Position(x, tunnelY), TerrainPixel.Blank);
        core = new Position(12, 14);
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                Assert.True(t.PlaceOre(core + new Offset(dx, dy), core: true, dirtOnly: false));
        return t;
    }

    [Fact]
    public void Core_HeatsTheTunnel_AndStopsWhenMinedOut_WithTheLedgerBalanced()
    {
        var t = CoreNextToTunnel(out var core, out int tunnelY);
        double startEnergy = t.SumTotalThermalEnergy();
        double startNet = t.Ledger.NetExternal;
        double maxResidual = 0.0;
        void Step(int n)
        {
            for (int i = 0; i < n; i++)
            {
                t.CoolDown();
                double residual = (t.SumTotalThermalEnergy() - startEnergy) - (t.Ledger.NetExternal - startNet);
                maxResidual = Math.Max(maxResidual, Math.Abs(residual));
            }
        }

        Step(300);
        Assert.True(t.GetHeatTemperature(core) < CoreStart, "a core is finite: it has already given some heat away");
        Assert.True(t.GetHeatTemperature(core) > Tweaks.Ore.PlainStartTemperature, $"but its heat is still there after 12 s. {t.GetHeatTemperature(core):0.0}");
        var below = new Position(core.X, tunnelY);
        float warm = t.GetAirTemperature(below);
        Assert.True(warm > 10f, $"the tunnel beside the core should be warm. air={warm:0.0}");

        // Mine the core out (as the torch does): the heat in them goes with them and the tunnel cools.
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                t.SetPixel(core + new Offset(dx, dy), TerrainPixel.Blank);
        Step(600);

        Assert.True(t.GetAirTemperature(core) < warm, "a mined-out core is no longer a source");
        Assert.True(t.GetAirTemperature(below) < warm, "the tunnel cools once the source is gone");
        Assert.True(maxResidual < 0.5, $"the core's heat must be fully accounted for. residual={maxResidual:0.0000}");
    }

    private static TerrainGrid RockWithTunnelAndOre(int tunnelY, Action<TerrainGrid> placeOre)
    {
        var t = new TerrainGrid(new Size(60, 40));
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 60; x++)
                t.SetPixelRaw(new Position(x, y), TerrainPixel.Rock);
        for (int x = 1; x < 59; x++)
            t.SetPixelRaw(new Position(x, tunnelY), TerrainPixel.Blank);
        placeOre(t);
        return t;
    }

    /// <summary>What a run of a tunnel beside ore found: the worst energy-balance residual and the tunnel air's peaks.</summary>
    private readonly record struct TunnelRun(double Residual, float PeakAir, int PeakHotCells, int TicksAboveSafe, float FinalAir);

    private static TunnelRun Run(TerrainGrid t, int ticks, int tunnelY)
    {
        double startEnergy = t.SumTotalThermalEnergy();
        double startNet = t.Ledger.NetExternal;
        double residual = 0.0;
        float peak = float.MinValue;
        int peakCells = 0, above = 0;
        float air = 0f;
        for (int i = 0; i < ticks; i++)
        {
            t.CoolDown();
            residual = Math.Max(residual, Math.Abs(t.SumTotalThermalEnergy() - startEnergy - (t.Ledger.NetExternal - startNet)));
            air = float.MinValue;
            int cells = 0;
            for (int x = 1; x < 59; x++)
            {
                float a = t.GetAirTemperature(new Position(x, tunnelY));
                air = Math.Max(air, a);
                if (a > Tweaks.Tank.HeatSafeMax)
                    cells++;
            }
            peak = Math.Max(peak, air);
            peakCells = Math.Max(peakCells, cells);
            if (air > Tweaks.Tank.HeatSafeMax)
                above++;
        }
        return new TunnelRun(residual, peak, peakCells, above, air);
    }

    /// <summary>A square block of core cells with its lower edge just above the tunnel row.</summary>
    private static void PlaceCoreBlock(TerrainGrid t, int side, int tunnelY)
    {
        for (int dy = 0; dy < side; dy++)
            for (int dx = 0; dx < side; dx++)
                Assert.True(t.PlaceOre(new Position(20 + dx, tunnelY - 1 - dy), core: true, dirtOnly: false));
    }

    [Fact]
    public void Speck_WarmsItsGround_AFewDegrees_ThenCoolsAway_WithTheLedgerBalanced()
    {
        var t = new TerrainGrid(new Size(30, 30));
        t.Fill(TerrainPixel.Dirt);
        var speck = new Position(15, 15);
        Assert.True(t.PlaceOre(speck, core: false, dirtOnly: true));
        Assert.Equal(Tweaks.Ore.PlainStartTemperature, t.GetHeatTemperature(speck));

        double startEnergy = t.SumTotalThermalEnergy(), startNet = t.Ledger.NetExternal, residual = 0.0;
        float early = 0f, beside = 0f;
        for (int i = 0; i < 1500; i++)
        {
            t.CoolDown();
            residual = Math.Max(residual, Math.Abs(t.SumTotalThermalEnergy() - startEnergy - (t.Ledger.NetExternal - startNet)));
            if (i == 20)
            {
                early = t.GetHeatTemperature(speck);
                beside = t.GetHeatTemperature(speck + new Offset(1, 0));
            }
        }

        Assert.True(beside > 0.5f, "a speck warms the ground beside it");
        Assert.True(early < Tweaks.Ore.PlainStartTemperature);
        Assert.True(t.GetHeatTemperature(speck) < early, "a speck is finite: it keeps cooling");
        Assert.True(t.GetHeatTemperature(speck) < Tweaks.Ore.PlainStartTemperature / 2f);
        Assert.True(residual < 0.5, $"a speck's heat must be fully accounted for. residual={residual:0.0000}");
    }

    [Fact]
    public void ACoreInAShell_PassesItsHeatThroughTheShell_AndCoolsWithIt()
    {
        var t = RockWithTunnelAndOre(tunnelY: 20, tt =>
        {
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                    Assert.True(tt.PlaceOre(new Position(20 + dx, 16 + dy), core: Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1,
                        dirtOnly: false));
        });
        var run = Run(t, 600, 20);

        Assert.True(t.GetHeatTemperature(new Position(20, 19)) > Tweaks.Ore.PlainStartTemperature,
            $"the shell between the core and the tunnel carries its heat. {t.GetHeatTemperature(new Position(20, 19)):0.0}");
        Assert.True(run.Residual < 0.5, $"residual={run.Residual:0.0000}");
    }

    [Fact]
    public void LargeLumpCore_IsDangerousToATankForLonger_ThanASmallOne_AndASpeckIsNot()
    {
        const int tunnelY = 30;
        var speck = RockWithTunnelAndOre(tunnelY, t => Assert.True(t.PlaceOre(new Position(20, tunnelY - 1), core: false, dirtOnly: false)));
        int smallSide = (int)Math.Ceiling(Math.Sqrt(OreDeposits.CoreCellsFor(Tweaks.Ore.LumpMinCells)));
        int largeSide = (int)Math.Ceiling(Math.Sqrt(OreDeposits.CoreCellsFor(Tweaks.Ore.LumpMaxCells)));
        var smallLump = RockWithTunnelAndOre(tunnelY, t => PlaceCoreBlock(t, smallSide, tunnelY));
        var largeLump = RockWithTunnelAndOre(tunnelY, t => PlaceCoreBlock(t, largeSide, tunnelY));
        int ticks = 600 * Tweaks.Perf.TargetFps / 10;

        var speckRun = Run(speck, ticks, tunnelY);
        var smallRun = Run(smallLump, ticks, tunnelY);
        var largeRun = Run(largeLump, ticks, tunnelY);

        Assert.True(speckRun.PeakAir < Tweaks.Tank.HeatSafeMax * 0.1f, $"a speck is merely warm. air={speckRun.PeakAir:0.0}");
        Assert.True(largeRun.PeakAir > Tweaks.Tank.HeatMax, $"a large lump's core is dangerous. air={largeRun.PeakAir:0.0}");
        Assert.True(largeRun.TicksAboveSafe > smallRun.TicksAboveSafe,
            $"a larger core holds more energy and stays dangerous longer. small={smallRun.TicksAboveSafe} large={largeRun.TicksAboveSafe}");
        Assert.True(largeRun.FinalAir < largeRun.PeakAir, "the core is finite: the tunnel cools again");
        Assert.True(Math.Max(speckRun.Residual, Math.Max(smallRun.Residual, largeRun.Residual)) < 1.0,
            $"residual={largeRun.Residual:0.0000}");
    }

    private static World SeededWorld(int seed, Size? size = null)
    {
        var mapSize = size ?? MediumMap;
        var (terrain, spawns) = new ToastGenerator().Generate(mapSize, seed: seed);
        var world = new World(mapSize, simulationSeed: seed, new MatchRules { TerrainRegrowth = false, SeedOre = true });
        world.Initialize(terrain, spawns, decorationSeed: seed + 1);
        return world;
    }

    private static int CountOreUnits(TerrainGrid terrain)
    {
        int units = 0;
        for (int i = 0; i < terrain.Size.Area; i++)
            units += Pixel.OreUnits(terrain[i]);
        return units;
    }

    [Fact]
    public void Seeding_IsDeterministic_AndFollowsTheSeed()
    {
        var small = new Size(500, 300);
        var a = SeededWorld(8181, small);
        var b = SeededWorld(8181, small);
        var other = SeededWorld(9292, small);

        Assert.NotEmpty(a.OreDeposits);
        Assert.Equal(a.OreDeposits, b.OreDeposits);
        Assert.NotEqual(a.OreDeposits, other.OreDeposits);
        for (int i = 0; i < a.Terrain.Size.Area; i++)
            Assert.Equal(a.Terrain[i], b.Terrain[i]);
        Assert.Empty(TestHelpers.CreateSeededWorld(seed: 8181).OreDeposits);
    }

    [Fact]
    public void Seeding_PlacesEveryKind_WithinItsSizeAndDistanceRules()
    {
        var world = SeededWorld(8181);
        var bases = world.TankBases.Bases.Select(b => b.Position).ToArray();
        var byKind = world.OreDeposits.ToLookup(d => d.Kind);
        // The distance rules shrink with the map: 800x400 is about half the reference map's width.
        double scale = OreDeposits.DistanceScale(MediumMap.Area);
        Assert.InRange(scale, 0.5, 0.55);
        int Scaled(int distance) => (int)(distance * scale);

        // 800x400: three lumps, a dozen pockets, about fifty specks.
        Assert.InRange(byKind[OreDepositKind.Lump].Count(), 2, 3);
        Assert.InRange(byKind[OreDepositKind.Pocket].Count(), 10, 12);
        Assert.InRange(byKind[OreDepositKind.Speck].Count(), 45, 53);

        foreach (var d in world.OreDeposits)
        {
            var (min, max, fromBase) = d.Kind switch
            {
                OreDepositKind.Speck => (Tweaks.Ore.SpeckMinCells, Tweaks.Ore.SpeckMaxCells, Tweaks.Ore.SpeckMinDistanceFromBase),
                OreDepositKind.Pocket => (1, Tweaks.Ore.PocketMaxCells, Tweaks.Ore.PocketMinDistanceFromBase),
                _ => (Tweaks.Ore.LumpMinCoreCells, Tweaks.Ore.LumpMaxCells, Tweaks.Ore.LumpMinDistanceFromBase),
            };
            Assert.InRange(d.Cells, min, max);
            if (d.Kind == OreDepositKind.Lump)
                Assert.InRange(d.CoreCells, Math.Min(Tweaks.Ore.LumpMinCoreCells, d.Cells),
                    OreDeposits.CoreCellsFor(Tweaks.Ore.LumpMaxCells));
            else
                Assert.Equal(0, d.CoreCells);
            int minFromBase = Scaled(fromBase);
            Assert.All(bases, b => Assert.True(Position.DistanceSquared(d.Center, b) >= minFromBase * minFromBase,
                $"{d.Kind} at {d.Center} too close to the base at {b}"));
            Assert.True(Pixel.IsOre(world.Terrain.GetPixelRaw(d.Center)), "a deposit grows from its start cell");
        }

        // Most pockets reach their size; a lump's core sits at its start, pinned warm.
        Assert.True(byKind[OreDepositKind.Pocket].Average(d => d.Cells) >= Tweaks.Ore.PocketMinCells);
        foreach (var lump in byKind[OreDepositKind.Lump])
        {
            Assert.True(lump.Cells >= Tweaks.Ore.LumpMinCells / 2, $"lump at {lump.Center} has only {lump.Cells} cells");
            Assert.Equal(TerrainPixel.EmberiteCore, world.Terrain.GetPixelRaw(lump.Center));
            // The level's prewarm (World) has already let the core's heat spread through the shell.
            Assert.True(world.Terrain.GetHeatTemperature(lump.Center) > Tweaks.Ore.PlainStartTemperature * 2f, $"a lump is still hot at the start of play. {world.Terrain.GetHeatTemperature(lump.Center):0.0}");
        }
        var lumps = byKind[OreDepositKind.Lump].ToArray();
        for (int i = 0; i < lumps.Length; i++)
            for (int j = i + 1; j < lumps.Length; j++)
                Assert.True(Position.DistanceSquared(lumps[i].Center, lumps[j].Center) >=
                    Scaled(Tweaks.Ore.LumpMinDistanceBetween) * Scaled(Tweaks.Ore.LumpMinDistanceBetween));

        // What the deposits report is what lies in the ground.
        Assert.Equal(world.OreDeposits.Sum(d => d.Units), CountOreUnits(world.Terrain));
    }

    [Theory]
    [InlineData(8181)]
    [InlineData(4242)]
    [InlineData(77)]
    public void SmallMap_StillGetsAFewPockets_AndALump(int seed)
    {
        // The desktop's default match (DesktopScreenTweaks.RenderSurfaceSize).
        var size = new Size(320, 200);
        var world = SeededWorld(seed, size);
        var byKind = world.OreDeposits.ToLookup(d => d.Kind);

        Assert.Equal(Tweaks.Ore.MinDistanceScale, OreDeposits.DistanceScale(size.Area));
        Assert.True(byKind[OreDepositKind.Pocket].Count() >= Tweaks.Ore.MinPockets,
            $"pockets={byKind[OreDepositKind.Pocket].Count()}");
        Assert.True(byKind[OreDepositKind.Lump].Any(), "a lump fits on a 320x200 map");
        Assert.True(world.OreDeposits.Sum(d => d.Units) >= 150, $"units={world.OreDeposits.Sum(d => d.Units)}");
        Assert.True(OreDeposits.DistanceScale(1500L * 750) == 1.0, "the reference map keeps its rules");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void DefaultMap_HoldsAboutTwoAndAHalfThousandUnits_SplitBetweenTheKinds()
    {
        var world = SeededWorld(8181, new Size(1500, 750));
        int total = world.OreDeposits.Sum(d => d.Units);
        double Share(OreDepositKind kind) =>
            world.OreDeposits.Where(d => d.Kind == kind).Sum(d => d.Units) / (double)total;

        Assert.InRange(total, 2200, 2800);
        Assert.InRange(Share(OreDepositKind.Speck), 0.10, 0.20);
        Assert.InRange(Share(OreDepositKind.Pocket), 0.28, 0.42);
        Assert.InRange(Share(OreDepositKind.Lump), 0.42, 0.58);
    }

    [Fact]
    public void FindOre_SaysWhatTheCircleHolds_AsOreIsPlacedAndMined()
    {
        var terrain = new TerrainGrid(new Size(300, 200));
        terrain.Fill(TerrainPixel.Dirt);
        var rng = new Random(11);
        var ore = new List<Position>();

        void Check()
        {
            for (int i = 0; i < 40; i++)
            {
                var center = new Position(rng.Next(-10, 310), rng.Next(-10, 210));
                int radius = rng.Next(1, 30);
                bool plain = false, core = false;
                foreach (var p in ore)
                {
                    int dx = p.X - center.X, dy = p.Y - center.Y;
                    if (dx * dx + dy * dy > radius * radius)
                        continue;
                    if (terrain.GetPixelRaw(p) == TerrainPixel.EmberiteCore)
                        core = true;
                    else
                        plain = true;
                }
                terrain.FindOre(center, radius, out bool foundPlain, out bool foundCore);
                Assert.Equal((plain, core), (foundPlain, foundCore));
            }
        }

        Check();
        // Placed every way a pixel can be written, then mined again a cell at a time.
        for (int i = 0; i < 60; i++)
        {
            var p = new Position(rng.Next(1, 299), rng.Next(1, 199));
            if (ore.Contains(p))
                continue;
            ore.Add(p);
            switch (i % 3)
            {
                case 0: terrain.PlaceOre(p, core: i % 2 == 0, dirtOnly: false); break;
                case 1: terrain.SetPixel(p, TerrainPixel.Emberite); break;
                default: terrain[p.X + p.Y * terrain.Width] = TerrainPixel.EmberiteCore; break;
            }
            Check();
        }
        while (ore.Count > 0)
        {
            terrain.SetPixel(ore[^1], TerrainPixel.Blank);
            ore.RemoveAt(ore.Count - 1);
            Check();
        }

        terrain.Fill(TerrainPixel.Emberite);
        terrain.FindOre(new Position(150, 100), 5, out bool afterFill, out _);
        Assert.True(afterFill, "a bulk write is counted too");
    }
}
