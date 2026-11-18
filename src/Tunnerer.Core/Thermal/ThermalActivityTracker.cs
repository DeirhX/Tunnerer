namespace Tunnerer.Core.Thermal;

using Tunnerer.Core.Types;

/// <summary>A rectangle of cells that the heat solver steps as one block: one tile of the activity grid.</summary>
internal readonly record struct HeatRegion(int MinX, int MinY, int MaxXInclusive, int MaxYInclusive);

/// <summary>
/// Where terrain heat is changing. A tile is active when the last step still changed a cell in it, or when something
/// outside the solver wrote heat into it or changed what a cell is made of. The solver steps the active tiles and the
/// ring of tiles around them (a step writes the next set, then commits it); every other tile is in equilibrium and
/// costs nothing until it is disturbed. The dirty rect tells the renderer which cells changed visibly since it last
/// looked.
/// <para>
/// The active and the stepped tiles are kept as lists beside their flags, so every operation here costs the number
/// of tiles involved, never the number of tiles on the map.
/// </para>
/// </summary>
internal sealed class ThermalActivityTracker
{
    private readonly bool[] _active;
    private readonly bool[] _stepped;
    // Counts, per tile, the times it became active or was stepped: unchanged means its heat has not been touched.
    private readonly int[] _disturbances;
    private readonly List<int> _activeTiles = new();
    private readonly List<int> _steppedTiles = new();
    private readonly int _width;
    private readonly int _height;
    private readonly int _tileSize;
    private readonly int _tileCountX;
    private readonly int _tileCountY;

    private DirtyRect _dirty;

    public ThermalActivityTracker(int width, int height, int tileSize)
    {
        _width = width;
        _height = height;
        _tileSize = tileSize;
        _tileCountX = (width + tileSize - 1) / tileSize;
        _tileCountY = (height + tileSize - 1) / tileSize;
        TileCount = _tileCountX * _tileCountY;
        _active = new bool[TileCount];
        _stepped = new bool[TileCount];
        _disturbances = new int[TileCount];
    }

    public int TileCount { get; }
    public int TileSize => _tileSize;
    public int TileCountX => _tileCountX;
    public int TileCountY => _tileCountY;

    /// <summary>How many tiles are active for the coming step.</summary>
    public int ActiveTileCount => _activeTiles.Count;

    /// <summary>Marks the tile of a cell active for the coming step.</summary>
    public void Activate(int x, int y)
    {
        int tx = x / _tileSize;
        int ty = y / _tileSize;
        if ((uint)tx >= (uint)_tileCountX || (uint)ty >= (uint)_tileCountY)
            return;
        ActivateTile(tx + ty * _tileCountX);
    }

    private void ActivateTile(int tile)
    {
        if (_active[tile])
            return;
        _active[tile] = true;
        _disturbances[tile]++;
        _activeTiles.Add(tile);
    }

    /// <summary>
    /// A number that stays the same for as long as no heat in the given cells can have changed: none of their tiles
    /// has been written to or stepped. Negative while one of the tiles is active, when no such promise can be made.
    /// Whoever found nothing to do in an area can keep the number and skip looking again until it differs.
    /// </summary>
    public long QuietStamp(int minX, int minY, int maxX, int maxY)
    {
        long stamp = 0;
        for (int ty = minY / _tileSize; ty <= maxY / _tileSize; ty++)
            for (int tx = minX / _tileSize; tx <= maxX / _tileSize; tx++)
            {
                int tile = tx + ty * _tileCountX;
                if (_active[tile])
                    return -1;
                stamp += _disturbances[tile];
            }
        return stamp;
    }

    /// <summary>
    /// Replaces the active set with the tiles of <paramref name="steppedTiles"/> (the list the last
    /// <see cref="BuildSteppedTiles"/> returned) whose entry in <paramref name="stillChanging"/> is set.
    /// </summary>
    public void CommitStep(List<int> steppedTiles, ReadOnlySpan<bool> stillChanging)
    {
        foreach (int tile in _activeTiles)
            _active[tile] = false;
        _activeTiles.Clear();
        for (int i = 0; i < steppedTiles.Count; i++)
            if (stillChanging[i])
                ActivateTile(steppedTiles[i]);
    }

