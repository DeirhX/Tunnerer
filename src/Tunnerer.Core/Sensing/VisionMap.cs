namespace Tunnerer.Core.Sensing;

using Tunnerer.Core.Config;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

/// <summary>Something that sees: a tank, a base, a machine.</summary>
/// <param name="Radius">How far it sees along open lines of sight, in cells.</param>
/// <param name="NearRadius">Within this distance it sees everything, through solid ground too.</param>
public readonly record struct VisionSource(Position Position, int Radius, int NearRadius);

/// <summary>
/// What one side can see (fog of war). Every cell has a visibility level (0 = out of sight, 255 = in plain sight,
/// fading toward the edge of a source's reach), an explored flag (seen at least once) and a remembered pixel
/// (<see cref="Known"/>: the terrain as that side last saw it).
/// </summary>
/// <remarks>
/// It only observes. Nothing in the simulation reads it, so tracking vision cannot change a result
/// (<c>SimulationFingerprintTests</c>). The work per update is proportional to the cells in sight, never to the
/// size of the map: cell lists drive every pass and nothing sweeps the grid.
/// Sources that stay put (bases, machines) are recast every few ticks into one layer, the tank every tick into
/// another; a cell's level is the higher of the two.
/// </remarks>
public sealed class VisionMap
{
    private readonly int _width;
    private readonly int _height;
    private readonly Layer _fixedLayer;
    private readonly Layer _movingLayer;
    private readonly byte[] _scratch;
    private readonly bool[] _explored;
    private readonly KnownTerrain _known;
    private readonly List<Position> _knownChanges = new();

    private bool _hasDirtyRect;
    private int _dirtyMinX, _dirtyMinY, _dirtyMaxX, _dirtyMaxY;

    /// <summary>Starts with nothing seen; <see cref="Known"/> starts as a copy of <paramref name="terrain"/>.</summary>
    public VisionMap(ITerrainPixelReadOnly terrain)
    {
        _width = terrain.Width;
        _height = terrain.Height;
        int area = _width * _height;
        _fixedLayer = new Layer(area);
        _movingLayer = new Layer(area);
        _scratch = new byte[area];
        _explored = new bool[area];
        _known = new KnownTerrain(terrain);
    }

    public int Width => _width;
    public int Height => _height;

    /// <summary>Number of cells in sight right now (a cell both layers see counts twice).</summary>
    public int VisibleCellCount => _fixedLayer.Cells.Count + _movingLayer.Cells.Count;

    /// <summary>0 when the cell is out of sight, otherwise 1..255.</summary>
    public byte LevelAt(int offset) => Math.Max(_fixedLayer.Level[offset], _movingLayer.Level[offset]);

    public bool IsVisible(int offset) => (_fixedLayer.Level[offset] | _movingLayer.Level[offset]) != 0;

    /// <summary>False for a position outside the map.</summary>
    public bool IsVisible(Position pos) =>
        (uint)pos.X < (uint)_width && (uint)pos.Y < (uint)_height && IsVisible(pos.X + pos.Y * _width);

    /// <summary>The cell has been in sight at least once.</summary>
    public bool IsExplored(int offset) => _explored[offset];

    public bool IsExplored(Position pos) =>
        (uint)pos.X < (uint)_width && (uint)pos.Y < (uint)_height && _explored[pos.X + pos.Y * _width];

    /// <summary>
    /// The terrain as this side last saw it: a cell in sight matches the world, a cell out of sight keeps the pixel
    /// it had when it was last seen. A cell never seen holds what the map had when tracking began; presentation
    /// hides those anyway.
    /// </summary>
    public ITerrainPixelReadOnly Known => _known;

    /// <summary>Cells whose <see cref="Known"/> pixel changed in the last update; valid until the next one.</summary>
    public IReadOnlyList<Position> KnownChanges => _knownChanges;

    /// <summary>The cells whose level or explored flag changed since <see cref="ClearDirtyRect"/>, as one rectangle.</summary>
    public bool TryGetDirtyRect(out Rect dirtyRect)
    {
        dirtyRect = _hasDirtyRect
            ? RectMath.FromMinMaxInclusive(_dirtyMinX, _dirtyMinY, _dirtyMaxX, _dirtyMaxY)
            : default;
        return _hasDirtyRect;
    }

    public void ClearDirtyRect() => _hasDirtyRect = false;

