namespace Tunnerer.Core.Thermal;

using Tunnerer.Core.Types;

/// <summary>A rectangle of cells that the heat solver steps as one block; regions of one step never overlap.</summary>
internal readonly record struct HeatRegion(int MinX, int MinY, int MaxXInclusive, int MaxYInclusive)
{
    public int Width => MaxXInclusive - MinX + 1;
    public int Height => MaxYInclusive - MinY + 1;

    public bool Intersects(in HeatRegion other)
        => MinX <= other.MaxXInclusive && other.MinX <= MaxXInclusive &&
           MinY <= other.MaxYInclusive && other.MinY <= MaxYInclusive;
}

/// <summary>
/// Where terrain heat is changing. Active tiles tell the solver which regions to step next tick (a step writes the
/// next set, then commits it); the dirty rect tells the renderer which cells changed visibly since it last looked.
/// Tiles do not exist until <see cref="EnsureTiles"/> is first called, and activations before that are dropped.
/// </summary>
internal sealed class ThermalActivityTracker
{
    private bool[]? _activeTiles;
    private bool[]? _nextActiveTiles;
    private int _width;
    private int _height;
    private int _tileSize;
    private int _tileCountX;
    private int _tileCountY;

    private bool _hasDirtyRect;
    private int _dirtyMinX;
    private int _dirtyMinY;
    private int _dirtyMaxX;
    private int _dirtyMaxY;

    public int TileCount { get; private set; }

    public void EnsureTiles(int width, int height, int tileSize)
    {
        int tcx = (width + tileSize - 1) / tileSize;
        int tcy = (height + tileSize - 1) / tileSize;
        int count = tcx * tcy;
        if (_activeTiles != null && _tileSize == tileSize && TileCount == count)
            return;

        _width = width;
        _height = height;
        _tileSize = tileSize;
        _tileCountX = tcx;
        _tileCountY = tcy;
        TileCount = count;
        _activeTiles = new bool[count];
        _nextActiveTiles = new bool[count];
    }

    public int CountActiveTiles()
    {
        if (_activeTiles == null)
            return 0;
        int count = 0;
        for (int i = 0; i < _activeTiles.Length; i++)
            if (_activeTiles[i]) count++;
        return count;
    }

    /// <summary>Marks the tile of a cell active for the coming step.</summary>
    public void Activate(int x, int y) => SetTile(_activeTiles, x, y);

    /// <summary>Marks the tile of a cell active in the set being built by the current step.</summary>
    public void ActivateNext(int x, int y) => SetTile(_nextActiveTiles, x, y);

    private void SetTile(bool[]? tiles, int x, int y)
    {
        if (tiles == null)
            return;
        int tx = x / _tileSize;
        int ty = y / _tileSize;
        if ((uint)tx >= (uint)_tileCountX || (uint)ty >= (uint)_tileCountY)
            return;
        tiles[tx + ty * _tileCountX] = true;
    }

    /// <summary>Starts building the next active set from scratch.</summary>
    public void BeginNextPass()
    {
        if (_nextActiveTiles != null)
            Array.Clear(_nextActiveTiles, 0, _nextActiveTiles.Length);
    }

    /// <summary>Makes the set built since <see cref="BeginNextPass"/> the active one.</summary>
    public void CommitNextPass()
    {
        if (_activeTiles == null || _nextActiveTiles == null)
            return;
        (_activeTiles, _nextActiveTiles) = (_nextActiveTiles, _activeTiles);
        Array.Clear(_nextActiveTiles, 0, _nextActiveTiles.Length);
    }

    public void ForgetActivity()
    {
        if (_activeTiles != null)
            Array.Clear(_activeTiles, 0, _activeTiles.Length);
        if (_nextActiveTiles != null)
            Array.Clear(_nextActiveTiles, 0, _nextActiveTiles.Length);
    }

