namespace Tunnerer.Core.Sensing;

using Tunnerer.Core.Config;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

/// <summary>
/// Something that sees: a tank, a base, a machine. Two sources with the same values are one source, and a source
/// that moves is a new one.
/// </summary>
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
/// (<c>SimulationFingerprintTests</c>).
/// The work per update follows what changed, never the size of the map or the number of cells in sight. Each source
/// keeps its own cast (<see cref="VisionEye"/>) and a cell's level is the highest any source gives it. A source is
/// cast when it appears, and again only when ground that blocks sight changes within its reach; one that sees
/// through the ground repairs just the cells around the change. A source that stands still in ground that stays as
/// it is costs nothing. The terrain has to report its changes (<see cref="ITerrainChangeTracker"/>): a cell written
/// behind its back is not noticed.
/// </remarks>
public sealed class VisionMap
{
    private const int TileSize = Tweaks.Vision.SourceTileSize;

    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _level;
    private readonly bool[] _explored;
    // Whether each cell blocked sight when the sources were last brought up to date: a change of pixel that leaves
    // this as it was (regrowing dirt that settles, a scorch mark on open ground) changes nothing anyone sees.
    private readonly bool[] _blocksSight;
    private readonly KnownTerrain _known;
    private readonly List<Position> _knownChanges = new();

    private readonly Dictionary<VisionSource, VisionEye> _eyeOf = new();
    private readonly List<VisionEye> _eyes = new();
    // The eyes whose square overlaps each tile, so a cell finds the sources that can see it without a search.
    private readonly List<VisionEye>?[] _eyesByTile;
    private readonly int _tilesX;
    private readonly Stack<VisionEye> _spareEyes = new();
    private readonly Dictionary<int, Stack<byte[]>> _spareLevels = new();
    private readonly Dictionary<int, byte[]> _rayLevels = new();
    private byte[] _before = [];
    private int _update;

    private DirtyRect _dirty;

    /// <summary>Starts with nothing seen; <see cref="Known"/> starts as a copy of <paramref name="terrain"/>.</summary>
    public VisionMap(ITerrainPixelReadOnly terrain)
    {
        _width = terrain.Width;
        _height = terrain.Height;
        int area = _width * _height;
        _level = new byte[area];
        _explored = new bool[area];
        _known = new KnownTerrain(terrain);
        _blocksSight = new bool[area];
        ReadOnlySpan<TerrainPixel> pixels = terrain.Data;
        for (int i = 0; i < area; i++)
            _blocksSight[i] = Pixel.BlocksLink(pixels[i]);
        _tilesX = (_width + TileSize - 1) / TileSize;
        _eyesByTile = new List<VisionEye>?[_tilesX * ((_height + TileSize - 1) / TileSize)];
    }

    public int Width => _width;
    public int Height => _height;

    /// <summary>0 when the cell is out of sight, otherwise 1..255.</summary>
    public byte LevelAt(int offset) => _level[offset];

    public bool IsVisible(int offset) => _level[offset] != 0;

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

    /// <summary>
    /// How many cells the last update worked out a source's level for: the measure of what vision cost. It is zero
    /// while every source stands still and no ground that blocks sight changes around one.
    /// </summary>
    public int CellsCastInLastUpdate { get; private set; }

    /// <summary>The cells whose level or explored flag changed since <see cref="ClearDirtyRect"/>, as one rectangle.</summary>
    public bool TryGetDirtyRect(out Rect dirtyRect) => _dirty.TryGet(out dirtyRect);

    public void ClearDirtyRect() => _dirty.Clear();