    /// <summary>
    /// Recasts vision and brings <see cref="Known"/> up to date for every cell in sight.
    /// <paramref name="fixedSources"/> is null on the ticks that keep the last cast of the sources that stay put.
    /// </summary>
    public void Update(
        ITerrainPixelReadOnly terrain, IReadOnlyList<VisionSource>? fixedSources, IReadOnlyList<VisionSource> movingSources)
    {
        _knownChanges.Clear();
        if (fixedSources != null)
            Recast(_fixedLayer, fixedSources, terrain);
        Recast(_movingLayer, movingSources, terrain);
        SyncKnown(_fixedLayer.Cells, terrain);
        SyncKnown(_movingLayer.Cells, terrain);
    }

    private void SyncKnown(List<int> cells, ITerrainPixelReadOnly terrain)
    {
        ReadOnlySpan<TerrainPixel> actual = terrain.Data;
        TerrainPixel[] known = _known.Pixels;
        foreach (int offset in cells)
        {
            TerrainPixel pixel = actual[offset];
            if (known[offset] == pixel)
                continue;
            known[offset] = pixel;
            _knownChanges.Add(new Position(offset % _width, offset / _width));
        }
    }

    private void Recast(Layer layer, IReadOnlyList<VisionSource> sources, ITerrainPixelReadOnly terrain)
    {
        List<int> lit = layer.Spare;
        lit.Clear();
        ReadOnlySpan<TerrainPixel> pixels = terrain.Data;
        for (int i = 0; i < sources.Count; i++)
            Cast(sources[i], pixels, lit);

        // Cells that fell out of sight, then cells that came into it or changed level.
        byte[] level = layer.Level;
        foreach (int offset in layer.Cells)
        {
            if (_scratch[offset] != 0)
                continue;
            level[offset] = 0;
            MarkDirty(offset);
        }
        foreach (int offset in lit)
        {
            byte now = _scratch[offset];
            _scratch[offset] = 0;
            if (level[offset] == now)
                continue;
            level[offset] = now;
            _explored[offset] = true;
            MarkDirty(offset);
        }
        layer.Swap();
    }

    /// <summary>
    /// Lights what <paramref name="source"/> sees into the scratch levels: a ray to every cell on the edge of its
    /// square, cut to a circle. A ray runs through open ground and stops at the first cell that blocks it; inside
    /// the near radius everything is lit. The walls around the open ground in sight are then lit
    /// <see cref="Tweaks.Vision.WallSkin"/> cells deep, whatever angle they were seen from.
    /// </summary>
    private void Cast(in VisionSource source, ReadOnlySpan<TerrainPixel> pixels, List<int> lit)
    {
        int cx = source.Position.X, cy = source.Position.Y;
        if ((uint)cx >= (uint)_width || (uint)cy >= (uint)_height)
            return;
        int radius = Math.Max(source.Radius, source.NearRadius);
        if (radius <= 0)
        {
            Light(cx + cy * _width, 255, lit);
            return;
        }

        var reach = new Reach(radius, source.NearRadius);
        int firstLit = lit.Count;
        for (int t = -radius; t <= radius; t++)
        {
            CastRay(cx, cy, cx + t, cy - radius, reach, pixels, lit);
            CastRay(cx, cy, cx + t, cy + radius, reach, pixels, lit);
        }
        for (int t = -radius + 1; t < radius; t++)
        {
            CastRay(cx, cy, cx - radius, cy + t, reach, pixels, lit);
            CastRay(cx, cy, cx + radius, cy + t, reach, pixels, lit);
        }
        LightWalls(firstLit, lit.Count, pixels, lit);
    }

    /// <summary>
    /// Lights the solid cells within <see cref="Tweaks.Vision.WallSkin"/> of each open cell in
    /// <paramref name="lit"/>[<paramref name="from"/>..<paramref name="to"/>) at that cell's level. Only open cells
    /// that touch a wall do any work.
    /// </summary>
    private void LightWalls(int from, int to, ReadOnlySpan<TerrainPixel> pixels, List<int> lit)
    {
        const int skin = Tweaks.Vision.WallSkin;
        for (int i = from; i < to; i++)
        {
            int offset = lit[i];
            if (Pixel.BlocksLink(pixels[offset]))
                continue;
            int x = offset % _width, y = offset / _width;
            if (!TouchesWall(x, y, pixels))
                continue;

            byte level = _scratch[offset];
            int minX = Math.Max(0, x - skin), maxX = Math.Min(_width - 1, x + skin);
            int minY = Math.Max(0, y - skin), maxY = Math.Min(_height - 1, y + skin);
            for (int ny = minY; ny <= maxY; ny++)
            {
                int row = ny * _width;
                for (int nx = minX; nx <= maxX; nx++)
                    if (Pixel.BlocksLink(pixels[row + nx]))
                        Light(row + nx, level, lit);
            }
        }
    }

