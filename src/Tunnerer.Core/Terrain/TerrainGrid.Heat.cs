namespace Tunnerer.Core.Terrain;

using System.Diagnostics;
using Tunnerer.Core.Config;
using Tunnerer.Core.Thermal;
using Tunnerer.Core.Types;

public partial class TerrainGrid
{
    /// <summary>What the last <see cref="CoolDown"/> did, for the profile.</summary>
    /// <param name="Simulate">Time computing the exchange.</param>
    /// <param name="Apply">Time applying it and merging what the tiles found.</param>
    /// <param name="ActiveTiles">Tiles that were still changing when the tick began.</param>
    /// <param name="SteppedTiles">Tiles stepped: the active ones and the ring around them. Zero when the solver slept.</param>
    /// <param name="UsedParallel">Whether the tiles were stepped on several threads.</param>
    public readonly record struct CoolDownProfile(
        TimeSpan Simulate, TimeSpan Apply, int ActiveTiles, int SteppedTiles, bool UsedParallel);

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
            ActivateTileByCell(pos.X, pos.Y);
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
        var area = AreaAround(center, radius);
        for (int ny = area.MinY; ny <= area.MaxY; ny++)
            for (int nx = area.MinX; nx <= area.MaxX; nx++)
            {
                int dx = nx - center.X, dy = ny - center.Y;
                int dist2 = dx * dx + dy * dy;
                if (dist2 > radiusSq) continue;
                float falloff = 1f - (float)dist2 / radiusSq;
                int scaled = (int)(amount * falloff);
                if (scaled == 0) continue;
                int offset = nx + ny * Width;
                float old = _heatTemperature[offset];
                AddToTerrainField(offset, scaled);
                float next = _heatTemperature[offset];
                Ledger.RecordInjected((double)scaled * TerrainCapacityAt(offset));
                if (ShouldMarkHeatDirty(old, next))
                    MarkHeatDirty(nx, ny);
                ActivateTileByCell(nx, ny);
            }
    }

    /// <summary>
    /// One tick of terrain heat: step the tiles that are still changing and the ring around them, unless there are
    /// none or the scheduler holds the tick back. Profiling timings go to <see cref="LastCoolDownProfile"/> only.
    /// </summary>
    public void CoolDown()
    {
        int activeTiles = _thermalActivity.ActiveTileCount;
        if (!_thermalScheduler.ShouldStep(_simulationSettings, activeTiles, _thermalActivity.TileCount))
        {
            LastCoolDownProfile = new CoolDownProfile(TimeSpan.Zero, TimeSpan.Zero, activeTiles, 0, false);
            return;
        }

        StepActiveTiles(out TimeSpan simulate, out TimeSpan apply, out int steppedTiles, out bool parallel);
        LastCoolDownProfile = new CoolDownProfile(simulate, apply, activeTiles, steppedTiles, parallel);
    }

    public float SampleAverageHeat(Position center, int radius)
    {
        float sum = 0f;
        int count = 0;
        var area = AreaAround(center, radius);
        for (int ny = area.MinY; ny <= area.MaxY; ny++)
            for (int nx = area.MinX; nx <= area.MaxX; nx++)
            {
                int idx = ny * Width + nx;
                sum += _heatEngine.GetTemperatureAt(_heatTemperature, _heatTemperature.Length, idx);
                count++;
            }
        return count > 0 ? sum / (count * 255f) : 0f;
    }

    public float SampleAverageAirTemperature(Position center, int radius)
    {
        float sum = 0f;
        int count = 0;
        var area = AreaAround(center, radius);
        for (int ny = area.MinY; ny <= area.MaxY; ny++)
            for (int nx = area.MinX; nx <= area.MaxX; nx++)
            {
                int idx = ny * Width + nx;
                sum += _heatEngine.GetTemperatureAt(_airTemperature, _airTemperature.Length, idx);
                count++;
            }
        return count > 0 ? sum / count : 0f;
    }

    public int CountCellsInRadiusArea(Position center, int radius)
    {
        var area = AreaAround(center, radius);
        return Math.Max(0, area.MaxX - area.MinX + 1) * Math.Max(0, area.MaxY - area.MinY + 1);
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
        var area = AreaAround(center, radius);
        for (int ny = area.MinY; ny <= area.MaxY; ny++)
            for (int nx = area.MinX; nx <= area.MaxX; nx++)
            {
                int delta = baseDelta;
                if (i < remainderAbs)
                    delta += remainderSign;
                i++;

                if (delta == 0)
                    continue;

                int offset = nx + ny * Width;
                float old = _heatTemperature[offset];
                AddToTerrainField(offset, delta);
                float next = _heatTemperature[offset];
                Ledger.RecordInjected((double)delta * TerrainCapacityAt(offset));
                if (ShouldMarkHeatDirty(old, next))
                    MarkHeatDirty(nx, ny);
                ActivateTileByCell(nx, ny);
                applied += delta;
            }

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
        var area = AreaAround(center, radius);
        for (int ny = area.MinY; ny <= area.MaxY; ny++)
            for (int nx = area.MinX; nx <= area.MaxX; nx++)
            {
                int delta = baseDelta;
                if (i < remainderAbs)
                    delta += remainderSign;
                i++;

                if (delta == 0)
                    continue;

                int offset = nx + ny * Width;
                float old = _airTemperature[offset];
                _heatEngine.AddEnergyAt(_airTemperature, offset, delta);
                float next = _airTemperature[offset];
                Ledger.RecordInjected((double)(next - old) * _heatEngine.AirHeatCapacity);
                MirrorAirIntoTerrainFieldIfAirCell(nx, ny, offset, next);
                ActivateTileByCell(nx, ny);
                applied += (int)MathF.Round(next - old);
            }

        return applied;
    }

    /// <summary>The two temperature fields of the grid.</summary>
    public enum ThermalField { Terrain, Air }

    private float TerrainCapacityAt(int offset)
        => _heatEngine.GetHeatCapacity(Pixel.GetThermalMaterial(_data[offset]));

    private float[] FieldArray(ThermalField field)
        => field == ThermalField.Terrain ? _heatTemperature : _airTemperature;

    private float FieldCapacityAt(ThermalField field, int offset)
        => field == ThermalField.Terrain ? TerrainCapacityAt(offset) : _heatEngine.AirHeatCapacity;

    private bool IsAirCell(int offset)
        => Pixel.GetThermalMaterial(_data[offset]) == ThermalMaterial.Air;

    /// <summary>
    /// Sinks and relays only take heat that is stored in the cells. Hot rock (and an emberite core, and frost rock as a
    /// cold source) is a source: it is pinned hot and refills
    /// itself (and the air in it) from nothing, so drawing from it would be an unlimited supply that cools the source
    /// instead of the tunnel. Everything else, including the air and pinned cells of a base, can be drawn from.
    /// </summary>
    private bool CannotExtractFrom(int offset, ThermalField field)
        => Pixel.GetThermalMaterial(_data[offset]) is ThermalMaterial.HotRock or ThermalMaterial.FrostRock
            or ThermalMaterial.EmberiteCore;

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
            ActivateTileByCell(nx, ny);
        }
    }

    /// <summary>Sum of heat capacities of every cell in the square area, for the given field.</summary>
    public double SumHeatCapacityInRadiusArea(Position center, int radius, ThermalField field)
    {
        double sum = 0.0;
        var area = AreaAround(center, radius);
        for (int ny = area.MinY; ny <= area.MaxY; ny++)
            for (int nx = area.MinX; nx <= area.MaxX; nx++)
                sum += FieldCapacityAt(field, nx + ny * Width);
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
        var area = AreaAround(center, radius);
        for (int ny = area.MinY; ny <= area.MaxY; ny++)
            for (int nx = area.MinX; nx <= area.MaxX; nx++)
                ApplyFieldDelta(nx, ny, field, deltaTemperature);
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
        var area = AreaAround(center, radius);
        for (int ny = area.MinY; ny <= area.MaxY; ny++)
            for (int nx = area.MinX; nx <= area.MaxX; nx++)
            {
                int offset = nx + ny * Width;
                if (CannotExtractFrom(offset, field))
                    continue; // a heat source or sink, not storage: nothing to take
                float excess = temperatures[offset] - ambient;
                if (excess > 0f)
                    available += excess * FieldCapacityAt(field, offset);
            }
        if (available <= 0.0)
            return 0.0;

        double take = Math.Min(maxEnergy, available);
        float fraction = (float)(take / available);
        for (int ny = area.MinY; ny <= area.MaxY; ny++)
            for (int nx = area.MinX; nx <= area.MaxX; nx++)
            {
                int offset = nx + ny * Width;
                if (CannotExtractFrom(offset, field))
                    continue;
                float excess = temperatures[offset] - ambient;
                if (excess > 0f)
                    ApplyFieldDelta(nx, ny, field, -excess * fraction);
            }
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
        var area = AreaAround(center, radius);
        for (int ny = area.MinY; ny <= area.MaxY; ny++)
            for (int nx = area.MinX; nx <= area.MaxX; nx++)
            {
                int offset = nx + ny * Width;
                if (CannotExtractFrom(offset, field))
                    continue;
                float excess = temperatures[offset] - ambient;
                if (excess <= 0f)
                    continue;
                double e = excess * FieldCapacityAt(field, offset);
                energy += e;
                weighted += e * excess;
            }
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

    /// <summary>
    /// The cells of the map within <paramref name="radius"/> of <paramref name="center"/> along both axes (a square):
    /// x from MinX to MaxX and y from MinY to MaxY, none when a Max is below its Min. Area functions visit them row
    /// by row, which fixes the order sums are taken in.
    /// </summary>
    private (int MinX, int MinY, int MaxX, int MaxY) AreaAround(Position center, int radius) => (
        Math.Max(0, center.X - radius), Math.Max(0, center.Y - radius),
        Math.Min(Width - 1, center.X + radius), Math.Min(Height - 1, center.Y + radius));

    /// <summary>
    /// A number that stays the same for as long as no temperature within <paramref name="radius"/> of
    /// <paramref name="center"/> can have changed; negative while heat there is on the move. A relay that found no
    /// heat to pump keeps it and looks again only once it differs.
    /// </summary>
    public long QuietHeatStamp(Position center, int radius)
    {
        var area = AreaAround(center, radius);
        return area.MaxX < area.MinX || area.MaxY < area.MinY
            ? 0
            : _thermalActivity.QuietStamp(area.MinX, area.MinY, area.MaxX, area.MaxY);
    }

    public bool TryGetHeatDirtyRect(out Rect dirtyRect) => _thermalActivity.TryGetDirtyRect(out dirtyRect);

    public void ClearHeatDirtyRect() => _thermalActivity.ClearDirtyRect();

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
