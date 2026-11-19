using Emberward.Core.Config;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using Emberward.Core.Regrowth;

namespace Emberward.Tests;

public class TerrainRegrowthTests
{
    private const int PassTicks = 18;
    private const int Hour = 60 * 60 * 24;
    private static readonly RegrowthBlocker[] NoBlockers = [];

    private static TerrainGrid Dirt(int width = 160, int height = 100)
    {
        var terrain = new TerrainGrid(new Size(width, height));
        terrain.Fill(TerrainPixel.Dirt);
        return terrain;
    }

    private static TerrainRegrowth Regrowth(int seed = 77) => new(simulationSeed: seed);

    private static int Run(TerrainRegrowth regrowth, TerrainGrid terrain, int ticks, IReadOnlyList<RegrowthBlocker>? blockers = null)
    {
        int visited = 0;
        for (int i = 0; i < ticks; i++)
        {
            regrowth.Advance(terrain, blockers ?? NoBlockers);
            visited += regrowth.CellsVisitedInLastAdvance;
        }
        return visited;
    }

    private static int Count(TerrainGrid terrain, Func<TerrainPixel, bool> isCounted)
    {
        int count = 0;
        for (int i = 0; i < terrain.Size.Area; i++)
            if (isCounted(terrain[i]))
                count++;
        return count;
    }

    /// <summary>A corridor of open ground, <paramref name="halfWidth"/> cells either side of row <paramref name="y"/>.</summary>
    private static void Tunnel(TerrainGrid terrain, int fromX, int toX, int y, int halfWidth)
    {
        for (int x = fromX; x <= toX; x++)
            for (int dy = -halfWidth; dy <= halfWidth; dy++)
                terrain.SetPixel(new Position(x, y + dy), TerrainPixel.Blank);
    }

    [Fact]
    public void ATunnel_ClosesFromItsWallsInward_AndSettlesIntoDirt()
    {
        var terrain = Dirt();
        var regrowth = Regrowth();
        Run(regrowth, terrain, 1);
        Tunnel(terrain, 20, 140, 50, halfWidth: 2);

        // A cell on the wall has three dirt neighbours: about one chance in twenty a pass. After twelve passes a
        // good part of both walls has started to grow, and the middle, which touches no dirt yet, has not.
        Run(regrowth, terrain, 12 * PassTicks);
        int wall = 0, middle = 0;
        for (int x = 25; x <= 135; x++)
        {
            foreach (int dy in new[] { -2, 2 })
                if (terrain.GetPixelRaw(new Position(x, 50 + dy)) != TerrainPixel.Blank)
                    wall++;
            if (terrain.GetPixelRaw(new Position(x, 50)) != TerrainPixel.Blank)
                middle++;
        }
        Assert.InRange(wall, 60, 150);
        Assert.Equal(0, middle);

        Run(regrowth, terrain, Hour);
        Assert.Equal(0, Count(terrain, p => p != TerrainPixel.Dirt));
        Assert.Equal(0, regrowth.CellsWaiting);
    }

    [Fact]
    public void GrowthAndSettling_HappenAtTheChancesTheSettingsGive()
    {
        // Single open cells in solid dirt have eight dirt neighbours, so each pass they grow with the full chance,
        // and growing dirt settles with its own.
        var terrain = Dirt(400, 400);
        var regrowth = Regrowth();
        Run(regrowth, terrain, 1);
        int cells = 0;
        for (int y = 10; y < 390; y += 4)
            for (int x = 10; x < 390; x += 4, cells++)
                terrain.SetPixel(new Position(x, y), TerrainPixel.Blank);

        // The writes are heard on the first tick, and every part of the map takes five passes in the ninety after it.
        const int passes = 5;
        Run(regrowth, terrain, 1 + passes * PassTicks);
        double growChance = Tweaks.World.DirtRegrowSpeed * Tweaks.World.DirtRegrowBlankModifier * 8 / 1000.0;
        double stillOpen = Math.Pow(1 - growChance, passes);
        Assert.InRange(Count(terrain, p => p == TerrainPixel.Blank) / (double)cells, stillOpen - 0.03, stillOpen + 0.03);

        // Much later all have grown; the share that has settled follows the settling chance.
        Run(regrowth, terrain, 100 * PassTicks);
        Assert.Equal(0, Count(terrain, p => p == TerrainPixel.Blank));
        double settled = 1 - Math.Pow(1 - Tweaks.World.DirtRecoverSpeed / 1000.0, 100);
        Assert.InRange(Count(terrain, p => p == TerrainPixel.Dirt) - (160_000 - cells), cells * (settled - 0.06), cells * (settled + 0.03));
    }