    private bool TouchesWall(int x, int y, ReadOnlySpan<TerrainPixel> pixels)
    {
        int minX = Math.Max(0, x - 1), maxX = Math.Min(_width - 1, x + 1);
        int minY = Math.Max(0, y - 1), maxY = Math.Min(_height - 1, y + 1);
        for (int ny = minY; ny <= maxY; ny++)
            for (int nx = minX; nx <= maxX; nx++)
                if (Pixel.BlocksLink(pixels[nx + ny * _width]))
                    return true;
        return false;
    }

    /// <summary>A source's reach, in squared cells, and the band at its edge over which the level fades.</summary>
    private readonly struct Reach
    {
        public readonly int RadiusSq;
        public readonly int NearSq;
        private readonly int _fullSq;

        public Reach(int radius, int nearRadius)
        {
            RadiusSq = radius * radius;
            NearSq = nearRadius * nearRadius;
            int full = Math.Max(0, radius - Tweaks.Vision.EdgeFade);
            _fullSq = full * full;
        }

        public byte LevelAt(int distSq) =>
            distSq <= _fullSq ? (byte)255 : (byte)Math.Max(1, 255 * (RadiusSq - distSq) / (RadiusSq - _fullSq));
    }

    private void CastRay(int x0, int y0, int x1, int y1, in Reach reach, ReadOnlySpan<TerrainPixel> pixels, List<int> lit)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int x = x0, y = y0;
        bool blocked = false;

        while (true)
        {
            if ((uint)x >= (uint)_width || (uint)y >= (uint)_height)
                return;
            int ox = x - x0, oy = y - y0;
            int distSq = ox * ox + oy * oy;
            if (distSq > reach.RadiusSq)
                return;

            int offset = x + y * _width;
            // The source's own cell never blocks: a machine stands in the ground it was planted in.
            if (!blocked && distSq != 0 && Pixel.BlocksLink(pixels[offset]))
                blocked = true;
            if (blocked && distSq > reach.NearSq)
                return;
            Light(offset, reach.LevelAt(distSq), lit);

            if (x == x1 && y == y1)
                return;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
    }

    private void Light(int offset, byte level, List<int> lit)
    {
        byte current = _scratch[offset];
        if (current == 0)
            lit.Add(offset);
        if (level > current)
            _scratch[offset] = level;
    }

    private void MarkDirty(int offset)
    {
        int x = offset % _width, y = offset / _width;
        if (!_hasDirtyRect)
        {
            _hasDirtyRect = true;
            _dirtyMinX = _dirtyMaxX = x;
            _dirtyMinY = _dirtyMaxY = y;
            return;
        }
        if (x < _dirtyMinX) _dirtyMinX = x;
        if (x > _dirtyMaxX) _dirtyMaxX = x;
        if (y < _dirtyMinY) _dirtyMinY = y;
        if (y > _dirtyMaxY) _dirtyMaxY = y;
    }

    /// <summary>One set of sources' levels, and the list of cells they light.</summary>
    private sealed class Layer(int area)
    {
        public readonly byte[] Level = new byte[area];
        public List<int> Cells { get; private set; } = new();
        public List<int> Spare { get; private set; } = new();

        public void Swap() => (Cells, Spare) = (Spare, Cells);
    }

    private sealed class KnownTerrain : ITerrainPixelReadOnly
    {
        public readonly TerrainPixel[] Pixels;

        public KnownTerrain(ITerrainPixelReadOnly terrain)
        {
            Size = terrain.Size;
            Pixels = terrain.Data.ToArray();
        }

        public Size Size { get; }
        public int Width => Size.X;
        public int Height => Size.Y;
        public bool IsInside(Position pos) => (uint)pos.X < (uint)Width && (uint)pos.Y < (uint)Height;
        public TerrainPixel GetPixel(Position pos) => IsInside(pos) ? Pixels[pos.X + pos.Y * Width] : TerrainPixel.Rock;
        public TerrainPixel GetPixelRaw(Position pos) => Pixels[pos.X + pos.Y * Width];
        public TerrainPixel GetPixelRaw(int offset) => Pixels[offset];
        public ReadOnlySpan<TerrainPixel> Data => Pixels;
    }
}
