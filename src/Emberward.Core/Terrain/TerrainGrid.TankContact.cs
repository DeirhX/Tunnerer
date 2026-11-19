namespace Emberward.Core.Terrain;

using Emberward.Core.Types;

/// <summary>The two independent heat stores under a tank. Solid temperature is weighted by heat capacity.</summary>
public readonly record struct TankHeatContact(
    float SolidTemperature, double SolidCapacity, float SolidFraction, float AirTemperature, double AirCapacity);

public partial class TerrainGrid
{
    public TankHeatContact SampleTankContact(Position center, int radius)
    {
        var area = AreaAround(center, radius);
        int cells = 0, solids = 0;
        double solidEnergy = 0, solidCapacity = 0, airTemperatureSum = 0;
        for (int y = area.MinY; y <= area.MaxY; y++)
            for (int x = area.MinX; x <= area.MaxX; x++)
            {
                int offset = x + y * Width;
                cells++;
                airTemperatureSum += _airTemperature[offset];
                if (IsAirCell(offset))
                    continue; // Its terrain field mirrors the same air.
                solids++;
                double capacity = TerrainCapacityAt(offset);
                solidCapacity += capacity;
                solidEnergy += capacity * _heatTemperature[offset];
            }
        if (cells == 0)
            return default;
        return new TankHeatContact(
            solidCapacity > 0 ? (float)(solidEnergy / solidCapacity) : 0f, solidCapacity, solids / (float)cells,
            (float)(airTemperatureSum / cells), cells * (double)_heatEngine.AirHeatCapacity);
    }

    /// <summary>Returns the tank's exchange to the same cells sampled in this step, as a uniform delta per field.</summary>
    public void ApplyTankExchange(Position center, int radius, in TankHeatContact contact, double toSolid, double toAir)
    {
        if (toSolid == 0 && toAir == 0)
            return;
        float solidDelta = toSolid == 0 ? 0f : (float)(toSolid / contact.SolidCapacity);
        float airDelta = toAir == 0 ? 0f : (float)(toAir / contact.AirCapacity);
        var area = AreaAround(center, radius);
        for (int y = area.MinY; y <= area.MaxY; y++)
            for (int x = area.MinX; x <= area.MaxX; x++)
            {
                if (solidDelta != 0f && !IsAirCell(x + y * Width))
                    ApplyFieldDelta(x, y, ThermalField.Terrain, solidDelta);
                if (airDelta != 0f)
                    ApplyFieldDelta(x, y, ThermalField.Air, airDelta);
            }
    }
}
