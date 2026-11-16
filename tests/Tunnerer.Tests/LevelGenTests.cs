using Tunnerer.Core.Config;
using Tunnerer.Core.LevelGen;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

public class LevelGenTests
{
    private const int W = 320;
    private const int H = 200;
    private readonly Size _size = new(W, H);

    [Fact]
    public void FullGeneration_ProducesCorrectDirtPercentage()
    {
        var gen = new ToastGenerator();
        var (terrain, _) = gen.Generate(_size, seed: TestHelpers.DefaultSeed);
        terrain.MaterializeTerrain();

        double pct = TestHelpers.CountDirtPercentage(terrain, _size);
        Assert.True(pct >= 40 && pct <= 85,
            $"Dirt percentage {pct:F1}% outside expected range [40, 85]");
    }

    [Fact]
    public void FullGeneration_ProducesEnoughSpawns()
    {
        var gen = new ToastGenerator();
        var (_, spawns) = gen.Generate(_size, seed: TestHelpers.DefaultSeed);
        Assert.True(spawns.Length >= 2, $"Expected at least 2 spawns, got {spawns.Length}");
    }

    private static Position[] RandomPoints(int seed)
    {
        var size = new Size(W, H);
        var rng = new Random(seed);
        var points = new Position[Tweaks.LevelGen.TreeSize];
        for (int i = 0; i < points.Length; i++)
            points[i] = GeneratorUtils.GenerateInside(size, Tweaks.LevelGen.BorderWidth, rng);
        return points;
    }

    [Fact]
    public void SelectSpawns_FirstPointNearCenter_StillYieldsTwoSpawns()
    {
        // The playable area (map minus border) is only slightly larger than the minimum spawn distance, so a first
        // point near the centre has no partner that far away. This used to produce a one-spawn map.
        var points = RandomPoints(seed: 1);
        points[0] = new Position(W / 2, H / 2);

        var spawns = ToastGenerator.SelectSpawns(points);

        Assert.Equal(2, spawns.Length);
        Assert.NotEqual(spawns[0], spawns[1]);
        int farthest = 0;
        for (int i = 0; i < points.Length; i++)
            for (int j = i + 1; j < points.Length; j++)
                farthest = Math.Max(farthest, Position.DistanceSquared(points[i], points[j]));
        Assert.Equal(farthest, Position.DistanceSquared(spawns[0], spawns[1]));
    }

    [Fact]
    public void SelectSpawns_FirstPointKept_WhenAPartnerExists()
    {
        var points = RandomPoints(seed: 2);
        points[0] = new Position(Tweaks.LevelGen.BorderWidth, Tweaks.LevelGen.BorderWidth);
        points[1] = new Position(W - Tweaks.LevelGen.BorderWidth - 1, H - Tweaks.LevelGen.BorderWidth - 1);

        var spawns = ToastGenerator.SelectSpawns(points);

        Assert.Equal(points[0], spawns[0]);
        Assert.True(Position.DistanceSquared(spawns[0], spawns[1]) >= Tweaks.LevelGen.MinSpawnDistanceSq);
    }

    [Fact]
    public void SelectSpawns_AnySeed_AlwaysTwoDistinctSpawnsFarApart()
    {
        for (int seed = 0; seed < 20000; seed++)
        {
            var spawns = ToastGenerator.SelectSpawns(RandomPoints(seed));

            Assert.True(spawns.Length >= 2, $"seed {seed} produced {spawns.Length} spawn(s)");
            Assert.True(Position.DistanceSquared(spawns[0], spawns[1]) >= Tweaks.LevelGen.MinSpawnDistanceSq,
                $"seed {seed}: spawns too close");
        }
    }

    [Fact]
    public void FullGeneration_SpawnsAreInsideBorder()
    {
        var gen = new ToastGenerator();
        var (_, spawns) = gen.Generate(_size, seed: TestHelpers.DefaultSeed);

        int border = Tweaks.LevelGen.BorderWidth;
        foreach (var s in spawns)
        {
            Assert.True(s.X >= border && s.X < W - border, $"Spawn x={s.X} outside border");
            Assert.True(s.Y >= border && s.Y < H - border, $"Spawn y={s.Y} outside border");
        }
    }

