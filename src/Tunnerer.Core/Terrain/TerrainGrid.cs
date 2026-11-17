namespace Tunnerer.Core.Terrain;

using System.Diagnostics;
using Tunnerer.Core.Thermal;
using Tunnerer.Core.Types;

public partial class TerrainGrid : ITerrainAccess, ITerrainChangeTracker
{
    private readonly TerrainPixel[] _data;
    private readonly byte[] _scorchIntensity;
    private readonly float[] _airTemperature;
    private readonly int[] _neighborOffsets;
    private readonly List<Position> _changeList = new();

    /// <summary>The tiles regrowth still has to look at; every pixel write keeps it up to date.</summary>
    public RegrowthTiles RegrowthTiles { get; }

    public Size Size { get; }
    public int Width => Size.X;
    public int Height => Size.Y;

    public TerrainGrid(Size size)
        : this(size, Config.SimulationSettings.FromTweaks())
    {
    }

    public TerrainGrid(Size size, Config.SimulationSettings simulationSettings)
    {
        _simulationSettings = simulationSettings;
        Size = size;
        _data = new TerrainPixel[size.Area];
        _scorchIntensity = new byte[size.Area];
        _heatTemperature = new float[size.Area];
        _airTemperature = new float[size.Area];
        _heatEngine = new TerrainHeatEngine(simulationSettings);
        _neighborOffsets = BuildNeighborOffsets(size.X);
        RegrowthTiles = new RegrowthTiles(size.X, size.Y);
        _thermalActivity = new ThermalActivityTracker(size.X, size.Y, Math.Max(8, simulationSettings.ThermalActiveTileSize));
        _computeTileDelta = ComputeTileDelta;
        _applyTileStep = ApplyTileStep;
        if (simulationSettings.ThermalMaxWorkers > 0)
            _tileThreads.MaxDegreeOfParallelism = simulationSettings.ThermalMaxWorkers;
    }

    public TerrainPixel this[int offset]
    {
        get => _data[offset];
        set => SetPixelRaw(offset, value);
    }

    public TerrainPixel this[int x, int y]
    {
        get => _data[x + y * Width];
        set => SetPixelRaw(x + y * Width, value);
    }

    public TerrainPixel GetPixel(Position pos)
    {
        if (!IsInside(pos)) return TerrainPixel.Rock;
        return _data[pos.X + pos.Y * Width];
    }

    public void SetPixelRaw(Position pos, TerrainPixel value) => SetPixelRaw(pos.X + pos.Y * Width, value);

    public void SetPixelRaw(int offset, TerrainPixel value)
    {
        _data[offset] = value;
        RegrowthTiles.NoteWrite(offset, value);
    }
    public TerrainPixel GetPixelRaw(Position pos) => _data[pos.X + pos.Y * Width];
    public TerrainPixel GetPixelRaw(int offset) => _data[offset];

    public const byte ScorchIntensityMax = 64;

    public void SetPixel(Position pos, TerrainPixel value)
    {
        int offset = pos.X + pos.Y * Width;
        TerrainPixel previous = _data[offset];
        RecordMaterialChangeEnergy(offset, previous, value);
        _data[offset] = value;
        RegrowthTiles.NoteWrite(offset, value);
        MirrorAirIfCellBecameAir(pos, offset, previous, value);
        WakeHeatIfMaterialChanged(pos, offset, previous, value);
        if (value == TerrainPixel.Decal)
            _scorchIntensity[offset] = ScorchIntensityMax;
        else if (!Pixel.IsScorched(value))
            _scorchIntensity[offset] = 0;
        CommitPixel(pos);
    }

    public byte GetScorchIntensity(int offset) => _scorchIntensity[offset];

    public void SetScorchIntensity(int offset, byte value) => _scorchIntensity[offset] = value;

    /// <summary>
    /// A cell that changes thermal material (dirt dug out to air, rock blasted, regrowth) keeps its
    /// terrain-field temperature, but only non-air cells store energy there, so energy appears or disappears
    /// with the change. The difference is recorded in <see cref="Ledger"/> so the balance stays auditable.
    /// </summary>
    private void RecordMaterialChangeEnergy(int offset, TerrainPixel oldPixel, TerrainPixel newPixel)
    {
        var oldMaterial = Pixel.GetThermalMaterial(oldPixel);
        var newMaterial = Pixel.GetThermalMaterial(newPixel);
        if (oldMaterial == newMaterial)
            return;

        // Only non-air cells store energy in the terrain field (an air cell's terrain field mirrors its air),
        // so the terrain-field energy that appears or disappears is T * (new stored capacity - old stored capacity).
        double stored(ThermalMaterial m) => m == ThermalMaterial.Air ? 0.0 : _heatEngine.GetHeatCapacity(m);
        Ledger.RecordInjected(_heatTemperature[offset] * (stored(newMaterial) - stored(oldMaterial)));
    }

