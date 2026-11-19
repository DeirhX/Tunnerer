namespace Emberward.Core.Sensing;

using Emberward.Core.Config;
using Emberward.Core.Terrain;

/// <summary>How a source's sight depends on the ground around it.</summary>
internal enum SightKind
{
    /// <summary>Sees only its own cell (no radius, or it stands outside the map); the ground changes nothing.</summary>
    Point,

    /// <summary>
    /// Sees its whole circle, through solid ground too (a machine). Its rays light the same cells whatever the ground
    /// is; only the walls lit around open ground follow the terrain, so a change is repaired in a small window.
    /// </summary>
    SeeThrough,

    /// <summary>Sees along open lines (a tank, a base): a change in its reach means casting it again.</summary>
    LineOfSight,
}

/// <summary>
/// One source's sight, kept between updates: the level it gives every cell of the square it can reach, which is
/// its radius plus the wall skin on every side.
/// </summary>
internal sealed class VisionEye
{
    public VisionSource Source { get; private set; }
    public SightKind Kind { get; private set; }

    /// <summary>The radius its rays run to.</summary>
    public int Radius { get; private set; }

    /// <summary>Cells from the source to the edge of its square.</summary>
    public int Extent { get; private set; }
    public int Side => 2 * Extent + 1;

    /// <summary>Map position of the square's first cell; it may lie outside the map.</summary>
    public int OriginX => Source.Position.X - Extent;
    public int OriginY => Source.Position.Y - Extent;

    /// <summary>The level it gives each cell of its square, row by row.</summary>
    public byte[] Levels { get; set; } = [];

    /// <summary>A see-through eye: what its rays alone light, in the same layout. Shared by eyes of one radius.</summary>
    public byte[]? RayLevels { get; set; }

    /// <summary>The update that last listed this source; an eye a later update does not list is closed.</summary>
    public int SeenInUpdate { get; set; }

    /// <summary>Ground that blocks sight changed in its square since it was last cast: the cells' bounds.</summary>
    public bool IsStale { get; private set; }
    public int StaleMinX { get; private set; }
    public int StaleMinY { get; private set; }
    public int StaleMaxX { get; private set; }
    public int StaleMaxY { get; private set; }

    public void Reset(in VisionSource source, bool insideMap)
    {
        Source = source;
        Radius = Math.Max(source.Radius, source.NearRadius);
        Kind = Radius <= 0 || !insideMap ? SightKind.Point
            : source.NearRadius >= source.Radius ? SightKind.SeeThrough
            : SightKind.LineOfSight;
        Extent = Kind == SightKind.Point ? 0 : Radius + Tweaks.Vision.WallSkin;
        RayLevels = null;
        IsStale = false;
    }

    public bool Covers(int x, int y) =>
        (uint)(x - OriginX) < (uint)Side && (uint)(y - OriginY) < (uint)Side;

    public byte LevelAt(int x, int y) => Levels[(x - OriginX) + (y - OriginY) * Side];

    public void NoteChange(int x, int y)
    {
        if (!IsStale)
        {
            IsStale = true;
            StaleMinX = StaleMaxX = x;
            StaleMinY = StaleMaxY = y;
            return;
        }
        if (x < StaleMinX) StaleMinX = x;
        if (x > StaleMaxX) StaleMaxX = x;
        if (y < StaleMinY) StaleMinY = y;
        if (y > StaleMaxY) StaleMaxY = y;
    }

    public void ClearStale() => IsStale = false;
}

/// <summary>Casts a <see cref="VisionEye"/> into its own square of levels.</summary>
internal static class VisionCast
{
    /// <summary>The map and, inside it, the square the levels are written to.</summary>
    private readonly record struct Frame(int Width, int Height, int OriginX, int OriginY, int Side);