    [Fact]
    public void FullGeneration_SpawnsAreFarEnoughApart()
    {
        var gen = new ToastGenerator();
        var (_, spawns) = gen.Generate(_size, seed: TestHelpers.DefaultSeed);

        int minDistSq = Tweaks.Base.MinDistance * Tweaks.Base.MinDistance;
        for (int i = 0; i < spawns.Length; i++)
            for (int j = i + 1; j < spawns.Length; j++)
                Assert.True(Position.DistanceSquared(spawns[i], spawns[j]) >= minDistSq,
                    $"Spawns {spawns[i]} and {spawns[j]} too close");
    }

    [Fact]
    public void SmoothingConverges_NoLevelGenPixelsRemain()
    {
        var gen = new ToastGenerator();
        var (terrain, _) = gen.Generate(_size, seed: TestHelpers.DefaultSeed);

        for (int i = 0; i < _size.Area; i++)
        {
            var pix = terrain[i];
            Assert.True(pix == TerrainPixel.LevelGenDirt || pix == TerrainPixel.LevelGenRock,
                $"Unexpected pixel {pix} at offset {i} after generation (before materialize)");
        }
    }

    [Fact]
    public void SmoothingConverges_NoBoundaryArtifacts()
    {
        var gen = new ToastGenerator();
        var (terrain, _) = gen.Generate(_size, seed: TestHelpers.DefaultSeed);
        TestHelpers.AssertBorderIsRock(terrain, W, H);
    }

    [Fact]
    public void MaterializeTerrain_NeverLeavesLevelGenValues()
    {
        var gen = new ToastGenerator();
        var (terrain, _) = gen.Generate(_size, seed: TestHelpers.DefaultSeed);
        terrain.MaterializeTerrain();
        TestHelpers.AssertNoLevelGenPixels(terrain, _size);
    }

    [Fact]
    public void GenerateInside_RespectsMargins()
    {
        int border = Tweaks.LevelGen.BorderWidth;
        var rng = new Random(42);
        for (int i = 0; i < 1000; i++)
        {
            var pos = GeneratorUtils.GenerateInside(_size, border, rng);
            Assert.True(pos.X >= border && pos.X < W - border);
            Assert.True(pos.Y >= border && pos.Y < H - border);
        }
    }

    [Fact]
    public void DrawLine_ConnectsEndpoints()
    {
        var terrain = new TerrainGrid(_size);
        terrain.Fill(TerrainPixel.LevelGenRock);

        var from = new Position(10, 10);
        var to = new Position(50, 30);
        GeneratorUtils.DrawLine(terrain, from, to, TerrainPixel.LevelGenDirt);

        Assert.Equal(TerrainPixel.LevelGenDirt, terrain.GetPixelRaw(from));
        Assert.Equal(TerrainPixel.LevelGenDirt, terrain.GetPixelRaw(to));
    }

    [Fact]
    public void Fill_SetsEveryPixel()
    {
        var terrain = new TerrainGrid(new Size(16, 16));
        terrain.Fill(TerrainPixel.Dirt);

        for (int i = 0; i < 256; i++)
            Assert.Equal(TerrainPixel.Dirt, terrain[i]);
    }

    [Fact]
    public void SetOutside_SetsOnlyBorder()
    {
        var terrain = new TerrainGrid(new Size(10, 10));
        terrain.Fill(TerrainPixel.LevelGenDirt);
        GeneratorUtils.SetOutside(terrain, TerrainPixel.LevelGenRock);

        // Border should be rock
        for (int x = 0; x < 10; x++)
        {
            Assert.Equal(TerrainPixel.LevelGenRock, terrain.GetPixelRaw(new Position(x, 0)));
            Assert.Equal(TerrainPixel.LevelGenRock, terrain.GetPixelRaw(new Position(x, 9)));
        }
        for (int y = 0; y < 10; y++)
        {
            Assert.Equal(TerrainPixel.LevelGenRock, terrain.GetPixelRaw(new Position(0, y)));
            Assert.Equal(TerrainPixel.LevelGenRock, terrain.GetPixelRaw(new Position(9, y)));
        }
        // Interior should remain dirt
        Assert.Equal(TerrainPixel.LevelGenDirt, terrain.GetPixelRaw(new Position(5, 5)));
    }