    [Fact]
    public void SolidGround_CostsNothing_UntilSomethingIsDugThere()
    {
        var terrain = Dirt(320, 200);
        var regrowth = Regrowth();
        Run(regrowth, terrain, 1);
        Assert.Equal(0, regrowth.CellsWaiting);
        Assert.Equal(0, Run(regrowth, terrain, 10 * PassTicks));

        // Rock written into solid ground is nothing to regrowth; a hole is.
        terrain.SetPixel(new Position(20, 20), TerrainPixel.Rock);
        Assert.Equal(0, Run(regrowth, terrain, PassTicks));
        terrain.SetPixel(new Position(300, 180), TerrainPixel.Blank);
        Run(regrowth, terrain, 1);
        Assert.Equal(1, regrowth.CellsWaiting);

        Run(regrowth, terrain, Hour);
        Assert.Equal(TerrainPixel.Dirt, terrain.GetPixelRaw(new Position(300, 180)));
        Assert.Equal(0, regrowth.CellsWaiting);
        Assert.Equal(0, Run(regrowth, terrain, 10 * PassTicks));
    }

    [Fact]
    public void InACave_OnlyTheWallsAreLookedAt_NotTheFloor()
    {
        // A cave wide enough that its floor touches no dirt: only the cells along its walls can grow.
        const int caveWidth = 240, caveHeight = 120;
        var terrain = Dirt(320, 200);
        for (int y = 40; y < 40 + caveHeight; y++)
            for (int x = 40; x < 40 + caveWidth; x++)
                terrain.SetPixelRaw(new Position(x, y), TerrainPixel.Blank);
        var regrowth = Regrowth();
        int wallCells = 2 * (caveWidth + caveHeight) - 4;

        Run(regrowth, terrain, 1);
        Assert.Equal(wallCells, regrowth.CellsWaiting);

        // Ten passes over the cave would look at its 28,800 cells ten times; this looks at a wall cell now and then.
        int visited = Run(regrowth, terrain, 10 * PassTicks);
        Assert.InRange(visited, 1, 3 * wallCells);
        Assert.True(regrowth.CellsWaiting <= wallCells);
    }

    [Fact]
    public void OpenGroundThatWasLeftAlone_GrowsOnceDirtIsPutBesideIt()
    {
        // The floor of a wide cave touches no dirt and has no appointment. Dirt dropped on it changes that for the
        // cells around the drop, however it was written.
        var terrain = Dirt(200, 200);
        for (int y = 50; y < 150; y++)
            for (int x = 50; x < 150; x++)
                terrain.SetPixelRaw(new Position(x, y), TerrainPixel.Blank);
        var regrowth = Regrowth();
        Run(regrowth, terrain, 1);
        var hollow = new Position(100, 100);
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                if (dx != 0 || dy != 0)
                    terrain.SetPixelRaw(hollow + new Offset(dx, dy), TerrainPixel.Dirt);

        Run(regrowth, terrain, 60 * PassTicks);

        Assert.NotEqual(TerrainPixel.Blank, terrain.GetPixelRaw(hollow));
    }