    /// <summary>
    /// Casts the whole eye: a ray to every cell on the edge of its radius' square, cut to a circle. A ray runs through
    /// open ground and stops at the first cell that blocks it; inside the near radius everything is lit. The walls
    /// around the open ground in sight are then lit <see cref="Tweaks.Vision.WallSkin"/> cells deep, whatever angle
    /// they were seen from.
    /// </summary>
    public static void Cast(VisionEye eye, ReadOnlySpan<TerrainPixel> pixels, int width, int height)
    {
        byte[] levels = eye.Levels;
        Array.Clear(levels, 0, eye.Side * eye.Side);
        int cx = eye.Source.Position.X, cy = eye.Source.Position.Y;
        if ((uint)cx >= (uint)width || (uint)cy >= (uint)height)
            return;
        if (eye.Kind == SightKind.Point)
        {
            levels[0] = 255;
            return;
        }

        var frame = new Frame(width, height, eye.OriginX, eye.OriginY, eye.Side);
        Rays(cx, cy, eye.Radius, eye.Source.NearRadius, frame, pixels, levels);
        int minX = Math.Max(0, eye.OriginX), maxX = Math.Min(width - 1, eye.OriginX + eye.Side - 1);
        int minY = Math.Max(0, eye.OriginY), maxY = Math.Min(height - 1, eye.OriginY + eye.Side - 1);
        Walls(levels, frame, pixels, minX, minY, maxX, maxY, minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Brings a see-through eye up to date in the cells <paramref name="minX"/>..<paramref name="maxX"/>,
    /// <paramref name="minY"/>..<paramref name="maxY"/> (inside its square and the map) and leaves the rest as it was.
    /// The window has to hold every cell the changed ground can reach: the changed cells and
    /// <see cref="Tweaks.Vision.WallSkin"/> + 1 cells around them.
    /// </summary>
    public static void CastWindow(
        VisionEye eye, ReadOnlySpan<TerrainPixel> pixels, int width, int height, int minX, int minY, int maxX, int maxY)
    {
        byte[] levels = eye.Levels;
        byte[] rays = eye.RayLevels!;
        int side = eye.Side;
        for (int y = minY; y <= maxY; y++)
        {
            int row = (y - eye.OriginY) * side - eye.OriginX;
            Array.Copy(rays, row + minX, levels, row + minX, maxX - minX + 1);
        }

        // Every open cell that can light a wall inside the window lies within the skin of it.
        const int skin = Tweaks.Vision.WallSkin;
        int fromMinX = Math.Max(Math.Max(0, eye.OriginX), minX - skin);
        int fromMaxX = Math.Min(Math.Min(width - 1, eye.OriginX + side - 1), maxX + skin);
        int fromMinY = Math.Max(Math.Max(0, eye.OriginY), minY - skin);
        int fromMaxY = Math.Min(Math.Min(height - 1, eye.OriginY + side - 1), maxY + skin);
        var frame = new Frame(width, height, eye.OriginX, eye.OriginY, side);
        Walls(levels, frame, pixels, fromMinX, fromMinY, fromMaxX, fromMaxY, minX, minY, maxX, maxY);
    }

    /// <summary>
    /// What the rays of a see-through source of <paramref name="radius"/> light, in an eye's layout. The ground
    /// cannot change it: such a source sees everything inside its radius, so it is worked out once, on open ground.
    /// </summary>
    public static byte[] RayLevelsOf(int radius)
    {
        int extent = radius + Tweaks.Vision.WallSkin;
        int side = 2 * extent + 1;
        var levels = new byte[side * side];
        var open = new TerrainPixel[side * side];
        Array.Fill(open, TerrainPixel.Blank);
        Rays(extent, extent, radius, nearRadius: radius, new Frame(side, side, 0, 0, side), open, levels);
        return levels;
    }

    private static void Rays(
        int cx, int cy, int radius, int nearRadius, in Frame frame, ReadOnlySpan<TerrainPixel> pixels, byte[] levels)
    {
        var fade = new Fade(radius, nearRadius);
        for (int t = -radius; t <= radius; t++)
        {
            CastRay(cx, cy, cx + t, cy - radius, fade, frame, pixels, levels);
            CastRay(cx, cy, cx + t, cy + radius, fade, frame, pixels, levels);
        }
        for (int t = -radius + 1; t < radius; t++)
        {
            CastRay(cx, cy, cx - radius, cy + t, fade, frame, pixels, levels);
            CastRay(cx, cy, cx + radius, cy + t, fade, frame, pixels, levels);
        }
    }

    private static void CastRay(
        int x0, int y0, int x1, int y1, in Fade fade, in Frame frame, ReadOnlySpan<TerrainPixel> pixels, byte[] levels)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int x = x0, y = y0;
        bool blocked = false;

        while (true)
        {
            if ((uint)x >= (uint)frame.Width || (uint)y >= (uint)frame.Height)
                return;
            int ox = x - x0, oy = y - y0;
            int distSq = ox * ox + oy * oy;
            if (distSq > fade.RadiusSq)
                return;

            // The source's own cell never blocks: a machine stands in the ground it was planted in.
            if (!blocked && distSq != 0 && Pixel.BlocksLink(pixels[x + y * frame.Width]))
                blocked = true;
            if (blocked && distSq > fade.NearSq)
                return;
            byte level = blocked ? fade.NearLevelAt(distSq) : fade.LevelAt(distSq);
            int local = (x - frame.OriginX) + (y - frame.OriginY) * frame.Side;
            if (level > levels[local])
                levels[local] = level;

            if (x == x1 && y == y1)
                return;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
    }

    /// <summary>
    /// Lights the solid cells within <see cref="Tweaks.Vision.WallSkin"/> of each lit open cell in the "from" bounds,
    /// at that cell's level on the wall's face and less with every cell of depth, writing only inside the "to" bounds.
    /// Only open cells that touch a wall do any work, and a wall never lights another, so one pass over the square is
    /// enough. All bounds are map cells, inclusive, inside the map and the frame's square.
    /// </summary>
    private static void Walls(
        byte[] levels, in Frame frame, ReadOnlySpan<TerrainPixel> pixels,
        int fromMinX, int fromMinY, int fromMaxX, int fromMaxY, int toMinX, int toMinY, int toMaxX, int toMaxY)
    {
        const int skin = Tweaks.Vision.WallSkin;
        for (int y = fromMinY; y <= fromMaxY; y++)
        {
            int localRow = (y - frame.OriginY) * frame.Side - frame.OriginX;
            for (int x = fromMinX; x <= fromMaxX; x++)
            {
                int level = levels[localRow + x];
                if (level == 0 || Pixel.BlocksLink(pixels[x + y * frame.Width]) || !TouchesWall(x, y, frame, pixels))
                    continue;

                int minX = Math.Max(toMinX, x - skin), maxX = Math.Min(toMaxX, x + skin);
                int minY = Math.Max(toMinY, y - skin), maxY = Math.Min(toMaxY, y + skin);
                for (int ny = minY; ny <= maxY; ny++)
                {
                    int row = ny * frame.Width;
                    int wallRow = (ny - frame.OriginY) * frame.Side - frame.OriginX;
                    for (int nx = minX; nx <= maxX; nx++)
                    {
                        if (!Pixel.BlocksLink(pixels[row + nx]))
                            continue;
                        // Full at the wall's face, fading with depth so the wall has no hard inner edge.
                        int depth = Math.Max(Math.Abs(nx - x), Math.Abs(ny - y));
                        byte lit = (byte)Math.Max(1, level * (skin + 1 - depth) / skin);
                        if (lit > levels[wallRow + nx])
                            levels[wallRow + nx] = lit;
                    }
                }
            }
        }
    }

    private static bool TouchesWall(int x, int y, in Frame frame, ReadOnlySpan<TerrainPixel> pixels)
    {
        int minX = Math.Max(0, x - 1), maxX = Math.Min(frame.Width - 1, x + 1);
        int minY = Math.Max(0, y - 1), maxY = Math.Min(frame.Height - 1, y + 1);
        for (int ny = minY; ny <= maxY; ny++)
            for (int nx = minX; nx <= maxX; nx++)
                if (Pixel.BlocksLink(pixels[nx + ny * frame.Width]))
                    return true;
        return false;
    }

    /// <summary>A source's reach, in squared cells, and the band at its edge over which the level fades.</summary>
    private readonly struct Fade
    {
        public readonly int RadiusSq;
        public readonly int NearSq;
        private readonly int _fullSq;
        private readonly int _nearFullSq;

        public Fade(int radius, int nearRadius)
        {
            RadiusSq = radius * radius;
            NearSq = nearRadius * nearRadius;
            _fullSq = FullSq(radius);
            _nearFullSq = FullSq(nearRadius);
        }

        /// <summary>The squared radius up to which a reach of <paramref name="radius"/> is at full level: a short reach fades over its outer half.</summary>
        private static int FullSq(int radius)
        {
            int full = radius - Math.Min(Tweaks.Vision.EdgeFade, radius / 2);
            return full * full;
        }

        /// <summary>The level of a cell seen along open ground.</summary>
        public byte LevelAt(int distSq) => Between(distSq, _fullSq, RadiusSq);

        /// <summary>The level of a cell seen through solid ground, which fades toward the near radius.</summary>
        public byte NearLevelAt(int distSq) => Math.Min(LevelAt(distSq), Between(distSq, _nearFullSq, NearSq));

        private static byte Between(int distSq, int fullSq, int edgeSq) =>
            distSq <= fullSq ? (byte)255 : (byte)Math.Max(1, 255 * (edgeSq - distSq) / Math.Max(1, edgeSq - fullSq));
    }
}
