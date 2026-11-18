namespace Tunnerer.Core.Terrain;

using Tunnerer.Core.Snapshots;
using System.Diagnostics;
using Tunnerer.Core.Thermal;

public partial class TerrainGrid
{
    /// <summary>What applying one tile's step found; merged tile by tile, in order, so threads cannot reorder a sum.</summary>
    private struct TileStepResult
    {
        public double SunkFixed;
        public double SunkLoss;
        public int DirtyMinX, DirtyMinY, DirtyMaxX, DirtyMaxY;
        public bool Dirty;
    }

    private readonly ThermalActivityTracker _thermalActivity;
    [NotSaved("scratch: a heat step writes each tile's deltas before it reads them")]
    private readonly List<float[]> _tileDelta = new();
    [NotSaved("scratch: a heat step writes each tile's deltas before it reads them")]
    private readonly List<float[]> _tileAirDelta = new();
    [NotSaved("fixed: a delegate to this grid's own method")]
    private readonly Action<int> _computeTileDelta;
    [NotSaved("fixed: a delegate to this grid's own method")]
    private readonly Action<int> _applyTileStep;
    [NotSaved("fixed for the match: the worker count, from the settings")]
    private readonly ParallelOptions _tileThreads = new();
    private List<int> _steppedTiles = new();
    private TileStepResult[] _tileResults = [];
    private bool[] _tileStillChanging = [];

    private void ActivateTileByCell(int x, int y) => _thermalActivity.Activate(x, y);

    /// <summary>The size of the tiles the heat solver works in, and how many there are each way.</summary>
    public void GetThermalTileInfo(out int tileSize, out int tileCountX, out int tileCountY)
    {
        tileSize = _thermalActivity.TileSize;
        tileCountX = _thermalActivity.TileCountX;
        tileCountY = _thermalActivity.TileCountY;
    }

    /// <summary>
    /// One solver step over the active tiles and the ring around them. Every tile's deltas are computed from the same
    /// state, then every tile is applied; both passes run a tile per thread when there are enough tiles. Tiles trade
    /// heat across the sides they share, so the stepped area behaves as one piece, and what each tile found is merged
    /// in tile order, so the result does not depend on the threads.
    /// </summary>
    private void StepActiveTiles(out TimeSpan simulate, out TimeSpan apply, out int steppedTiles, out bool parallel)
    {
        long t0 = Stopwatch.GetTimestamp();
        _steppedTiles = _thermalActivity.BuildSteppedTiles();
        ExtendSteppedTilesAcrossLiveSides();
        int count = steppedTiles = _steppedTiles.Count;
        EnsureTileBuffers(count);
        bool onThreads = parallel = count >= _simulationSettings.ThermalParallelTileThreshold;

        RunPerTile(_computeTileDelta);
        simulate = Stopwatch.GetElapsedTime(t0);

        t0 = Stopwatch.GetTimestamp();
        RunPerTile(_applyTileStep);
        double sunkFixed = 0.0, sunkLoss = 0.0;
        for (int slot = 0; slot < count; slot++)
        {
            ref TileStepResult result = ref _tileResults[slot];
            sunkFixed += result.SunkFixed;
            sunkLoss += result.SunkLoss;
            if (result.Dirty)
            {
                _thermalActivity.MarkDirty(result.DirtyMinX, result.DirtyMinY);
                _thermalActivity.MarkDirty(result.DirtyMaxX, result.DirtyMaxY);
            }
        }
        Ledger.RecordSunkFixedTemperature(sunkFixed);
        Ledger.RecordSunkAmbientLoss(sunkLoss);
        _thermalActivity.CommitStep(_steppedTiles, _tileStillChanging.AsSpan(0, count));
        apply = Stopwatch.GetElapsedTime(t0);

        void RunPerTile(Action<int> perTile)
        {
            if (onThreads)
            {
                Parallel.For(0, count, _tileThreads, perTile);
                return;
            }
            for (int slot = 0; slot < count; slot++)
                perTile(slot);
        }
    }

    /// <summary>
    /// A stepped tile's side toward a tile that is left out stays closed, which is only right if nothing crosses it.
    /// Settled heat does cross: a pocket keeps feeding its halo, and closing a side between them would cut that for
    /// as long as one is stepped without the other. So wherever a stepped tile trades heat across a side, the tile
    /// on the other side is stepped too, and so on outward until every remaining closed side is quiet.
    /// </summary>
    private void ExtendSteppedTilesAcrossLiveSides()
    {
        float step = _simulationSettings.ThermalSettledFadeStep;
        bool extended = false;
        // Tiles added here go on the end of the list and are looked at in turn.
        for (int i = 0; i < _steppedTiles.Count; i++)
        {
            int tile = _steppedTiles[i];
            _thermalActivity.SidesOf(tile, out _, out RegionEdges unstepped);
            if (unstepped == RegionEdges.None)
                continue;
            HeatRegion r = _thermalActivity.TileRegion(tile);
            foreach (RegionEdges side in OneSideAtATime)
            {
                if ((unstepped & side) == 0 ||
                    !_heatEngine.EdgeCarriesHeat(_heatTemperature, _airTemperature, _data, Width,
                        r.MinX, r.MinY, r.MaxXInclusive, r.MaxYInclusive, side, step))
                    continue;
                _thermalActivity.AddSteppedNeighbour(tile, side);
                extended = true;
            }
        }
        if (extended)
            _thermalActivity.SortSteppedTiles();
    }