    [Fact]
    public void Scorch_FadesAStepAPass_ToOpenGround_AndThenGrowsOver()
    {
        var terrain = Dirt();
        var regrowth = Regrowth();
        Run(regrowth, terrain, 1);
        Tunnel(terrain, 60, 100, 50, halfWidth: 2);
        var mark = new Position(80, 50);
        terrain.SetPixel(mark, TerrainPixel.Decal);
        int start = terrain.GetScorchIntensity(mark.X + mark.Y * terrain.Width);
        Assert.Equal(TerrainGrid.ScorchIntensityMax, start);

        Run(regrowth, terrain, 1 + 5 * PassTicks);
        Assert.Equal(TerrainPixel.Decal, terrain.GetPixelRaw(mark));
        Assert.Equal(start - 5 * Tweaks.World.DecalIntensityDecay, terrain.GetScorchIntensity(mark.X + mark.Y * terrain.Width));

        // In the middle of open ground it has no dirt beside it to grow from, so it fades right out.
        Run(regrowth, terrain, (start / Tweaks.World.DecalIntensityDecay + 1) * PassTicks);
        Assert.Equal(TerrainPixel.Blank, terrain.GetPixelRaw(mark));
        Assert.Equal(0, terrain.GetScorchIntensity(mark.X + mark.Y * terrain.Width));

        Run(regrowth, terrain, Hour);
        Assert.Equal(TerrainPixel.Dirt, terrain.GetPixelRaw(mark));
    }

    [Fact]
    public void NoDirtGrows_InsideABlocker_ButItDoesOutside()
    {
        var terrain = Dirt(120, 60);
        Tunnel(terrain, 10, 109, 30, halfWidth: 2);
        RegrowthBlocker[] blockers = [new RegrowthBlocker(new Position(30, 30), 15)];
        var regrowth = Regrowth();

        Run(regrowth, terrain, 6000, blockers);

        Assert.Equal(21 * 5, Open(terrain, 20, 40));
        Assert.True(Open(terrain, 70, 90) < 21 * 5 / 2, $"the tunnel outside the blocker should mostly have closed, open={Open(terrain, 70, 90)}");
    }

    [Fact]
    public void GroundABlockerKeptOpen_CostsNothingWhileItStands_AndGrowsOnceItIsGone()
    {
        var terrain = Dirt(120, 60);
        Tunnel(terrain, 20, 40, 30, halfWidth: 2);
        RegrowthBlocker[] blockers = [new RegrowthBlocker(new Position(30, 30), 20)];
        var regrowth = Regrowth();
        Run(regrowth, terrain, 1 + Tweaks.World.RegrowthLongestLookPasses * PassTicks, blockers);
        Assert.Equal(21 * 5, Open(terrain, 20, 40));

        // Every cell that could grow has been looked at, found blocked and left alone.
        Assert.Equal(0, regrowth.CellsWaiting);
        Assert.Equal(0, Run(regrowth, terrain, 10 * PassTicks, blockers));

        Run(regrowth, terrain, 6000);
        Assert.True(Open(terrain, 20, 40) < 21 * 5 / 2, $"with the blocker gone the tunnel closes, open={Open(terrain, 20, 40)}");
    }

    [Fact]
    public void TwoRuns_WithTheSameSeedAndTheSameDigging_GiveTheSameGround_AndAnotherSeedDoesNot()
    {
        TerrainGrid a = DugAndRegrown(seed: 77), b = DugAndRegrown(seed: 77), other = DugAndRegrown(seed: 78);

        int differing = 0;
        for (int i = 0; i < a.Size.Area; i++)
        {
            Assert.Equal(a[i], b[i]);
            Assert.Equal(a.GetScorchIntensity(i), b.GetScorchIntensity(i));
            if (a[i] != other[i])
                differing++;
        }
        Assert.True(differing > 0, "another seed rolls differently");
    }

    [Fact]
    public void AMapWrittenInBulk_IsSurveyedAgain()
    {
        var terrain = Dirt();
        var regrowth = Regrowth();
        Tunnel(terrain, 20, 140, 50, halfWidth: 2);
        Run(regrowth, terrain, 5 * PassTicks);
        Assert.True(regrowth.CellsWaiting > 0);

        terrain.Fill(TerrainPixel.Dirt);
        Run(regrowth, terrain, 1);
        Assert.Equal(0, regrowth.CellsWaiting);

        terrain.Fill(TerrainPixel.Dirt);
        terrain[50 + 50 * terrain.Width] = TerrainPixel.Blank;
        Run(regrowth, terrain, 1);
        Assert.Equal(1, regrowth.CellsWaiting);
    }

