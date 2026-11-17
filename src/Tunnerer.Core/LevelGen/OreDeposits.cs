namespace Tunnerer.Core.LevelGen;

using Tunnerer.Core.Config;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

/// <summary>The three sizes of emberite deposit (DESIGN.md §6.3).</summary>
public enum OreDepositKind
{
    /// <summary>One to three cells in dirt, anywhere outside the bases.</summary>
    Speck,
    /// <summary>An irregular blob of a couple of dozen cells, away from the bases.</summary>
    Pocket,
    /// <summary>A large blob with a warm core, far from the bases and toward the map centre.</summary>
    Lump,
}

/// <summary>A deposit as it was seeded: where it started, its kind, and how many cells (of them core cells) it has.</summary>
public readonly record struct OreDeposit(Position Center, OreDepositKind Kind, int Cells, int CoreCells)
{
    /// <summary>The ore the deposit holds when mined out.</summary>
    public int Units => (Cells - CoreCells) * Tweaks.Ore.PlainCellUnits + CoreCells * Tweaks.Ore.CoreCellUnits;
}

/// <summary>
/// Seeds emberite over a generated level: lumps first (far from the bases, biased toward the centre), then pockets,
/// then specks scattered through the dirt. Counts scale with map area (<see cref="Tweaks.Ore"/>); on a small map the
/// distance rules shrink with it and a floor of pockets and lumps is kept (<see cref="DistanceScale"/>). Each deposit grows
/// from a start cell by random accretion over its own neighbourhood, so generation touches only the cells it
/// converts and their neighbours, never the whole grid. Deterministic for a given seed.
/// </summary>
public static class OreDeposits
{
    private const int AttemptsPerDeposit = 40;

    public static List<OreDeposit> Place(TerrainGrid terrain, IReadOnlyList<Position> bases, int seed)
    {
        var deposits = new List<OreDeposit>();
        int margin = Tweaks.LevelGen.BorderWidth + 4;
        if (terrain.Width <= margin * 2 || terrain.Height <= margin * 2)
            return deposits;

        var rng = new Random(seed);
        long area = (long)terrain.Width * terrain.Height;
        double scale = DistanceScale(area);
        PlaceLumps(terrain, bases, rng, Math.Max(Tweaks.Ore.MinLumps, CountFor(area, Tweaks.Ore.LumpEveryCells)), margin,
            scale, deposits);
        PlaceScattered(terrain, bases, rng, Math.Max(Tweaks.Ore.MinPockets, CountFor(area, Tweaks.Ore.PocketEveryCells)),
            margin, OreDepositKind.Pocket, Tweaks.Ore.PocketMinCells, Tweaks.Ore.PocketMaxCells,
            Scaled(Tweaks.Ore.PocketMinDistanceFromBase, scale), dirtOnly: false, deposits);
        PlaceScattered(terrain, bases, rng, CountFor(area, Tweaks.Ore.SpeckEveryCells), margin, OreDepositKind.Speck,
            Tweaks.Ore.SpeckMinCells, Tweaks.Ore.SpeckMaxCells, Scaled(Tweaks.Ore.SpeckMinDistanceFromBase, scale),
            dirtOnly: true, deposits);
        return deposits;
    }

    private static int CountFor(long area, int everyCells) => (int)((area + everyCells / 2) / everyCells);

    /// <summary>
    /// What the minimum distances are multiplied by on a map of <paramref name="area"/> cells: the square root of its
    /// share of <see cref="Tweaks.Ore.DistanceReferenceArea"/>, between <see cref="Tweaks.Ore.MinDistanceScale"/> and 1.
    /// </summary>
    public static double DistanceScale(long area) =>
        Math.Clamp(Math.Sqrt(area / (double)Tweaks.Ore.DistanceReferenceArea), Tweaks.Ore.MinDistanceScale, 1.0);

    private static int Scaled(int distance, double scale) => (int)(distance * scale);

