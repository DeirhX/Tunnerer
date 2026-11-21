using Emberward.Core.Config;
using Emberward.Core.LevelGen;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

namespace Emberward.Tests;

/// <summary>
/// Where level generation puts the bases (<see cref="EdgeSpawns"/>): each backs onto the border at the same inset,
/// they are far apart and balanced, and where the others are cannot be read off one's own place.
/// </summary>
public class EdgeSpawnTests
{
    private const int Seeds = 400;
    private static readonly Size Small = new(320, 200);
    private static readonly Size Network = new(480, 300);
    private static readonly Size Large = new(1500, 750);
    private static readonly Size[] Sizes = [Small, Network, Large];
    private static readonly int[] PlayerCounts = [2, 3, 4, 8];

    private static Position[] Place(Size size, int players, int seed) => EdgeSpawns.Place(size, players, new Random(seed));

    private static int EdgeDistance(Position p, Size size) =>
        Math.Min(Math.Min(p.X, size.X - 1 - p.X), Math.Min(p.Y, size.Y - 1 - p.Y));

    private static double Distance(Position a, Position b) => Math.Sqrt(Position.DistanceSquared(a, b));

    [Fact]
    public void EveryBase_BacksOntoTheBorder_AtTheSameInset_ClearOfTheCorners()
    {
        int half = Tweaks.Base.BaseSize / 2;
        int fromCorner = Tweaks.Spawns.EdgeInset + Tweaks.Spawns.CornerClearance;
        foreach (var size in Sizes)
            foreach (int players in PlayerCounts)
                for (int seed = 0; seed < Seeds; seed++)
                {
                    var spawns = Place(size, players, seed);
                    Assert.Equal(players, spawns.Length);
                    foreach (var s in spawns)
                    {
                        string where = $"{size.X}x{size.Y}, {players} players, seed {seed}: base at {s}";
                        Assert.True(EdgeDistance(s, size) == Tweaks.Spawns.EdgeInset, $"{where} is not at the inset");
                        // The whole base square inside the map, off the edge cells.
                        Assert.True(s.X - half >= 1 && s.Y - half >= 1 && s.X + half <= size.X - 2 && s.Y + half <= size.Y - 2,
                            $"{where} does not fit inside the map");
                        // Against one border only: the perpendicular borders are far away.
                        bool onTopOrBottom = Math.Min(s.Y, size.Y - 1 - s.Y) == Tweaks.Spawns.EdgeInset;
                        int toSide = onTopOrBottom ? Math.Min(s.X, size.X - 1 - s.X) : Math.Min(s.Y, size.Y - 1 - s.Y);
                        Assert.True(toSide >= fromCorner, $"{where} is in a corner");
                    }
                }
    }

    // The least distance between two bases: Base.MinDistance, or less where the map is too small to place that many
    // bases so far apart (0.6 of the even spacing round the border).
    public static TheoryData<int, int, int, int> MinimumDistances => new()
    {
        { 320, 200, 2, 150 }, { 320, 200, 3, 118 }, { 320, 200, 4, 88 }, { 320, 200, 8, 44 },
        { 480, 300, 2, 150 }, { 480, 300, 3, 150 }, { 480, 300, 4, 150 }, { 480, 300, 8, 83 },
        { 1500, 750, 2, 150 }, { 1500, 750, 3, 150 }, { 1500, 750, 4, 150 }, { 1500, 750, 8, 150 },
    };

    [Theory]
    [MemberData(nameof(MinimumDistances))]
    public void EveryPairOfBases_IsFarApart(int width, int height, int players, int minDistance)
    {
        var size = new Size(width, height);
        for (int seed = 0; seed < Seeds; seed++)
        {
            var spawns = Place(size, players, seed);
            for (int i = 0; i < spawns.Length; i++)
                for (int j = i + 1; j < spawns.Length; j++)
                    Assert.True(Position.DistanceSquared(spawns[i], spawns[j]) >= minDistance * minDistance,
                        $"seed {seed}: bases {spawns[i]} and {spawns[j]} closer than {minDistance}");
        }
    }

