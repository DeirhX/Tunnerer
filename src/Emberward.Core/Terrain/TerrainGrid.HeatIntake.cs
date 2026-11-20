namespace Emberward.Core.Terrain;

using Emberward.Core.Types;

public partial class TerrainGrid
{
    /// <summary>Extractable heat above the floor and its weighted temperature, outside the exhaust square.</summary>
    public (double Energy, double Temperature) SampleHeatAround(Position center, int radius, int excludeRadius, float floor)
    {
        double energy = 0.0, weighted = 0.0;
        var area = AreaAround(center, radius);
        for (int y = area.MinY; y <= area.MaxY; y++)
            for (int x = area.MinX; x <= area.MaxX; x++)
            {
                if (InExhaust(x, y, center, excludeRadius))
                    continue;
                int offset = x + y * Width;
                float excess = IntakeExcess(offset, floor);
                double heat = (double)excess * TerrainCapacityAt(offset);
                energy += heat;
                weighted += heat * excess;
            }
        return (energy, floor + (energy > 0.0 ? weighted / energy : 0.0));
    }

    /// <summary>Moves a fraction of the heat above the floor out of the cells counted by <see cref="SampleHeatAround"/>.</summary>
    public double TakeHeatAround(Position center, int radius, int excludeRadius, float floor, double fraction)
    {
        double taken = 0.0;
        var area = AreaAround(center, radius);
        for (int y = area.MinY; y <= area.MaxY; y++)
            for (int x = area.MinX; x <= area.MaxX; x++)
            {
                if (InExhaust(x, y, center, excludeRadius))
                    continue;
                int offset = x + y * Width;
                float delta = (float)(IntakeExcess(offset, floor) * fraction);
                if (delta == 0f)
                    continue;
                ApplyFieldDelta(x, y, ThermalField.Terrain, -delta);
                taken += (double)delta * TerrainCapacityAt(offset);
            }
        return taken;
    }

    private static bool InExhaust(int x, int y, Position center, int radius) =>
        Math.Abs(x - center.X) <= radius && Math.Abs(y - center.Y) <= radius;

    private float IntakeExcess(int offset, float floor) => CannotExtractFrom(offset, ThermalField.Terrain)
        ? 0f : Math.Max(0f, _heatTemperature[offset] - floor);
}