    /// <summary>
    /// Brings vision and <see cref="Known"/> up to date. <paramref name="sources"/> is everything that sees right
    /// now: a source listed again where it was keeps its cast, a new one is cast, one no longer listed stops seeing.
    /// <paramref name="changedCells"/> is every cell whose pixel changed since the last update (a cell may be listed
    /// more than once, and cells that did not change do no harm).
    /// </summary>
    public void Update(
        ITerrainPixelReadOnly terrain, IReadOnlyList<VisionSource> sources, IReadOnlyList<Position> changedCells)
    {
        _knownChanges.Clear();
        CellsCastInLastUpdate = 0;
        _update++;
        ReadOnlySpan<TerrainPixel> pixels = terrain.Data;

        for (int i = 0; i < changedCells.Count; i++)
            NoteTerrainChange(changedCells[i], pixels);

        // New sources first and lost ones after: a source that moved a cell then leaves the cells both see alone.
        for (int i = 0; i < sources.Count; i++)
        {
            if (!_eyeOf.TryGetValue(sources[i], out VisionEye? eye))
                eye = Open(sources[i], pixels);
            eye.SeenInUpdate = _update;
        }
        for (int i = _eyes.Count - 1; i >= 0; i--)
        {
            VisionEye eye = _eyes[i];
            if (eye.SeenInUpdate != _update)
                Close(i, pixels);
            else if (eye.IsStale)
                Recast(eye, pixels);
        }

        // A cell in sight follows the world; one that came into sight was brought up to date as it did.
        for (int i = 0; i < changedCells.Count; i++)
        {
            Position cell = changedCells[i];
            if (IsVisible(cell))
                SyncKnown(cell.X, cell.Y, pixels);
        }
    }

    /// <summary>A changed cell matters to the sources around it only if it now blocks sight and did not, or the reverse.</summary>
    private void NoteTerrainChange(Position cell, ReadOnlySpan<TerrainPixel> pixels)
    {
        int x = cell.X, y = cell.Y;
        if ((uint)x >= (uint)_width || (uint)y >= (uint)_height)
            return;
        int offset = x + y * _width;
        bool blocks = Pixel.BlocksLink(pixels[offset]);
        if (_blocksSight[offset] == blocks)
            return;
        _blocksSight[offset] = blocks;
        List<VisionEye>? near = _eyesByTile[x / TileSize + y / TileSize * _tilesX];
        if (near == null)
            return;
        foreach (VisionEye eye in near)
            if (eye.Kind != SightKind.Point && eye.Covers(x, y))
                eye.NoteChange(x, y);
    }

    private VisionEye Open(in VisionSource source, ReadOnlySpan<TerrainPixel> pixels)
    {
        VisionEye eye = _spareEyes.Count > 0 ? _spareEyes.Pop() : new VisionEye();
        bool insideMap = (uint)source.Position.X < (uint)_width && (uint)source.Position.Y < (uint)_height;
        eye.Reset(source, insideMap);
        eye.Levels = RentLevels(eye.Side * eye.Side);
        if (eye.Kind == SightKind.SeeThrough)
        {
            if (!_rayLevels.TryGetValue(eye.Radius, out byte[]? rays))
                _rayLevels[eye.Radius] = rays = VisionCast.RayLevelsOf(eye.Radius);
            eye.RayLevels = rays;
        }
        VisionCast.Cast(eye, pixels, _width, _height);
        CellsCastInLastUpdate += eye.Side * eye.Side;

        _eyeOf.Add(source, eye);
        _eyes.Add(eye);
        Bounds b = BoundsOf(eye);
        for (int ty = b.MinY / TileSize; !b.IsEmpty && ty <= b.MaxY / TileSize; ty++)
            for (int tx = b.MinX / TileSize; tx <= b.MaxX / TileSize; tx++)
                (_eyesByTile[tx + ty * _tilesX] ??= new()).Add(eye);
        for (int y = b.MinY; y <= b.MaxY; y++)
            for (int x = b.MinX; x <= b.MaxX; x++)
            {
                byte now = eye.LevelAt(x, y);
                if (now != 0)
                    Replace(x, y, was: 0, now, pixels);
            }
        return eye;
    }

    private void Close(int index, ReadOnlySpan<TerrainPixel> pixels)
    {
        VisionEye eye = _eyes[index];
        _eyes[index] = _eyes[^1];
        _eyes.RemoveAt(_eyes.Count - 1);
        _eyeOf.Remove(eye.Source);
        Bounds b = BoundsOf(eye);
        for (int ty = b.MinY / TileSize; !b.IsEmpty && ty <= b.MaxY / TileSize; ty++)
            for (int tx = b.MinX / TileSize; tx <= b.MaxX / TileSize; tx++)
                _eyesByTile[tx + ty * _tilesX]!.Remove(eye);
        for (int y = b.MinY; y <= b.MaxY; y++)
            for (int x = b.MinX; x <= b.MaxX; x++)
            {
                byte was = eye.LevelAt(x, y);
                if (was != 0)
                    Replace(x, y, was, now: 0, pixels);
            }

        ReturnLevels(eye.Levels);
        eye.Levels = [];
        eye.RayLevels = null;
        _spareEyes.Push(eye);
    }