    [Theory]
    [InlineData(100, 100, 2)]
    [InlineData(100, 100, 3)]
    [InlineData(100, 100, 4)]
    [InlineData(112, 112, 3)]
    [InlineData(150, 150, 4)]
    public void OnSmallMaps_NoTwoBasesOverlap(int width, int height, int players)
    {
        var size = new Size(width, height);
        for (int seed = 0; seed < Seeds; seed++)
        {
            Position[] spawns;
            try
            {
                spawns = Place(size, players, seed);
            }
            catch (ArgumentException)
            {
                continue;
            }
            for (int i = 0; i < spawns.Length; i++)
                for (int j = i + 1; j < spawns.Length; j++)
                    Assert.True(
                        Math.Max(Math.Abs(spawns[i].X - spawns[j].X), Math.Abs(spawns[i].Y - spawns[j].Y)) >= Tweaks.Base.BaseSize,
                        $"{width}x{height}, {players} players, seed {seed}: bases {spawns[i]} and {spawns[j]} overlap");
        }
    }

    [Fact]
    public void TwoBases_OnA100x100Map_AreOnOppositeSides()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var spawns = Place(new Size(100, 100), 2, seed);
            Assert.True(Math.Max(Math.Abs(spawns[0].X - spawns[1].X), Math.Abs(spawns[0].Y - spawns[1].Y)) >= Tweaks.Base.BaseSize);
        }
    }

    [Fact]
    public void AMapWithNoRoomForTheBases_Throws()
    {
        // A map this small has four places on its loop; neighbours are under one base's width apart, so only two
        // bases on opposite sides fit (and not even those below 35 cells across the loop).
        Assert.Throws<ArgumentException>(() => Place(new Size(60, 60), 2, 1));
        Assert.Throws<ArgumentException>(() => Place(new Size(100, 100), 3, 1));
    }

    [Fact]
    public void TwoBases_AreAboutAsFarFromTheCentre_AndAcrossTheMap()
    {
        foreach (var size in Sizes)
            for (int seed = 0; seed < Seeds; seed++)
            {
                var spawns = Place(size, 2, seed);
                double a = CentreDistance(spawns[0], size), b = CentreDistance(spawns[1], size);
                Assert.True(Math.Min(a, b) >= Tweaks.Spawns.MinCentreDistanceRatio * Math.Max(a, b),
                    $"{size.X}x{size.Y} seed {seed}: {spawns[0]} and {spawns[1]} are {a:F0} and {b:F0} from the centre");
                // Across the map: farther apart than either is from the centre.
                Assert.True(Distance(spawns[0], spawns[1]) > Math.Max(a, b),
                    $"{size.X}x{size.Y} seed {seed}: {spawns[0]} and {spawns[1]} are on the same side of the map");
            }
    }

    [Theory]
    [InlineData(320, 200, 3)]
    [InlineData(320, 200, 4)]
    [InlineData(480, 300, 4)]
    [InlineData(1500, 750, 4)]
    public void SeveralBases_HaveComparableNearestNeighbours(int width, int height, int players)
    {
        var size = new Size(width, height);
        for (int seed = 0; seed < Seeds; seed++)
        {
            var spawns = Place(size, players, seed);
            var nearest = spawns.Select((s, i) => spawns.Where((_, j) => j != i).Min(o => Distance(s, o))).ToArray();
            Assert.True(nearest.Min() >= Tweaks.Spawns.MinNeighbourDistanceRatio * nearest.Max(),
                $"seed {seed}: nearest neighbours from {nearest.Min():F0} to {nearest.Max():F0}");
        }
    }

    private static double CentreDistance(Position p, Size size)
    {
        double dx = p.X - (size.X - 1) / 2.0, dy = p.Y - (size.Y - 1) / 2.0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    [Theory]
    [InlineData(320, 200)]
    [InlineData(1500, 750)]
    public void TheEnemyBase_IsNotDeterminedByOnesOwn(int width, int height)
    {
        var size = new Size(width, height);
        int fromMirror = 0, notOpposite = 0;
        // Own bases grouped by where they stand (the border they back onto and a stretch of it) to the enemy bases
        // seen with them.
        var seenWith = new Dictionary<(int Side, int Stretch), List<Position>>();
        int stretch = Math.Max(size.X, size.Y) / 16;
        for (int seed = 0; seed < Seeds * 2; seed++)
        {
            var spawns = Place(size, 2, seed);
            var (own, enemy) = (spawns[0], spawns[1]);
            var mirror = new Position(size.X - 1 - own.X, size.Y - 1 - own.Y);
            if (Distance(enemy, mirror) > Math.Min(size.X, size.Y) / 10)
                fromMirror++;
            if (Side(enemy, size) != (Side(own, size) + 2) % 4)
                notOpposite++;
            var key = (Side(own, size), (Side(own, size) % 2 == 0 ? own.X : own.Y) / stretch);
            if (!seenWith.TryGetValue(key, out var list))
                seenWith[key] = list = [];
            list.Add(enemy);
        }

        // Rarely the point mirror of one's own base, and not always on the opposite border.
        Assert.True(fromMirror >= Seeds, $"only {fromMirror} of {Seeds * 2} enemy bases away from the mirror point");
        Assert.True(notOpposite >= Seeds / 20, $"only {notOpposite} of {Seeds * 2} enemy bases off the opposite border");
        // From the same stretch of border the enemy base turns up over a wide stretch of the map.
        int minSpread = Math.Min(size.X, size.Y) / 4;
        foreach (var (key, enemies) in seenWith.Where(g => g.Value.Count >= 8))
        {
            int spreadX = enemies.Max(e => e.X) - enemies.Min(e => e.X);
            int spreadY = enemies.Max(e => e.Y) - enemies.Min(e => e.Y);
            Assert.True(Math.Max(spreadX, spreadY) >= minSpread,
                $"own base on side {key.Side}, stretch {key.Stretch}: enemy bases within {spreadX}x{spreadY} cells");
        }
    }

    // 0 top, 1 right, 2 bottom, 3 left: the border the base backs onto.
    private static int Side(Position p, Size size)
    {
        int inset = Tweaks.Spawns.EdgeInset;
        return p.Y == inset ? 0 : p.X == size.X - 1 - inset ? 1 : p.Y == size.Y - 1 - inset ? 2 : 3;
    }

    [Theory]
    [InlineData(320, 200, 2, LevelGenMode.Deterministic)]
    [InlineData(480, 300, 4, LevelGenMode.Deterministic)]
    [InlineData(320, 200, 2, LevelGenMode.Optimized)]
    [InlineData(480, 300, 8, LevelGenMode.Optimized)]
    public void EveryBase_StandsInTheSameGround_AndReachesEveryOther(int width, int height, int players, LevelGenMode mode)
    {
        var size = new Size(width, height);
        int half = Tweaks.Base.BaseSize / 2, apron = Tweaks.Spawns.ApronDepth;
        for (int seed = 0; seed < 12; seed++)
        {
            var (terrain, spawns) = new ToastGenerator().Generate(size, seed, mode, players);
            Assert.Equal(players, spawns.Length);
            foreach (var s in spawns)
            {
                // Local frame of the border the base backs onto: along it, and depth from the map edge.
                int side = Side(s, size);
                for (int depth = 0; depth <= Tweaks.Spawns.EdgeInset + half + apron; depth++)
                    for (int along = -half - apron; along <= half + apron; along++)
                    {
                        var pos = side switch
                        {
                            0 => new Position(s.X + along, depth),
                            1 => new Position(size.X - 1 - depth, s.Y + along),
                            2 => new Position(s.X + along, size.Y - 1 - depth),
                            _ => new Position(depth, s.Y + along),
                        };
                        var expected = depth < Tweaks.Spawns.EdgeInset - half ? TerrainPixel.LevelGenRock : TerrainPixel.LevelGenDirt;
                        Assert.True(terrain.GetPixelRaw(pos) == expected,
                            $"seed {seed}: {pos} by the base at {s} is {terrain.GetPixelRaw(pos)}, not {expected}");
                    }
            }
            AssertAllReachable(terrain, spawns, seed);
        }
    }

    private static void AssertAllReachable(TerrainGrid terrain, Position[] spawns, int seed)
    {
        int w = terrain.Width, h = terrain.Height;
        var visited = new bool[w * h];
        var queue = new Queue<int>();
        GeneratorUtils.FloodFillDirt(terrain, visited, queue, spawns[0].X + spawns[0].Y * w);
        foreach (var s in spawns)
            Assert.True(visited[s.X + s.Y * w], $"seed {seed}: the base at {s} cannot be reached from {spawns[0]}");
    }
}
