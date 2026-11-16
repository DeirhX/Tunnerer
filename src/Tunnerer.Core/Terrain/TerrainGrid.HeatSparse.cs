namespace Tunnerer.Core.Terrain;

using System.Diagnostics;
using Tunnerer.Core.Thermal;

public partial class TerrainGrid
{
    private readonly ThermalActivityTracker _thermalActivity = new();
    private HeatRegion[]? _fullGridRegion;
    private readonly List<float[]> _regionDelta = new();
    private readonly List<float[]> _regionAirDelta = new();

    private void ActivateTileByCell(int x, int y) => _thermalActivity.Activate(x, y);

    private bool IsThermallyActive(float terrainTemperature, float airTemperature)
    {
        float threshold = _simulationSettings.ThermalActiveTemperatureThreshold;
        return MathF.Abs(terrainTemperature) >= threshold || MathF.Abs(airTemperature) >= threshold;
    }

    public bool TryGetThermalTileInfo(out int tileSize, out int tileCountX, out int tileCountY)
        => _thermalActivity.TryGetTileInfo(out tileSize, out tileCountX, out tileCountY);

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
        _thermalActivity.BeginNextPass();
        double sunkFixed = 0.0;
        float maxAbsDelta = 0f;
        for (int i = 0; i < regions.Count; i++)
            ApplyStepAndMark(regions[i], _regionDelta[i], _regionAirDelta[i], ref sunkFixed, ref maxAbsDelta);
        Ledger.RecordSunkFixedTemperature(sunkFixed);
        markDirty = Stopwatch.GetElapsedTime(t0);
        _thermalActivity.CommitNextPass();
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
                    _thermalActivity.MarkDirty(x, y);
                if (IsThermallyActive(next, nextAir))
                    _thermalActivity.ActivateNext(x, y);
            }
        }
    }
}