    [Fact]
    public void PointDistanceSquared_Correct()
    {
        Assert.Equal(25, Position.DistanceSquared(new Position(0, 0), new Position(3, 4)));
        Assert.Equal(0, Position.DistanceSquared(new Position(5, 5), new Position(5, 5)));
    }

    [Fact]
    public void TargetDirtAmount_MatchesCpp()
    {
        // C++: lvl->GetSize().x * lvl->GetSize().y * DirtTargetPercent / 100
        int expected = W * H * Tweaks.LevelGen.DirtTargetPercent / 100;
        Assert.Equal(expected, Tweaks.LevelGen.TargetDirtAmount(_size));
    }

    [Fact]
    public void SmoothingSteps_IsNegativeOne_MatchesCpp()
    {
        Assert.Equal(-1, Tweaks.LevelGen.SmoothingSteps);
    }

    [Fact]
    public void FullGeneration_ConnectedCaves_SpawnsReachable()
    {
        var gen = new ToastGenerator();
        var (terrain, spawns) = gen.Generate(_size, seed: TestHelpers.DefaultSeed);
        AssertSpawnsReachable(terrain, spawns, W, H);
    }

    [Fact]
    public void OptimizedMode_ProducesValidDirtPercentage()
    {
        var gen = new ToastGenerator();
        var (terrain, spawns) = gen.Generate(_size, seed: TestHelpers.DefaultSeed, mode: LevelGenMode.Optimized);
        terrain.MaterializeTerrain(parallel: true);

        double pct = TestHelpers.CountDirtPercentage(terrain, _size);
        Assert.True(pct >= 35 && pct <= 85,
            $"Optimized dirt percentage {pct:F1}% outside expected range [35, 85]");
        Assert.True(spawns.Length >= 2, $"Expected at least 2 spawns, got {spawns.Length}");
    }

    [Fact]
    public void OptimizedMode_NoBoundaryArtifacts()
    {
        var gen = new ToastGenerator();
        var (terrain, _) = gen.Generate(_size, seed: TestHelpers.DefaultSeed, mode: LevelGenMode.Optimized);
        TestHelpers.AssertBorderIsRock(terrain, W, H);
    }

    [Fact]
    public void OptimizedMode_ConnectedCaves_SpawnsReachable()
    {
        var gen = new ToastGenerator();
        var (terrain, spawns) = gen.Generate(_size, seed: TestHelpers.DefaultSeed, mode: LevelGenMode.Optimized);
        AssertSpawnsReachable(terrain, spawns, W, H);
    }

    [Fact]
    public void OptimizedMode_LargeMap_ProducesValidResult()
    {
        var largeSize = new Size(1000, 500);
        var gen = new ToastGenerator();
        var (terrain, spawns) = gen.Generate(largeSize, seed: TestHelpers.DefaultSeed, mode: LevelGenMode.Optimized);
        terrain.MaterializeTerrain(parallel: true);

        Assert.True(spawns.Length >= 2);

        double pct = TestHelpers.CountDirtPercentage(terrain, largeSize);
        Assert.True(pct >= 40 && pct <= 85,
            $"Large optimized dirt percentage {pct:F1}% outside expected range [40, 85]");
    }

    private static void AssertSpawnsReachable(TerrainGrid terrain, Position[] spawns, int w, int h)
    {
        var visited = new bool[w * h];
        var queue = new Queue<Position>();
        queue.Enqueue(spawns[0]);
        visited[spawns[0].X + spawns[0].Y * w] = true;

        while (queue.Count > 0)
        {
            var pos = queue.Dequeue();
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = pos.X + dx, ny = pos.Y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int off = nx + ny * w;
                    if (visited[off]) continue;
                    if (terrain.GetPixelRaw(new Position(nx, ny)) != TerrainPixel.LevelGenDirt) continue;
                    visited[off] = true;
                    queue.Enqueue(new Position(nx, ny));
                }
        }

        for (int i = 1; i < spawns.Length; i++)
        {
            int off = spawns[i].X + spawns[i].Y * w;
            Assert.True(visited[off], $"Spawn {i} at {spawns[i]} is not connected to spawn 0");
        }
    }
}