    [Fact]
    public void AWorldWithoutRegrowth_GrowsNothingBack_AndKeepsNoList()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false);
        TerrainGrid terrain = world.Terrain;

        Position at = SolidDirt(terrain, width: 44, height: 9);
        for (int x = at.X + 2; x < at.X + 42; x++)
            for (int y = at.Y + 2; y < at.Y + 7; y++)
                terrain.SetPixel(new Position(x, y), TerrainPixel.Blank);
        TestSimulation.AdvanceIdle(world, frames: 40 * PassTicks);

        for (int x = at.X + 2; x < at.X + 42; x++)
            for (int y = at.Y + 2; y < at.Y + 7; y++)
                Assert.Equal(TerrainPixel.Blank, terrain.GetPixelRaw(new Position(x, y)));
        Assert.False(terrain.RegrowthWrites.Surveyed);
        Assert.DoesNotContain("regrow", world.Profile.Sections);
    }

    [Fact]
    public void InAWorld_GroundDugDuringPlay_GrowsBack()
    {
        var world = TestHelpers.CreateSeededWorld();
        TerrainGrid terrain = world.Terrain;
        TestSimulation.AdvanceIdle(world, frames: 2);

        // A tunnel through the first stretch of solid dirt on the map.
        Position at = SolidDirt(terrain, width: 44, height: 9);
        for (int x = at.X + 2; x < at.X + 42; x++)
            for (int y = at.Y + 2; y < at.Y + 7; y++)
                terrain.SetPixel(new Position(x, y), TerrainPixel.Blank);

        TestSimulation.AdvanceIdle(world, frames: 40 * PassTicks);

        int grown = 0;
        for (int x = at.X + 2; x < at.X + 42; x++)
            for (int y = at.Y + 2; y < at.Y + 7; y++)
                if (terrain.GetPixelRaw(new Position(x, y)) != TerrainPixel.Blank)
                    grown++;
        Assert.True(grown > 40, $"the tunnel's walls should have started to grow in, grown={grown}");
    }

    private static Position SolidDirt(TerrainGrid terrain, int width, int height)
    {
        for (int y = 1; y + height < terrain.Height; y += 4)
            for (int x = 1; x + width < terrain.Width; x += 4)
            {
                bool solid = true;
                for (int dy = 0; dy < height && solid; dy++)
                    for (int dx = 0; dx < width && solid; dx++)
                        solid = terrain.GetPixelRaw(new Position(x + dx, y + dy)) == TerrainPixel.Dirt;
                if (solid)
                    return new Position(x, y);
            }
        throw new InvalidOperationException("The map has no stretch of solid dirt that large.");
    }

    private static int Open(TerrainGrid terrain, int minX, int maxX)
    {
        int open = 0;
        for (int x = minX; x <= maxX; x++)
            for (int dy = -2; dy <= 2; dy++)
                if (terrain.GetPixelRaw(new Position(x, 30 + dy)) == TerrainPixel.Blank)
                    open++;
        return open;
    }

    private static TerrainGrid DugAndRegrown(int seed)
    {
        // Holes, scorch and a tunnel that keeps being dug while the ground grows back, so every rule fires.
        var terrain = Dirt();
        var regrowth = Regrowth(seed);
        for (int y = 10; y < 90; y += 20)
            for (int x = 10; x < 150; x += 20)
                for (int dy = 0; dy < 8; dy++)
                    for (int dx = 0; dx < 8; dx++)
                        terrain.SetPixel(new Position(x + dx, y + dy), (x / 20 + y / 20) % 2 == 0 ? TerrainPixel.Decal : TerrainPixel.Blank);
        for (int tick = 0; tick < 3000; tick++)
        {
            int x = 5 + tick % 150;
            for (int dy = -1; dy <= 1; dy++)
                terrain.SetPixel(new Position(x, 50 + dy), TerrainPixel.Blank);
            regrowth.Advance(terrain, NoBlockers);
        }
        return terrain;
    }
}
