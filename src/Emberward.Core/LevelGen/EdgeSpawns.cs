namespace Emberward.Core.LevelGen;

using Emberward.Core.Config;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

/// <summary>
/// Where the bases stand (DESIGN.md §6.7). Every base backs onto the map's border at the same inset
/// (<see cref="Tweaks.Spawns.EdgeInset"/>), clear of the corners, so each has its back to rock and can be approached
/// from the front and the sides only.
/// <para>
/// The places a base may take form a loop round the border. The first base takes a random place on it; the others
/// follow at even shares of the loop, each moved by a random jitter of up to <see cref="Tweaks.Spawns.JitterShare"/>
/// of that spacing, and the players beyond the first are dealt to those places in a random order. So the other
/// bases are far away and roughly across the map, but where exactly cannot be read off one's own place. A draw is
/// kept when its bases are far enough apart and balanced (<see cref="Tweaks.Spawns.MinCentreDistanceRatio"/>,
/// <see cref="Tweaks.Spawns.MinNeighbourDistanceRatio"/>); after <see cref="Tweaks.Spawns.LayoutAttempts"/> draws
/// the most balanced one is kept, and a last draw without jitter (even spacing) places them. No two bases overlap: a
/// map too small for that throws an <see cref="ArgumentException"/>.
/// </para>
/// </summary>
public static class EdgeSpawns
{
    /// <summary>Picks <paramref name="playerCount"/> base positions for a map of <paramref name="size"/>.</summary>
    public static Position[] Place(Size size, int playerCount, Random rng)
    {
        if (playerCount <= 0)
            return [];

        var loop = new BorderLoop(size);
        int length = loop.Length;
        double spacing = (double)length / playerCount;
        int jitter = (int)(Tweaks.Spawns.JitterShare * spacing);
        long minDistance = Math.Min(Tweaks.Base.MinDistance, (int)(Tweaks.Spawns.MinSpacingShare * spacing));
        long minDistanceSq = minDistance * minDistance;

        // Which player takes which place round the loop after the first: dealt at random, so with three or more
        // bases no one knows which neighbour is which.
        var order = new int[playerCount - 1];
        for (int i = 0; i < order.Length; i++)
            order[i] = i + 1;
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        Position[]? best = null;
        double bestBalance = double.NegativeInfinity;
        for (int attempt = 0; attempt <= Tweaks.Spawns.LayoutAttempts; attempt++)
        {
            // The last draw has no jitter: evenly spaced bases are always at least the minimum apart.
            var layout = Draw(loop, rng, order, attempt < Tweaks.Spawns.LayoutAttempts ? jitter : 0);
            if (!Separated(layout) || ClosestPairSq(layout) < minDistanceSq)
            {
                if (attempt == Tweaks.Spawns.LayoutAttempts && best == null)
                    return Separated(layout) ? layout : throw ImpossibleLayout(size, playerCount);
                continue;
            }
            double balance = Balance(layout, size);
            if (balance >= 1.0)
                return layout;
            if (balance > bestBalance)
            {
                best = layout;
                bestBalance = balance;
            }
        }
        return best!;
    }

    /// <summary>
    /// Makes the ground round every base alike: rock from its back wall to the map edge (the back is sealed, a tank
    /// cannot pass behind it), and dirt for <see cref="Tweaks.Spawns.ApronDepth"/> cells round its other three sides,
    /// so no rock blocks a gate. Runs on level-generation pixels, before the bases are drawn into the terrain.
    /// </summary>
    public static void PrepareGround(TerrainGrid terrain, IReadOnlyList<Position> spawns)
    {
        int half = Tweaks.Base.BaseSize / 2;
        int apron = Tweaks.Spawns.ApronDepth;
        foreach (var spawn in spawns)
        {
            var side = BorderSide.Nearest(terrain.Size, spawn);
            var (along, depth) = side.ToLocal(spawn);
            for (int v = 0; v <= depth + half + apron; v++)
            {
                var pixel = v < depth - half ? TerrainPixel.LevelGenRock : TerrainPixel.LevelGenDirt;
                for (int u = along - half - apron; u <= along + half + apron; u++)
                {
                    var pos = side.ToMap(u, v);
                    if (GeneratorUtils.IsInterior(pos.X, pos.Y, terrain.Width, terrain.Height))
                        terrain.SetPixelRaw(pos, pixel);
                }
            }
        }
    }

    private static Position[] Draw(BorderLoop loop, Random rng, int[] order, int jitter)
    {
        int count = order.Length + 1;
        int length = loop.Length;
        int first = rng.Next(length);
        var layout = new Position[count];
        layout[0] = loop.At(first);
        for (int k = 1; k < count; k++)
        {
            int offset = (int)(((long)k * length + count / 2) / count) + rng.Next(-jitter, jitter + 1);
            int at = ((first + offset) % length + length) % length;
            layout[order[k - 1]] = loop.At(at);
        }
        return layout;
    }

    /// <summary>
    /// Whether no two bases overlap: their centres are at least a base's width apart on one axis (the footprints are
    /// squares of <see cref="Tweaks.Base.BaseSize"/>). On a small map the distance rule above can fall to nothing, so
    /// this is what keeps the walls of two bases apart.
    /// </summary>
    private static bool Separated(Position[] layout)
    {
        for (int i = 0; i < layout.Length; i++)
            for (int j = i + 1; j < layout.Length; j++)
                if (Math.Max(Math.Abs(layout[i].X - layout[j].X), Math.Abs(layout[i].Y - layout[j].Y)) < Tweaks.Base.BaseSize)
                    return false;
        return true;
    }