    /// <summary>
    /// Brings an eye whose ground changed up to date: all of it if it sees along open lines, and only the window
    /// around the changed cells if it sees through the ground.
    /// </summary>
    private void Recast(VisionEye eye, ReadOnlySpan<TerrainPixel> pixels)
    {
        Bounds b = BoundsOf(eye);
        if (eye.Kind == SightKind.SeeThrough)
        {
            // A changed cell reaches the walls within the skin of the open cells beside it.
            const int reach = Tweaks.Vision.WallSkin + 1;
            b = new Bounds(
                Math.Max(b.MinX, eye.StaleMinX - reach), Math.Max(b.MinY, eye.StaleMinY - reach),
                Math.Min(b.MaxX, eye.StaleMaxX + reach), Math.Min(b.MaxY, eye.StaleMaxY + reach));
        }
        eye.ClearStale();

        int width = b.MaxX - b.MinX + 1;
        int count = width * (b.MaxY - b.MinY + 1);
        CellsCastInLastUpdate += count;
        if (_before.Length < count)
            _before = new byte[count];
        for (int y = b.MinY; y <= b.MaxY; y++)
            for (int x = b.MinX; x <= b.MaxX; x++)
                _before[(x - b.MinX) + (y - b.MinY) * width] = eye.LevelAt(x, y);

        if (eye.Kind == SightKind.SeeThrough)
            VisionCast.CastWindow(eye, pixels, _width, _height, b.MinX, b.MinY, b.MaxX, b.MaxY);
        else
            VisionCast.Cast(eye, pixels, _width, _height);

        for (int y = b.MinY; y <= b.MaxY; y++)
            for (int x = b.MinX; x <= b.MaxX; x++)
                Replace(x, y, _before[(x - b.MinX) + (y - b.MinY) * width], eye.LevelAt(x, y), pixels);
    }

    /// <summary>
    /// One eye's level for a cell went from <paramref name="was"/> to <paramref name="now"/>. The cell's level is the
    /// highest any eye gives it, so the others are asked only when this eye held that highest level and lowered it.
    /// </summary>
    private void Replace(int x, int y, byte was, byte now, ReadOnlySpan<TerrainPixel> pixels)
    {
        int offset = x + y * _width;
        byte level = _level[offset];
        byte next;
        if (now >= level)
            next = now;
        else if (was < level)
            return;
        else
            next = HighestLevel(x, y);
        if (next == level)
            return;

        _level[offset] = next;
        MarkDirty(x, y);
        if (level == 0)
        {
            _explored[offset] = true;
            SyncKnown(x, y, pixels);
        }
    }

    private byte HighestLevel(int x, int y)
    {
        byte highest = 0;
        List<VisionEye>? near = _eyesByTile[x / TileSize + y / TileSize * _tilesX];
        if (near == null)
            return highest;
        foreach (VisionEye eye in near)
            if (eye.Covers(x, y))
                highest = Math.Max(highest, eye.LevelAt(x, y));
        return highest;
    }

    private void SyncKnown(int x, int y, ReadOnlySpan<TerrainPixel> pixels)
    {
        int offset = x + y * _width;
        TerrainPixel pixel = pixels[offset];
        if (_known.Pixels[offset] == pixel)
            return;
        _known.Pixels[offset] = pixel;
        _knownChanges.Add(new Position(x, y));
    }

    /// <summary>The part of an eye's square that lies on the map (empty, max below min, when none does).</summary>
    private Bounds BoundsOf(VisionEye eye) => new(
        Math.Max(0, eye.OriginX), Math.Max(0, eye.OriginY),
        Math.Min(_width - 1, eye.OriginX + eye.Side - 1), Math.Min(_height - 1, eye.OriginY + eye.Side - 1));

    private readonly record struct Bounds(int MinX, int MinY, int MaxX, int MaxY)
    {
        public bool IsEmpty => MaxX < MinX || MaxY < MinY;
    }

    private byte[] RentLevels(int length) =>
        _spareLevels.TryGetValue(length, out Stack<byte[]>? spare) && spare.Count > 0 ? spare.Pop() : new byte[length];

    private void ReturnLevels(byte[] levels)
    {
        if (!_spareLevels.TryGetValue(levels.Length, out Stack<byte[]>? spare))
            _spareLevels[levels.Length] = spare = new();
        spare.Push(levels);
    }

    private void MarkDirty(int x, int y) => _dirty.Include(x, y);

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
