namespace Tunnerer.Core.Terrain;

using System.Diagnostics;
using Tunnerer.Core.Config;
using Tunnerer.Core.Thermal;
using Tunnerer.Core.Types;

public partial class TerrainGrid
{
    /// <summary>A rectangle of cells that the solver steps as one block; regions of one step never overlap.</summary>
    private readonly record struct HeatRegion(int MinX, int MinY, int MaxXInclusive, int MaxYInclusive)
    {
        public int Width => MaxXInclusive - MinX + 1;
        public int Height => MaxYInclusive - MinY + 1;
    }

    private HeatRegion[]? _fullGridRegion;
    private readonly List<float[]> _regionDelta = new();
    private readonly List<float[]> _regionAirDelta = new();

    private bool[]? _activeTiles;
    private bool[]? _nextActiveTiles;
    private int _tileSize;
    private int _tileCountX;
    private int _tileCountY;
    private int _tileCount;

    private void EnsureThermalTiles()
    {
        int size = Math.Max(8, _simulationSettings.ThermalActiveTileSize);
        int tcx = (Width + size - 1) / size;
        int tcy = (Height + size - 1) / size;
        int count = tcx * tcy;
        if (_activeTiles != null && _tileSize == size && _tileCount == count)
            return;

        _tileSize = size;
        _tileCountX = tcx;
        _tileCountY = tcy;
        _tileCount = count;
        _activeTiles = new bool[count];
        _nextActiveTiles = new bool[count];
    }

    private int CountActiveTiles()
    {
        if (_activeTiles == null)
            return 0;
        int count = 0;
        for (int i = 0; i < _activeTiles.Length; i++)
            if (_activeTiles[i]) count++;
        return count;
    }

    private void ActivateTileByCell(int x, int y)
    {
        if (_activeTiles == null)
            return;
        int tx = x / _tileSize;
        int ty = y / _tileSize;
        if ((uint)tx >= (uint)_tileCountX || (uint)ty >= (uint)_tileCountY)
            return;
        _activeTiles[tx + ty * _tileCountX] = true;
    }

    private void ActivateNextTileByCell(int x, int y)
    {
        if (_nextActiveTiles == null)
            return;
        int tx = x / _tileSize;
        int ty = y / _tileSize;
        if ((uint)tx >= (uint)_tileCountX || (uint)ty >= (uint)_tileCountY)
            return;
        _nextActiveTiles[tx + ty * _tileCountX] = true;
    }

    private void ClearNextActiveTiles()
    {
        if (_nextActiveTiles != null)
            Array.Clear(_nextActiveTiles, 0, _nextActiveTiles.Length);
    }

    private void SwapActiveTiles()
    {
        if (_activeTiles == null || _nextActiveTiles == null)
            return;
        var tmp = _activeTiles;
        _activeTiles = _nextActiveTiles;
        _nextActiveTiles = tmp;
        Array.Clear(_nextActiveTiles, 0, _nextActiveTiles.Length);
    }