    /// <summary>
    /// The tiles to step: every active tile and the eight tiles around it, in ascending order. Heat that reaches the
    /// ring changes it and makes it active in turn, so a front is always one tile ahead of what is stepped. The list
    /// is reused: it is valid until the next call. <see cref="AddSteppedNeighbour"/> can extend it.
    /// </summary>
    public List<int> BuildSteppedTiles()
    {
        foreach (int tile in _steppedTiles)
            _stepped[tile] = false;
        _steppedTiles.Clear();

        foreach (int tile in _activeTiles)
        {
            int tx = tile % _tileCountX, ty = tile / _tileCountX;
            int minTx = Math.Max(0, tx - 1), maxTx = Math.Min(_tileCountX - 1, tx + 1);
            int minTy = Math.Max(0, ty - 1), maxTy = Math.Min(_tileCountY - 1, ty + 1);
            for (int ny = minTy; ny <= maxTy; ny++)
                for (int nx = minTx; nx <= maxTx; nx++)
                    AddStepped(nx + ny * _tileCountX);
        }

        // Ascending, whatever order the tiles became active in: the step merges what tiles found in list order.
        _steppedTiles.Sort();
        return _steppedTiles;
    }

    private void AddStepped(int tile)
    {
        if (_stepped[tile])
            return;
        _stepped[tile] = true;
        // A step can change a ring tile by less than what makes it active.
        _disturbances[tile]++;
        _steppedTiles.Add(tile);
    }

    /// <summary>
    /// Adds the tile across <paramref name="side"/> of <paramref name="tile"/> to the stepped tiles, at the end of
    /// the list. Call <see cref="SortSteppedTiles"/> once the list is complete.
    /// </summary>
    public void AddSteppedNeighbour(int tile, RegionEdges side) => AddStepped(side switch
    {
        RegionEdges.Left => tile - 1,
        RegionEdges.Right => tile + 1,
        RegionEdges.Up => tile - _tileCountX,
        RegionEdges.Down => tile + _tileCountX,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "one side at a time"),
    });

    public void SortSteppedTiles() => _steppedTiles.Sort();

    /// <summary>The cells of a tile.</summary>
    public HeatRegion TileRegion(int tile)
    {
        int tx = tile % _tileCountX;
        int ty = tile / _tileCountX;
        return new HeatRegion(
            tx * _tileSize,
            ty * _tileSize,
            Math.Min(_width - 1, (tx + 1) * _tileSize - 1),
            Math.Min(_height - 1, (ty + 1) * _tileSize - 1));
    }

    /// <summary>
    /// The sides of a tile that touch another stepped tile (<paramref name="stepped"/>) and the sides that touch a
    /// tile left out of the step (<paramref name="unstepped"/>); a side on the edge of the map is in neither.
    /// </summary>
    public void SidesOf(int tile, out RegionEdges stepped, out RegionEdges unstepped)
    {
        int tx = tile % _tileCountX;
        int ty = tile / _tileCountX;
        stepped = unstepped = RegionEdges.None;
        if (tx > 0) Note(_stepped[tile - 1], RegionEdges.Left, ref stepped, ref unstepped);
        if (tx < _tileCountX - 1) Note(_stepped[tile + 1], RegionEdges.Right, ref stepped, ref unstepped);
        if (ty > 0) Note(_stepped[tile - _tileCountX], RegionEdges.Up, ref stepped, ref unstepped);
        if (ty < _tileCountY - 1) Note(_stepped[tile + _tileCountX], RegionEdges.Down, ref stepped, ref unstepped);

        static void Note(bool isStepped, RegionEdges side, ref RegionEdges stepped, ref RegionEdges unstepped)
        {
            if (isStepped)
                stepped |= side;
            else
                unstepped |= side;
        }
    }

    /// <summary>Grows the dirty rect to include the cell.</summary>
    public void MarkDirty(int x, int y) => _dirty.Include(x, y);

    public bool TryGetDirtyRect(out Rect dirtyRect) => _dirty.TryGet(out dirtyRect);

    public void ClearDirtyRect() => _dirty.Clear();

    /// <summary>
    /// Mixes which tiles are active (they decide which cells the next step changes) and every tile's disturbance
    /// count (relays compare stamps made from it), for the sync digest. Costs the number of tiles, not of cells.
    /// </summary>
    internal ulong MixLatent(ulong hash)
    {
        hash = Fnv1a.Mix(hash, _activeTiles.Count);
        foreach (int tile in _activeTiles)
            hash = Fnv1a.Mix(hash, tile);
        foreach (int disturbances in _disturbances)
            hash = Fnv1a.Mix(hash, disturbances);
        return hash;
    }
}