    public bool TryGetTileInfo(out int tileSize, out int tileCountX, out int tileCountY)
    {
        if (_activeTiles == null || _tileCountX <= 0 || _tileCountY <= 0)
        {
            tileSize = 0;
            tileCountX = 0;
            tileCountY = 0;
            return false;
        }

        tileSize = _tileSize;
        tileCountX = _tileCountX;
        tileCountY = _tileCountY;
        return true;
    }

    /// <summary>
    /// One region per connected group of active tiles, padded by a tile on every side and merged until no two
    /// regions overlap.
    /// </summary>
    public List<HeatRegion> BuildActiveRegions()
    {
        var regions = new List<HeatRegion>();
        if (_activeTiles == null)
            return regions;

        bool[] active = _activeTiles;
        bool[] visited = new bool[active.Length];
        var queue = new Queue<int>(64);
        for (int tile = 0; tile < active.Length; tile++)
        {
            if (!active[tile] || visited[tile])
                continue;
            visited[tile] = true;
            queue.Enqueue(tile);
            int minTx = int.MaxValue, minTy = int.MaxValue, maxTx = int.MinValue, maxTy = int.MinValue;

            while (queue.Count > 0)
            {
                int t = queue.Dequeue();
                int tx = t % _tileCountX;
                int ty = t / _tileCountX;
                if (tx < minTx) minTx = tx;
                if (ty < minTy) minTy = ty;
                if (tx > maxTx) maxTx = tx;
                if (ty > maxTy) maxTy = ty;

                TryEnqueue(tx - 1, ty);
                TryEnqueue(tx + 1, ty);
                TryEnqueue(tx, ty - 1);
                TryEnqueue(tx, ty + 1);
            }

            int padMinTx = Math.Max(0, minTx - 1);
            int padMinTy = Math.Max(0, minTy - 1);
            int padMaxTx = Math.Min(_tileCountX - 1, maxTx + 1);
            int padMaxTy = Math.Min(_tileCountY - 1, maxTy + 1);
            int minX = padMinTx * _tileSize;
            int minY = padMinTy * _tileSize;
            int maxX = Math.Min(_width - 1, (padMaxTx + 1) * _tileSize - 1);
            int maxY = Math.Min(_height - 1, (padMaxTy + 1) * _tileSize - 1);
            regions.Add(new HeatRegion(minX, minY, maxX, maxY));

            void TryEnqueue(int nx, int ny)
            {
                if ((uint)nx >= (uint)_tileCountX || (uint)ny >= (uint)_tileCountY)
                    return;
                int ni = nx + ny * _tileCountX;
                if (visited[ni] || !active[ni])
                    return;
                visited[ni] = true;
                queue.Enqueue(ni);
            }
        }

        MergeOverlappingRegions(regions);
        return regions;
    }

    private static void MergeOverlappingRegions(List<HeatRegion> regions)
    {
        bool merged;
        do
        {
            merged = false;
            for (int i = 0; i < regions.Count && !merged; i++)
            {
                for (int j = i + 1; j < regions.Count; j++)
                {
                    if (!regions[i].Intersects(regions[j]))
                        continue;
                    var a = regions[i];
                    var b = regions[j];
                    regions[i] = new HeatRegion(
                        Math.Min(a.MinX, b.MinX),
                        Math.Min(a.MinY, b.MinY),
                        Math.Max(a.MaxXInclusive, b.MaxXInclusive),
                        Math.Max(a.MaxYInclusive, b.MaxYInclusive));
                    regions.RemoveAt(j);
                    merged = true;
                    break;
                }
            }
        } while (merged);
    }

    /// <summary>Grows the dirty rect to include the cell.</summary>
    public void MarkDirty(int x, int y)
    {
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

    public bool TryGetDirtyRect(out Rect dirtyRect)
    {
        if (_hasDirtyRect)
        {
            dirtyRect = new Rect(
                _dirtyMinX,
                _dirtyMinY,
                _dirtyMaxX - _dirtyMinX + 1,
                _dirtyMaxY - _dirtyMinY + 1);
            return true;
        }

        dirtyRect = default;
        return false;
    }

    public void ClearDirtyRect() => _hasDirtyRect = false;
}
