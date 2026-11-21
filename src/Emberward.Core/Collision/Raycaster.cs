namespace Emberward.Core.Collision;

using Emberward.Core.Terrain;
using Emberward.Core.Types;

public static class Raycaster
{
    public static IEnumerable<Position> CastRay(PositionF origin, VectorF direction, int maxSteps)
    {
        float x = origin.X, y = origin.Y;
        float dx = direction.X, dy = direction.Y;

        for (int i = 0; i < maxSteps; i++)
        {
            x += dx;
            y += dy;
            yield return new Position((int)x, (int)y);
        }
    }

    public static void BresenhamLine(Position from, Position to, Action<Position> visit)
    {
        WalkBresenham(from, to, pos => { visit(pos); return false; });
    }

    public static bool BresenhamLineAny(Position from, Position to, Func<Position, bool> predicate)
    {
        return WalkBresenham(from, to, predicate);
    }

    /// <summary>
    /// Line of sight through open ground: true when no cell on the Bresenham line from <paramref name="from"/> to
    /// <paramref name="to"/> (both ends included) blocks a link (<see cref="Pixel.BlocksLink"/>). Links, the link
    /// preview and network coverage use this test.
    /// </summary>
    public static bool HasOpenLine(ITerrainPixelReadOnly terrain, Position from, Position to) =>
        !TryFindLineBlock(terrain, from, to, out _);

    /// <summary>
    /// A creature's line of sight: true when no cell on the Bresenham line from <paramref name="from"/> to
    /// <paramref name="to"/> (both ends included) stops sight (<see cref="Pixel.BlocksCreatureSight"/>: rock,
    /// hot and frost rock, concrete). Dirt and open ground do not. Integer only, so it is deterministic.
    /// </summary>
    public static bool HasCreatureLine(ITerrainPixelReadOnly terrain, Position from, Position to)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        int x0 = from.X, y0 = from.Y, x1 = to.X, y1 = to.Y;
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            if (Pixel.BlocksCreatureSight(terrain.GetPixel(new Position(x0, y0))))
                return false;
            if (x0 == x1 && y0 == y1)
                return true;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>
    /// Like <see cref="HasOpenLine"/>, walking from <paramref name="from"/>, but also gives the first blocking cell.
    /// Returns true when the line is blocked.
    /// </summary>
    public static bool TryFindLineBlock(ITerrainPixelReadOnly terrain, Position from, Position to, out Position firstBlock) =>
        TryFindBlock(terrain, from, to, shot: false, out firstBlock);

    /// <summary>
    /// A clear shot: like <see cref="HasOpenLine"/>, but base walls and anything else a bullet stops on
    /// (<see cref="Pixel.IsAnyCollision"/>) block it too. A turret aims along this.
    /// </summary>
    public static bool HasClearShot(ITerrainPixelReadOnly terrain, Position from, Position to) =>
        !TryFindBlock(terrain, from, to, shot: true, out _);

    /// <summary>
    /// A shot that passes through a little soil: true when the Bresenham line from <paramref name="from"/> to
    /// <paramref name="to"/> (both ends included) meets nothing a bullet stops on (<see cref="Pixel.IsAnyCollision"/>)
    /// but at most <paramref name="maxSoilCells"/> cells of soil. A gatling aims along this. Integer only, so it is
    /// deterministic.
    /// </summary>
    public static bool HasShotThroughSoil(ITerrainPixelReadOnly terrain, Position from, Position to, int maxSoilCells)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        int x0 = from.X, y0 = from.Y, x1 = to.X, y1 = to.Y;
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int soil = 0;
        while (true)
        {
            var pixel = terrain.GetPixel(new Position(x0, y0));
            if (Pixel.IsAnyCollision(pixel) && (!Pixel.IsSoil(pixel) || ++soil > maxSoilCells))
                return false;
            if (x0 == x1 && y0 == y1)
                return true;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>
    /// How many cells on the Bresenham line between <paramref name="from"/> and <paramref name="to"/> (both ends left
    /// out) are solid ground, dirt included (<see cref="Pixel.IsAnyCollision"/>), counting at most
    /// <paramref name="limit"/>. Sound uses it to muffle what it hears through rock and dirt.
    /// </summary>
    public static int CountSolidCells(ITerrainPixelReadOnly terrain, Position from, Position to, int limit)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        int x0 = from.X, y0 = from.Y, x1 = to.X, y1 = to.Y;
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int count = 0;
        while (count < limit && (x0 != x1 || y0 != y1))
        {
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
            if (x0 == x1 && y0 == y1)
                break;
            var pos = new Position(x0, y0);
            if (terrain.IsInside(pos) && Pixel.IsAnyCollision(terrain.GetPixelRaw(pos)))
                count++;
        }
        return count;
    }

    private static bool TryFindBlock(ITerrainPixelReadOnly terrain, Position from, Position to, bool shot, out Position firstBlock)
    {
        // The walk of WalkBresenham, written out: this runs for every link, coverage and charging test.
        int x0 = from.X, y0 = from.Y, x1 = to.X, y1 = to.Y;
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            var pos = new Position(x0, y0);
            var pixel = terrain.GetPixel(pos);
            if (Pixel.BlocksLink(pixel) || (shot && Pixel.IsAnyCollision(pixel)))
            {
                firstBlock = pos;
                return true;
            }
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
        firstBlock = default;
        return false;
    }

    private static bool WalkBresenham(Position from, Position to, Func<Position, bool> visitor)
    {
        int x0 = from.X, y0 = from.Y, x1 = to.X, y1 = to.Y;
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            if (visitor(new Position(x0, y0))) return true;
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
        return false;
    }
}