    private static void PlaceLumps(TerrainGrid terrain, IReadOnlyList<Position> bases, Random rng, int count, int margin,
        double scale, List<OreDeposit> deposits)
    {
        var middle = new Position(terrain.Width / 2, terrain.Height / 2);
        int lumpMargin = margin + 8;
        int halfW = terrain.Width / 2 - lumpMargin, halfH = terrain.Height / 2 - lumpMargin;
        if (halfW <= 0 || halfH <= 0)
            return;
        int minFromBase = Scaled(Tweaks.Ore.LumpMinDistanceFromBase, scale);
        int minBetween = Scaled(Tweaks.Ore.LumpMinDistanceBetween, scale);
        int minFromBaseSq = minFromBase * minFromBase, minBetweenSq = minBetween * minBetween;
        var lumps = new List<Position>();

        for (int attempt = 0; attempt < count * AttemptsPerDeposit && lumps.Count < count; attempt++)
        {
            double fraction = DeterministicMath.Pow(rng.NextDouble(), Tweaks.Ore.LumpCentreBias);
            double angle = rng.NextDouble() * Math.PI * 2.0;
            int cells = rng.Next(Tweaks.Ore.LumpMinCells, Tweaks.Ore.LumpMaxCells + 1);
            var pos = new Position(
                middle.X + (int)(DeterministicMath.Cos(angle) * fraction * halfW),
                middle.Y + (int)(DeterministicMath.Sin(angle) * fraction * halfH));
            if (TooClose(pos, bases, minFromBaseSq) || TooClose(pos, lumps, minBetweenSq))
                continue;

            var deposit = Grow(terrain, rng, pos, OreDepositKind.Lump, cells, Tweaks.Ore.LumpCoreCells, dirtOnly: false);
            if (deposit is OreDeposit placed)
            {
                lumps.Add(pos);
                deposits.Add(placed);
            }
        }
    }

    private static void PlaceScattered(TerrainGrid terrain, IReadOnlyList<Position> bases, Random rng, int count,
        int margin, OreDepositKind kind, int minCells, int maxCells, int minFromBase, bool dirtOnly,
        List<OreDeposit> deposits)
    {
        int minFromBaseSq = minFromBase * minFromBase;
        int placed = 0;
        for (int attempt = 0; attempt < count * AttemptsPerDeposit && placed < count; attempt++)
        {
            var pos = new Position(
                margin + rng.Next(terrain.Width - margin * 2),
                margin + rng.Next(terrain.Height - margin * 2));
            int cells = rng.Next(minCells, maxCells + 1);
            if (TooClose(pos, bases, minFromBaseSq))
                continue;

            if (Grow(terrain, rng, pos, kind, cells, coreCells: 0, dirtOnly) is OreDeposit deposit)
            {
                deposits.Add(deposit);
                placed++;
            }
        }
    }

    /// <summary>
    /// Grows one deposit from <paramref name="start"/>: the first <paramref name="coreCells"/> cells are taken nearest
    /// the start (a compact core), the rest from random cells on the growing edge (an irregular blob). Stops early when
    /// the edge runs out of ground it may convert. Null when not even the start cell could be converted.
    /// </summary>
    private static OreDeposit? Grow(TerrainGrid terrain, Random rng, Position start, OreDepositKind kind, int cells,
        int coreCells, bool dirtOnly)
    {
        var frontier = new List<Position> { start };
        var seen = new HashSet<Position> { start };
        int placed = 0, cores = 0;
        while (frontier.Count > 0 && placed < cells)
        {
            bool core = placed < coreCells;
            int index = core ? NearestIndex(frontier, start) : rng.Next(frontier.Count);
            var pos = frontier[index];
            frontier[index] = frontier[^1];
            frontier.RemoveAt(frontier.Count - 1);
            if (!terrain.PlaceOre(pos, core, dirtOnly))
                continue;

            placed++;
            if (core)
                cores++;
            foreach (var step in Neighbours)
            {
                var next = pos + step;
                if (seen.Add(next))
                    frontier.Add(next);
            }
        }

        return placed > 0 ? new OreDeposit(start, kind, placed, cores) : null;
    }

    private static readonly Offset[] Neighbours = [new(1, 0), new(-1, 0), new(0, 1), new(0, -1)];

    private static int NearestIndex(List<Position> cells, Position to)
    {
        int best = 0, bestSq = int.MaxValue;
        for (int i = 0; i < cells.Count; i++)
        {
            int d = Position.DistanceSquared(cells[i], to);
            if (d < bestSq)
            {
                best = i;
                bestSq = d;
            }
        }
        return best;
    }

    private static bool TooClose(Position pos, IReadOnlyList<Position> others, int minDistanceSq)
    {
        foreach (var other in others)
            if (Position.DistanceSquared(pos, other) < minDistanceSq)
                return true;
        return false;
    }
}