    private static readonly RegionEdges[] OneSideAtATime =
        [RegionEdges.Left, RegionEdges.Up, RegionEdges.Right, RegionEdges.Down];

    private void EnsureTileBuffers(int count)
    {
        int area = _thermalActivity.TileSize * _thermalActivity.TileSize;
        // Give the buffers back once a burst of activity is well over.
        if (_tileDelta.Count > 4 * count + 256)
        {
            int keep = 2 * count + 64;
            _tileDelta.RemoveRange(keep, _tileDelta.Count - keep);
            _tileAirDelta.RemoveRange(keep, _tileAirDelta.Count - keep);
        }
        while (_tileDelta.Count < count)
        {
            _tileDelta.Add(new float[area]);
            _tileAirDelta.Add(new float[area]);
        }
        if (_tileResults.Length < count)
        {
            int capacity = Math.Max(count, _tileResults.Length * 2);
            _tileResults = new TileStepResult[capacity];
            _tileStillChanging = new bool[capacity];
        }
    }

    private void ComputeTileDelta(int slot)
    {
        int tile = _steppedTiles[slot];
        HeatRegion r = _thermalActivity.TileRegion(tile);
        _thermalActivity.SidesOf(tile, out RegionEdges stepped, out _);
        _heatEngine.ComputeRegionDeltaConservative(
            _heatTemperature, _airTemperature, _data, Width, Height,
            r.MinX, r.MinY, r.MaxXInclusive, r.MaxYInclusive,
            _tileDelta[slot], _tileAirDelta[slot], stepped);
    }

    /// <summary>
    /// Applies one tile's step deltas (through <see cref="TerrainHeatEngine.ResolveCell"/>, the only apply point)
    /// and notes what changed: the cells to redraw, and whether the tile is still changing and so stays active.
    /// </summary>
    private void ApplyTileStep(int slot)
    {
        HeatRegion region = _thermalActivity.TileRegion(_steppedTiles[slot]);
        float[] delta = _tileDelta[slot], airDelta = _tileAirDelta[slot];
        float ambient = _simulationSettings.ThermalAmbientTemperature;
        float settledGrow = _simulationSettings.ThermalSettledGrowStep;
        float settledFade = _simulationSettings.ThermalSettledFadeStep;
        var result = new TileStepResult
        {
            DirtyMinX = int.MaxValue,
            DirtyMinY = int.MaxValue,
            DirtyMaxX = int.MinValue,
            DirtyMaxY = int.MinValue,
        };
        bool stillChanging = false;
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
                float next = _heatEngine.ResolveCell(
                    Pixel.GetThermalMaterial(_data[offset]), oldTerrain + delta[idx], ref nextAir,
                    ref result.SunkFixed, ref result.SunkLoss);
                if (next == oldTerrain && nextAir == oldAir)
                    continue;
                _heatTemperature[offset] = next;
                _airTemperature[offset] = nextAir;
                if (ShouldMarkHeatDirty(oldTerrain, next))
                {
                    result.Dirty = true;
                    if (x < result.DirtyMinX) result.DirtyMinX = x;
                    if (x > result.DirtyMaxX) result.DirtyMaxX = x;
                    if (y < result.DirtyMinY) result.DirtyMinY = y;
                    if (y > result.DirtyMaxY) result.DirtyMaxY = y;
                }
                stillChanging = stillChanging ||
                    IsStillChanging(oldTerrain, next, ambient, settledGrow, settledFade) ||
                    IsStillChanging(oldAir, nextAir, ambient, settledGrow, settledFade);
            }
        }
        _tileResults[slot] = result;
        _tileStillChanging[slot] = stillChanging;
    }

    /// <summary>
    /// Whether a step moved a temperature enough to keep its tile active. A value moving away from ambient (the edge
    /// of a halo still spreading) has to move by the grow step; a value coming back to ambient (heat fading out) only
    /// by the much smaller fade step, so that fading heat is followed nearly all the way down before its tile sleeps.
    /// </summary>
    private static bool IsStillChanging(float old, float next, float ambient, float settledGrow, float settledFade)
    {
        float change = MathF.Abs(next - old);
        bool fading = MathF.Abs(next - ambient) < MathF.Abs(old - ambient);
        return change >= (fading ? settledFade : settledGrow);
    }
}