    private List<HeatRegion> BuildSparseRegions()
    {
        var regions = new List<HeatRegion>();
        if (_activeTiles == null)
            return regions;

        bool[] visited = new bool[_activeTiles.Length];
        var queue = new Queue<int>(64);
        for (int tile = 0; tile < _activeTiles.Length; tile++)
        {
            if (!_activeTiles[tile] || visited[tile])
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
            int maxX = Math.Min(Width - 1, (padMaxTx + 1) * _tileSize - 1);
            int maxY = Math.Min(Height - 1, (padMaxTy + 1) * _tileSize - 1);
            regions.Add(new HeatRegion(minX, minY, maxX, maxY));

            void TryEnqueue(int nx, int ny)
            {
                if ((uint)nx >= (uint)_tileCountX || (uint)ny >= (uint)_tileCountY)
                    return;
                int ni = nx + ny * _tileCountX;
                if (visited[ni] || !_activeTiles[ni])
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
                    if (!Intersects(regions[i], regions[j]))
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

    private static bool Intersects(in HeatRegion a, in HeatRegion b)
    {
        return a.MinX <= b.MaxXInclusive && b.MinX <= a.MaxXInclusive &&
               a.MinY <= b.MaxYInclusive && b.MinY <= a.MaxYInclusive;
    }

    private bool IsThermallyActive(float terrainTemperature, float airTemperature)
    {
        float threshold = _simulationSettings.ThermalActiveTemperatureThreshold;
        return MathF.Abs(terrainTemperature) >= threshold || MathF.Abs(airTemperature) >= threshold;
    }

    public bool TryGetThermalTileInfo(out int tileSize, out int tileCountX, out int tileCountY)
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

    private HeatRegion[] FullGridRegion()
        => _fullGridRegion ??= [new HeatRegion(0, 0, Width - 1, Height - 1)];

    /// <summary>
    /// One solver step over disjoint regions: every region's deltas are computed from the same state (in parallel if
    /// asked), then applied and marked region by region in order. Returns the largest temperature change.
    /// </summary>
    private float StepRegions(IReadOnlyList<HeatRegion> regions, bool parallel, out TimeSpan simulate, out TimeSpan markDirty)
    {
        long t0 = Stopwatch.GetTimestamp();
        EnsureRegionBuffers(regions);
        if (parallel)
        {
            var po = new ParallelOptions();
            if (_simulationSettings.ThermalMaxWorkers > 0)
                po.MaxDegreeOfParallelism = _simulationSettings.ThermalMaxWorkers;
            Parallel.For(0, regions.Count, po, ComputeRegionDelta);
        }
        else
        {
            for (int i = 0; i < regions.Count; i++)
                ComputeRegionDelta(i);
        }
        simulate = Stopwatch.GetElapsedTime(t0);

        t0 = Stopwatch.GetTimestamp();
        ClearNextActiveTiles();
        double sunkFixed = 0.0;
        float maxAbsDelta = 0f;
        for (int i = 0; i < regions.Count; i++)
            ApplyStepAndMark(regions[i], _regionDelta[i], _regionAirDelta[i], ref sunkFixed, ref maxAbsDelta);
        Ledger.RecordSunkFixedTemperature(sunkFixed);
        markDirty = Stopwatch.GetElapsedTime(t0);
        SwapActiveTiles();
        return maxAbsDelta;

        void ComputeRegionDelta(int i)
        {
            var r = regions[i];
            _heatEngine.ComputeRegionDeltaConservative(
                _heatTemperature, _airTemperature, _data, Width, Height,
                r.MinX, r.MinY, r.MaxXInclusive, r.MaxYInclusive,
                _regionDelta[i], _regionAirDelta[i]);
        }
    }

    private void EnsureRegionBuffers(IReadOnlyList<HeatRegion> regions)
    {
        for (int i = 0; i < regions.Count; i++)
        {
            int area = regions[i].Width * regions[i].Height;
            if (i == _regionDelta.Count)
            {
                _regionDelta.Add(new float[area]);
                _regionAirDelta.Add(new float[area]);
            }
            else if (_regionDelta[i].Length < area)
            {
                _regionDelta[i] = new float[area];
                _regionAirDelta[i] = new float[area];
            }
        }
    }

    /// <summary>
    /// Applies one region's step deltas (through <see cref="TerrainHeatEngine.ResolveTerrainTemperature"/>, the only
    /// apply point) and marks what changed: the largest change, the dirty rect, and the next active tiles.
    /// </summary>
    private void ApplyStepAndMark(in HeatRegion region, float[] delta, float[] airDelta, ref double sunkFixed, ref float maxAbsDelta)
    {
        int idx = 0;
        for (int y = region.MinY; y <= region.MaxYInclusive; y++)
        {
            int row = y * Width;
            for (int x = region.MinX; x <= region.MaxXInclusive; x++, idx++)
            {
                int offset = row + x;
                float oldTerrain = _heatTemperature[offset];
                float oldAir = _airTemperature[offset];
                float nextAir = oldAir + airDelta[idx];
                float next = _heatEngine.ResolveTerrainTemperature(
                    Pixel.GetThermalMaterial(_data[offset]), oldTerrain + delta[idx], nextAir, ref sunkFixed);
                _heatTemperature[offset] = next;
                _airTemperature[offset] = nextAir;
                float cellMaxDelta = MathF.Max(MathF.Abs(next - oldTerrain), MathF.Abs(nextAir - oldAir));
                if (cellMaxDelta > maxAbsDelta)
                    maxAbsDelta = cellMaxDelta;
                if (ShouldMarkHeatDirty(oldTerrain, next))
                    MarkHeatDirty(x, y);
                if (IsThermallyActive(next, nextAir))
                    ActivateNextTileByCell(x, y);
            }
        }
    }
}