    /// <summary>
    /// A solid cell that becomes air (dug out, blasted) stops storing energy in the terrain field: from then on that
    /// field only mirrors the cell's air. The solid's energy has just been written off in the ledger, so its old
    /// temperature must not stay behind in the mirror, where a sampler or a sink would read it as heat the air does
    /// not hold and pull the air below ambient to pay for it.
    /// </summary>
    private void MirrorAirIfCellBecameAir(Position pos, int offset, TerrainPixel oldPixel, TerrainPixel newPixel)
    {
        if (Pixel.GetThermalMaterial(newPixel) != ThermalMaterial.Air ||
            Pixel.GetThermalMaterial(oldPixel) == ThermalMaterial.Air)
            return;

        float stale = _heatTemperature[offset];
        float air = _airTemperature[offset];
        _heatTemperature[offset] = air;
        if (ShouldMarkHeatDirty(stale, air))
            MarkHeatDirty(pos.X, pos.Y);
    }

    /// <summary>
    /// A cell that changes thermal material conducts differently from then on, so heat that had settled around it
    /// starts to move again: its tile is stepped. Where the cell and its four neighbours are all at ambient, or within
    /// the trace that faded heat leaves behind, there is nothing to move, and digging or regrowth there wakes nothing.
    /// </summary>
    private void WakeHeatIfMaterialChanged(Position pos, int offset, TerrainPixel oldPixel, TerrainPixel newPixel)
    {
        if (Pixel.GetThermalMaterial(newPixel) == Pixel.GetThermalMaterial(oldPixel))
            return;
        if (HoldsHeat(offset) ||
            (pos.X > 0 && HoldsHeat(offset - 1)) || (pos.X < Width - 1 && HoldsHeat(offset + 1)) ||
            (pos.Y > 0 && HoldsHeat(offset - Width)) || (pos.Y < Height - 1 && HoldsHeat(offset + Width)))
            ActivateTileByCell(pos.X, pos.Y);
    }

    private bool HoldsHeat(int offset)
    {
        float ambient = _simulationSettings.ThermalAmbientTemperature;
        float wake = _simulationSettings.ThermalWakeTemperature;
        return MathF.Abs(_heatTemperature[offset] - ambient) > wake || MathF.Abs(_airTemperature[offset] - ambient) > wake;
    }

    public void CommitPixel(Position pos) => _changeList.Add(pos);

    public bool IsInside(Position pos) => Size.FitsInside(pos);

    public int CountDirtNeighbors(Position pos)
    {
        AssertHasNeighborMargin(pos.X, pos.Y);
        int offset = pos.X + pos.Y * Width;
        int count = 0;
        for (int i = 0; i < 8; i++)
            if (Pixel.IsDirt(_data[offset + _neighborOffsets[i]])) count++;
        return count;
    }

    /// <summary>
    /// Sums the byte values of 8 neighbors. During level generation, LevelGenDirt=0 and
    /// LevelGenRock=1, so the result equals the number of rock neighbors (0..8).
    /// </summary>
    public int CountLevelGenNeighbors(Position pos)
    {
        AssertHasNeighborMargin(pos.X, pos.Y);
        int offset = pos.X + pos.Y * Width;
        int sum = 0;
        for (int i = 0; i < 8; i++)
            sum += (byte)_data[offset + _neighborOffsets[i]];
        return sum;
    }

    public bool HasLevelGenNeighbor(int x, int y)
    {
        AssertHasNeighborMargin(x, y);
        int offset = x + y * Width;
        for (int i = 0; i < 8; i++)
            if (_data[offset + _neighborOffsets[i]] == TerrainPixel.LevelGenDirt) return true;
        return false;
    }

    [Conditional("DEBUG")]
    private void AssertHasNeighborMargin(int x, int y)
    {
        Debug.Assert(
            x > 0 && x < Width - 1 && y > 0 && y < Height - 1,
            "Neighbor queries require an interior position (not on map boundary).");
    }

    private static int[] BuildNeighborOffsets(int w)
    {
        var offsets = new int[8];
        int i = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                offsets[i++] = dx + dy * w;
            }
        return offsets;
    }

    public IReadOnlyList<Position> GetChangeList() => _changeList;

    public void ClearChangeList() => _changeList.Clear();

    public void Fill(TerrainPixel value)
    {
        Array.Fill(_data, value);
        RegrowthTiles.MarkAll();
    }

    public ReadOnlySpan<TerrainPixel> Data => _data;
}