    private static ArgumentException ImpossibleLayout(Size size, int playerCount) =>
        new($"A {size.X}x{size.Y} map has no room for {playerCount} bases of {Tweaks.Base.BaseSize} cells that do not overlap.");

    private static long ClosestPairSq(Position[] layout)
    {
        long closest = long.MaxValue;
        for (int i = 0; i < layout.Length; i++)
            for (int j = i + 1; j < layout.Length; j++)
                closest = Math.Min(closest, Position.DistanceSquared(layout[i], layout[j]));
        return closest;
    }

    /// <summary>
    /// How balanced a layout is against the thresholds: 1 or more when the bases' distances from the map centre and
    /// (three or more bases) to their nearest neighbour are all within the ratios of <see cref="Tweaks.Spawns"/>.
    /// </summary>
    private static double Balance(Position[] layout, Size size)
    {
        double centre = Ratio(layout, i => DistanceFromCentre(layout[i], size)) / Tweaks.Spawns.MinCentreDistanceRatio;
        if (layout.Length < 3)
            return centre;
        double neighbours = Ratio(layout, i => NearestNeighbour(layout, i)) / Tweaks.Spawns.MinNeighbourDistanceRatio;
        return Math.Min(centre, neighbours);
    }

    /// <summary>The distance of <paramref name="pos"/> from the map centre.</summary>
    private static double DistanceFromCentre(Position pos, Size size)
    {
        double dx = pos.X - (size.X - 1) / 2.0, dy = pos.Y - (size.Y - 1) / 2.0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double NearestNeighbour(Position[] layout, int i)
    {
        long nearest = long.MaxValue;
        for (int j = 0; j < layout.Length; j++)
            if (j != i)
                nearest = Math.Min(nearest, Position.DistanceSquared(layout[i], layout[j]));
        return Math.Sqrt(nearest);
    }

    private static double Ratio(Position[] layout, Func<int, double> measure)
    {
        double min = double.MaxValue, max = 0.0;
        for (int i = 0; i < layout.Length; i++)
        {
            double m = measure(i);
            min = Math.Min(min, m);
            max = Math.Max(max, m);
        }
        return max <= 0.0 ? 1.0 : min / max;
    }

    /// <summary>
    /// The places a base centre may take: the rectangle <see cref="Tweaks.Spawns.EdgeInset"/> in from the map edge,
    /// without the stretches within <see cref="Tweaks.Spawns.CornerClearance"/> of a corner, walked clockwise from
    /// the top-left. On a map too small for those numbers the inset stops at the middle and each side keeps at least
    /// its middle cell.
    /// </summary>
    private readonly struct BorderLoop
    {
        private readonly Size _size;
        private readonly int _inset;
        private readonly int _fromX, _toX, _fromY, _toY;

        public BorderLoop(Size size)
        {
            _size = size;
            _inset = Inset(size);
            int fromCorner = _inset + Tweaks.Spawns.CornerClearance;
            (_fromX, _toX) = Span(size.X, fromCorner);
            (_fromY, _toY) = Span(size.Y, fromCorner);
        }

        private int Across => _toX - _fromX + 1;
        private int Down => _toY - _fromY + 1;

        public int Length => 2 * (Across + Down);

        public Position At(int t)
        {
            int right = _size.X - 1 - _inset, bottom = _size.Y - 1 - _inset;
            if (t < Across)
                return new Position(_fromX + t, _inset);
            t -= Across;
            if (t < Down)
                return new Position(right, _fromY + t);
            t -= Down;
            if (t < Across)
                return new Position(_toX - t, bottom);
            t -= Across;
            return new Position(_inset, _toY - t);
        }

        private static (int From, int To) Span(int extent, int fromCorner)
        {
            int from = fromCorner, to = extent - 1 - fromCorner;
            return to >= from ? (from, to) : ((extent - 1) / 2, (extent - 1) / 2);
        }
    }

    /// <summary>The inset of a base centre from the map edge on a map of <paramref name="size"/>.</summary>
    private static int Inset(Size size) => Math.Min(Tweaks.Spawns.EdgeInset, (Math.Min(size.X, size.Y) - 1) / 2);

    /// <summary>
    /// The border a base backs onto, and its local frame: <c>along</c> the border and <c>depth</c> from the map edge
    /// (0 on the edge cells).
    /// </summary>
    private readonly record struct BorderSide(Size Size, int Side)
    {
        public static BorderSide Nearest(Size size, Position pos)
        {
            int top = pos.Y, right = size.X - 1 - pos.X, bottom = size.Y - 1 - pos.Y, left = pos.X;
            int nearest = Math.Min(Math.Min(top, right), Math.Min(bottom, left));
            int side = nearest == top ? 0 : nearest == right ? 1 : nearest == bottom ? 2 : 3;
            return new BorderSide(size, side);
        }

        public (int Along, int Depth) ToLocal(Position pos) => Side switch
        {
            0 => (pos.X, pos.Y),
            1 => (pos.Y, Size.X - 1 - pos.X),
            2 => (pos.X, Size.Y - 1 - pos.Y),
            _ => (pos.Y, pos.X),
        };

        public Position ToMap(int along, int depth) => Side switch
        {
            0 => new Position(along, depth),
            1 => new Position(Size.X - 1 - depth, along),
            2 => new Position(along, Size.Y - 1 - depth),
            _ => new Position(depth, along),
        };
    }
}
