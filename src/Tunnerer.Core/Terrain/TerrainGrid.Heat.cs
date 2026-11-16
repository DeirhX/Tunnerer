namespace Tunnerer.Core.Terrain;

using System.Diagnostics;
using Tunnerer.Core.Config;
using Tunnerer.Core.Thermal;
using Tunnerer.Core.Types;

public partial class TerrainGrid
{
    public readonly struct CoolDownProfile
    {
        public CoolDownProfile(
            TimeSpan prep,
            TimeSpan simulate,
            TimeSpan markDirty,
            TimeSpan writeBack,
            int activeTiles,
            int regionCount,
            bool usedSparse,
            bool usedParallel)
        {
            Prep = prep;
            Simulate = simulate;
            MarkDirty = markDirty;
            WriteBack = writeBack;
            ActiveTiles = activeTiles;
            RegionCount = regionCount;
            UsedSparse = usedSparse;
            UsedParallel = usedParallel;
        }

        public TimeSpan Prep { get; }
        public TimeSpan Simulate { get; }
        public TimeSpan MarkDirty { get; }
        public TimeSpan WriteBack { get; }
        public int ActiveTiles { get; }
        public int RegionCount { get; }
        public bool UsedSparse { get; }
        public bool UsedParallel { get; }

        /// <summary>A tick on which the solver slept.</summary>
        public static CoolDownProfile Idle(int activeTiles)
            => new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, activeTiles, 0, false, false);
    }

    private readonly float[] _heatTemperature;
    private readonly TerrainHeatEngine _heatEngine;
    private readonly SimulationSettings _simulationSettings;
    private readonly ThermalScheduler _thermalScheduler = new();

    public CoolDownProfile LastCoolDownProfile { get; private set; }

    /// <summary>
    /// Energy that entered or left the thermal system from outside. See <see cref="ThermalLedger"/>.
    /// </summary>
    public ThermalLedger Ledger { get; } = new();

    public float GetHeatTemperature(int offset)
        => (uint)offset < (uint)_heatTemperature.Length ? _heatTemperature[offset] : 0f;

    public float GetAirTemperature(int offset)
        => (uint)offset < (uint)_airTemperature.Length ? _airTemperature[offset] : 0f;

    public float GetHeatTemperature(Position pos)
    {
        if ((uint)pos.X >= (uint)Width || (uint)pos.Y >= (uint)Height)
            return 0f;
        int offset = pos.X + pos.Y * Width;
        Debug.Assert((uint)offset < (uint)_heatTemperature.Length, "Validated position must map to a valid offset.");
        return _heatEngine.GetTemperatureAt(_heatTemperature, _heatTemperature.Length, offset);
    }

    public float GetAirTemperature(Position pos)
    {
        if ((uint)pos.X >= (uint)Width || (uint)pos.Y >= (uint)Height)
            return 0f;
        int offset = pos.X + pos.Y * Width;
        Debug.Assert((uint)offset < (uint)_airTemperature.Length, "Validated position must map to a valid offset.");
        return _heatEngine.GetTemperatureAt(_airTemperature, _airTemperature.Length, offset);
    }

    public void AddHeat(Position pos, int amount)
    {
        if ((uint)pos.X >= (uint)Width || (uint)pos.Y >= (uint)Height)
            return;
        int offset = pos.X + pos.Y * Width;
        Debug.Assert((uint)offset < (uint)_heatTemperature.Length, "Validated position must map to a valid offset.");
        float old = _heatTemperature[offset];
        AddToTerrainField(offset, amount);
        float next = _heatTemperature[offset];
        Ledger.RecordInjected((double)amount * TerrainCapacityAt(offset));
        if (ShouldMarkHeatDirty(old, next))
            MarkHeatDirty(pos.X, pos.Y);
        if (amount != 0)
        {
            ActivateTileByCell(pos.X, pos.Y);
            RequestThermalWake();
        }
        CommitPixel(pos);
    }

    public void AddHeatRadius(Position center, int amount, int radius)
    {
        Debug.Assert(radius >= 0, "Heat radius is expected to be non-negative.");
        if (radius <= 0)
        {
            AddHeat(center, amount);
            return;
        }

        int radiusSq = radius * radius;
        bool anyApplied = false;
        ForEachInRadius(center, radius, (nx, ny, dx, dy) =>
        {
            int dist2 = dx * dx + dy * dy;
            if (dist2 > radiusSq) return;
            float falloff = 1f - (float)dist2 / radiusSq;
            int scaled = (int)(amount * falloff);
            if (scaled == 0) return;
            int offset = nx + ny * Width;
            float old = _heatTemperature[offset];
            AddToTerrainField(offset, scaled);
            float next = _heatTemperature[offset];
            Ledger.RecordInjected((double)scaled * TerrainCapacityAt(offset));
            if (ShouldMarkHeatDirty(old, next))
                MarkHeatDirty(nx, ny);
            ActivateTileByCell(nx, ny);
            anyApplied = true;
        });
        if (anyApplied)
            RequestThermalWake();
    }

    /// <summary>
    /// One tick of terrain heat: ask the scheduler whether (and how widely) to step, run the full-grid or sparse
    /// solver, then the optional artificial cooling. Profiling timings go to <see cref="LastCoolDownProfile"/> only.
    /// </summary>
    public void CoolDown(float artificialCoolingPerFrame = 0f)
    {
        _thermalActivity.EnsureTiles(Width, Height, Math.Max(8, _simulationSettings.ThermalActiveTileSize));
        int activeTiles = _thermalActivity.CountActiveTiles();
        ThermalStep step = _thermalScheduler.Decide(
            _simulationSettings, activeTiles, _thermalActivity.TileCount, artificialCoolingPerFrame > 0f);

        if (step == ThermalStep.SleepAndForgetActivity)
        {
            _thermalActivity.ForgetActivity();
            activeTiles = 0;
        }
        if (step is ThermalStep.Sleep or ThermalStep.SleepAndForgetActivity)
        {
            LastCoolDownProfile = CoolDownProfile.Idle(activeTiles);
            return;
        }

        IReadOnlyList<HeatRegion> regions = step == ThermalStep.Full ? FullGridRegion() : _thermalActivity.BuildActiveRegions();
        bool parallel = step == ThermalStep.Sparse && regions.Count >= _simulationSettings.ThermalParallelRegionThreshold;
        float maxAbsDelta = StepRegions(regions, parallel, out TimeSpan simulate, out TimeSpan markDirty);

        if (artificialCoolingPerFrame > 0f)
            markDirty += ApplyArtificialCooling(artificialCoolingPerFrame, ref maxAbsDelta);

        _thermalScheduler.RecordStep(maxAbsDelta);
        bool sparse = step == ThermalStep.Sparse;
        LastCoolDownProfile = new CoolDownProfile(
            TimeSpan.Zero, simulate, markDirty, TimeSpan.Zero, activeTiles, sparse ? regions.Count : 0, sparse, parallel);
    }

    /// <summary>
    /// A dirt-only gameplay sink: stone, base and hot rock keep their energy, dirt below ambient keeps its cold, and
    /// the ledger meters the sunk energy with the dirt capacity. Rebuilds the active tiles; returns the time spent.
    /// </summary>
    private TimeSpan ApplyArtificialCooling(float coolingPerFrame, ref float maxAbsDelta)
    {
        int w = Width, len = Width * Height;
        float dirtCapacity = _heatEngine.GetHeatCapacity(ThermalMaterial.Dirt);
        long t0 = Stopwatch.GetTimestamp();
        _thermalActivity.BeginNextPass();
        for (int i = 0; i < len; i++)
        {
            if (Pixel.GetThermalMaterial(_data[i]) == ThermalMaterial.Dirt)
            {
                float old = _heatTemperature[i];
                // A sink only takes heat above ambient: sub-zero dirt (frost, or chilled by a cold tank) keeps its cold.
                float next = old > 0f ? MathF.Max(0f, old - coolingPerFrame) : old;
                if (next != old)
                {
                    _heatTemperature[i] = next;
                    Ledger.RecordSunkArtificialCooling((double)(old - next) * dirtCapacity);
                    float deltaTerrain = MathF.Abs(next - old);
                    if (deltaTerrain > maxAbsDelta)
                        maxAbsDelta = deltaTerrain;
                    if (ShouldMarkHeatDirty(old, next))
                        MarkHeatDirty(i % w, i / w);
                }
            }
            if (IsThermallyActive(_heatTemperature[i], _airTemperature[i]))
                _thermalActivity.ActivateNext(i % w, i / w);
        }
        TimeSpan elapsed = Stopwatch.GetElapsedTime(t0);
        _thermalActivity.CommitNextPass();
        return elapsed;
    }

    public float SampleAverageHeat(Position center, int radius)
    {
        float sum = 0f;
        int count = 0;
        ForEachInRadius(center, radius, (nx, ny, _, _) =>
        {
            int idx = ny * Width + nx;
            sum += _heatEngine.GetTemperatureAt(_heatTemperature, _heatTemperature.Length, idx);
            count++;
        });
        return count > 0 ? sum / (count * 255f) : 0f;
    }

    public float SampleAverageAirTemperature(Position center, int radius)
    {
        float sum = 0f;
        int count = 0;
        ForEachInRadius(center, radius, (nx, ny, _, _) =>
        {
            int idx = ny * Width + nx;
            sum += _heatEngine.GetTemperatureAt(_airTemperature, _airTemperature.Length, idx);
            count++;
        });
        return count > 0 ? sum / count : 0f;
    }

    public int CountCellsInRadiusArea(Position center, int radius)
    {
        int count = 0;
        ForEachInRadius(center, radius, (_, _, _, _) => count++);
        return count;
    }

    /// <summary>
    /// Applies an exact integer total <b>temperature</b> delta (not energy) across the same square area used by
    /// <see cref="SampleAverageHeat"/>. Returns the actually applied integer total. The resulting energy is
    /// recorded as external injection in <see cref="Ledger"/>; entity code that exchanges heat with the
    /// grid should use <see cref="AddEnergyInRadiusArea"/> / <see cref="ExtractEnergyInRadiusArea"/> instead.
    /// </summary>
    public int AddHeatTotalInRadiusArea(Position center, int radius, int totalAmount)
    {
        if (totalAmount == 0)
            return 0;
        int count = CountCellsInRadiusArea(center, radius);
        if (count <= 0)
            return 0;

        int baseDelta = totalAmount / count;
        int remainder = totalAmount % count;
        int remainderAbs = Math.Abs(remainder);
        int remainderSign = Math.Sign(remainder);

        int applied = 0;
        int i = 0;
        ForEachInRadius(center, radius, (nx, ny, _, _) =>
        {
            int delta = baseDelta;
            if (i < remainderAbs)
                delta += remainderSign;
            i++;

            if (delta == 0)
                return;

            int offset = nx + ny * Width;
            float old = _heatTemperature[offset];
            AddToTerrainField(offset, delta);
            float next = _heatTemperature[offset];
            Ledger.RecordInjected((double)delta * TerrainCapacityAt(offset));
            if (ShouldMarkHeatDirty(old, next))
                MarkHeatDirty(nx, ny);
            ActivateTileByCell(nx, ny);
            RequestThermalWake();
            applied += delta;
        });

        return applied;
    }

    public int AddAirHeatTotalInRadiusArea(Position center, int radius, int totalAmount)
    {
        if (totalAmount == 0)
            return 0;
        int count = CountCellsInRadiusArea(center, radius);
        if (count <= 0)
            return 0;

        int baseDelta = totalAmount / count;
        int remainder = totalAmount % count;
        int remainderAbs = Math.Abs(remainder);
        int remainderSign = Math.Sign(remainder);

        int applied = 0;
        int i = 0;
        ForEachInRadius(center, radius, (nx, ny, _, _) =>
        {
            int delta = baseDelta;
            if (i < remainderAbs)
                delta += remainderSign;
            i++;

            if (delta == 0)
                return;

            int offset = nx + ny * Width;
            float old = _airTemperature[offset];
            _heatEngine.AddEnergyAt(_airTemperature, offset, delta);
            float next = _airTemperature[offset];
            Ledger.RecordInjected((double)(next - old) * _simulationSettings.ThermalCapacityAir);
            MirrorAirIntoTerrainFieldIfAirCell(nx, ny, offset, next);
            if (IsThermallyActive(_heatTemperature[offset], next))
                ActivateTileByCell(nx, ny);
            RequestThermalWake();
            applied += (int)MathF.Round(next - old);
        });

        return applied;
    }

    /// <summary>The two temperature fields of the grid.</summary>
    public enum ThermalField { Terrain, Air }

    private float TerrainCapacityAt(int offset)
        => _heatEngine.GetHeatCapacity(Pixel.GetThermalMaterial(_data[offset]));

    private float[] FieldArray(ThermalField field)
        => field == ThermalField.Terrain ? _heatTemperature : _airTemperature;

    private float FieldCapacityAt(ThermalField field, int offset)
        => field == ThermalField.Terrain ? TerrainCapacityAt(offset) : _simulationSettings.ThermalCapacityAir;

    private bool IsAirCell(int offset)
        => Pixel.GetThermalMaterial(_data[offset]) == ThermalMaterial.Air;

    /// <summary>
    /// Sinks and relays only take heat that is stored in the cells. Hot rock is a source: it is pinned hot and refills
    /// itself (and the air in it) from nothing, so drawing from it would be an unlimited supply that cools the source
    /// instead of the tunnel. Everything else, including the air and pinned cells of a base, can be drawn from.
    /// </summary>
    private bool CannotExtractFrom(int offset, ThermalField field)
        => Pixel.GetThermalMaterial(_data[offset]) is ThermalMaterial.HotRock or ThermalMaterial.FrostRock;

    /// <summary>
    /// Adds a temperature delta to the terrain field of a cell. In an air cell the terrain field is only a
    /// mirror of the air field and holds no energy of its own, so the delta lands in the air field (where the
    /// energy lives) and the mirror follows. Without this the heat would be erased on the next solver step.
    /// </summary>
    private void AddToTerrainField(int offset, float deltaTemperature)
    {
        if (IsAirCell(offset))
        {
            float next = _airTemperature[offset] + deltaTemperature;
            _airTemperature[offset] = next;
            _heatTemperature[offset] = next;
        }
        else
        {
            _heatTemperature[offset] += deltaTemperature;
        }
    }

    /// <summary>Keeps the terrain-field mirror of an air cell equal to its air temperature.</summary>
    private void MirrorAirIntoTerrainFieldIfAirCell(int nx, int ny, int offset, float airTemperature)
    {
        if (!IsAirCell(offset))
            return;
        float oldMirror = _heatTemperature[offset];
        _heatTemperature[offset] = airTemperature;
        if (ShouldMarkHeatDirty(oldMirror, airTemperature))
            MarkHeatDirty(nx, ny);
    }

    private void ApplyFieldDelta(int nx, int ny, ThermalField field, float deltaTemperature)
    {
        int offset = nx + ny * Width;
        if (field == ThermalField.Terrain && !IsAirCell(offset))
        {
            float old = _heatTemperature[offset];
            float next = old + deltaTemperature;
            _heatTemperature[offset] = next;
            if (ShouldMarkHeatDirty(old, next))
                MarkHeatDirty(nx, ny);
            ActivateTileByCell(nx, ny);
        }
        else
        {
            // Air field, or the (mirrored) terrain field of an air cell: both are the same stored energy.
            float next = _airTemperature[offset] + deltaTemperature;
            _airTemperature[offset] = next;
            MirrorAirIntoTerrainFieldIfAirCell(nx, ny, offset, next);
            if (IsThermallyActive(_heatTemperature[offset], next))
                ActivateTileByCell(nx, ny);
        }
    }

    /// <summary>Sum of heat capacities of every cell in the square area, for the given field.</summary>
    public double SumHeatCapacityInRadiusArea(Position center, int radius, ThermalField field)
    {
        double sum = 0.0;
        ForEachInRadius(center, radius, (nx, ny, _, _) => sum += FieldCapacityAt(field, nx + ny * Width));
        return sum;
    }

    /// <summary>
    /// Adds <paramref name="energy"/> to the square area as one uniform temperature rise
    /// (<c>energy / sum(capacity)</c>), so exactly that much energy enters the grid. This is a transfer from
    /// an entity that has already been debited; it does not touch the <see cref="Ledger"/>.
    /// A negative <paramref name="energy"/> removes energy and can drive temperatures below ambient;
    /// use <see cref="ExtractEnergyInRadiusArea"/> for a sink that may only take existing heat.
    /// Returns the energy applied (0 if the area has no cells).
    /// </summary>
    public double AddEnergyInRadiusArea(Position center, int radius, double energy, ThermalField field)
    {
        if (energy == 0.0)
            return 0.0;
        double capacity = SumHeatCapacityInRadiusArea(center, radius, field);
        if (capacity <= 0.0)
            return 0.0;

        float deltaTemperature = (float)(energy / capacity);
        ForEachInRadius(center, radius, (nx, ny, _, _) => ApplyFieldDelta(nx, ny, field, deltaTemperature));
        RequestThermalWake();
        return energy;
    }

    /// <summary>
    /// Removes up to <paramref name="maxEnergy"/> from cells that are warmer than ambient, in proportion to
    /// the heat each cell holds above ambient. Cells never drop below ambient, so a sink cannot conjure
    /// energy out of a cold area. Returns the energy actually removed; the caller decides where it goes
    /// (a reactor, or <see cref="Ledger"/> if it is destroyed).
    /// </summary>
    public double ExtractEnergyInRadiusArea(Position center, int radius, double maxEnergy, ThermalField field)
    {
        if (maxEnergy <= 0.0)
            return 0.0;

        float ambient = _simulationSettings.ThermalAmbientTemperature;
        float[] temperatures = FieldArray(field);
        double available = 0.0;
        ForEachInRadius(center, radius, (nx, ny, _, _) =>
        {
            int offset = nx + ny * Width;
            if (CannotExtractFrom(offset, field))
                return; // a heat source or sink, not storage: nothing to take
            float excess = temperatures[offset] - ambient;
            if (excess > 0f)
                available += excess * FieldCapacityAt(field, offset);
        });
        if (available <= 0.0)
            return 0.0;

        double take = Math.Min(maxEnergy, available);
        float fraction = (float)(take / available);
        ForEachInRadius(center, radius, (nx, ny, _, _) =>
        {
            int offset = nx + ny * Width;
            if (CannotExtractFrom(offset, field))
                return;
            float excess = temperatures[offset] - ambient;
            if (excess > 0f)
                ApplyFieldDelta(nx, ny, field, -excess * fraction);
        });
        RequestThermalWake();
        return take;
    }

    /// <summary>
    /// The temperature (above ambient) of the heat <see cref="ExtractEnergyInRadiusArea"/> would take from this area:
    /// the cells' excess temperatures weighted by the energy each one gives up. A small hot spot in cold ground
    /// still reads hot, because nearly all the extracted energy comes from it. Zero when there is nothing to take.
    /// </summary>
    public double ExtractableHeatTemperature(Position center, int radius, ThermalField field)
    {
        float ambient = _simulationSettings.ThermalAmbientTemperature;
        float[] temperatures = FieldArray(field);
        double energy = 0.0, weighted = 0.0;
        ForEachInRadius(center, radius, (nx, ny, _, _) =>
        {
            int offset = nx + ny * Width;
            if (CannotExtractFrom(offset, field))
                return;
            float excess = temperatures[offset] - ambient;
            if (excess <= 0f)
                return;
            double e = excess * FieldCapacityAt(field, offset);
            energy += e;
            weighted += e * excess;
        });
        return energy > 0.0 ? weighted / energy : 0.0;
    }

    /// <summary>
    /// Forces one cell back to ambient and records the removed energy as a base sink in <see cref="Ledger"/>.
    /// Returns the removed energy (negative if the cell was below ambient).
    /// </summary>
    public double SinkCellToAmbient(Position pos, ThermalField field)
    {
        if ((uint)pos.X >= (uint)Width || (uint)pos.Y >= (uint)Height)
            return 0.0;
        int offset = pos.X + pos.Y * Width;
        float[] temperatures = FieldArray(field);
        float excess = temperatures[offset] - _simulationSettings.ThermalAmbientTemperature;
        if (excess == 0f)
            return 0.0;

        ApplyFieldDelta(pos.X, pos.Y, field, -excess);
        double energy = (double)excess * FieldCapacityAt(field, offset);
        Ledger.RecordSunkBase(energy);
        return energy;
    }

    public double SumTotalThermalEnergy()
    {
        return _heatEngine.SumTotalEnergy(_heatTemperature, _airTemperature, _data);
    }

    private void ForEachInRadius(Position center, int radius, Action<int, int, int, int> visitor)
    {
        int w = Width, h = Height;
        for (int dy = -radius; dy <= radius; dy++)
        {
            int ny = center.Y + dy;
            if ((uint)ny >= (uint)h) continue;
            for (int dx = -radius; dx <= radius; dx++)
            {
                int nx = center.X + dx;
                if ((uint)nx >= (uint)w) continue;
                visitor(nx, ny, dx, dy);
            }
        }
    }

    public bool TryGetHeatDirtyRect(out Rect dirtyRect) => _thermalActivity.TryGetDirtyRect(out dirtyRect);

    public void ClearHeatDirtyRect() => _thermalActivity.ClearDirtyRect();

    private void RequestThermalWake() => _thermalScheduler.RequestWake();

    private void MarkHeatDirty(int x, int y) => _thermalActivity.MarkDirty(x, y);

    private static bool ShouldMarkHeatDirty(float oldTemperature, float newTemperature)
    {
        return HeatToByte(oldTemperature) != HeatToByte(newTemperature);
    }

    /// <summary>Degrees per step of the one-byte heat encoding used for render uploads and dirty tracking.</summary>
    public const float HeatByteScale = 4.0f;

    /// <summary>
    /// Degrees below ambient the byte encoding can still show: byte 0 is <c>-HeatByteOffset</c>, so frost reads as
    /// well as heat. Renderers subtract <c>HeatByteOffset / (HeatByteScale * 255)</c> from the normalised value.
    /// </summary>
    public const float HeatByteOffset = 80.0f;

    /// <summary>The one-byte encoding of a temperature: <c>(t + HeatByteOffset) / HeatByteScale</c>, clamped.</summary>
    public static byte HeatToByte(float temperature)
    {
        return (byte)Math.Clamp((int)MathF.Round((temperature + HeatByteOffset) / HeatByteScale), 0, 255);
    }
}
