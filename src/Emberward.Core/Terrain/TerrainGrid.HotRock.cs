namespace Emberward.Core.Terrain;

using Emberward.Core.Types;

public partial class TerrainGrid
{
    /// <summary>
    /// Turns the dirt and rock inside a circle into a finite hot rock pocket and returns the number of cells converted.
    /// </summary>
    public int PlaceHotRockPocket(Position center, int radius) =>
        PlacePocket(center, radius, TerrainPixel.HotRock, _simulationSettings.HotRockStartTemperature);

    /// <summary>
    /// Turns the dirt and rock inside a circle into a finite frost pocket and returns the number of cells converted.
    /// </summary>
    public int PlaceFrostRockPocket(Position center, int radius) =>
        PlacePocket(center, radius, TerrainPixel.FrostRock, _simulationSettings.FrostRockStartTemperature);

    /// <summary>
    /// Seeds the temperature of the rock that replaces dirt and stone. The new rock exchanges and loses heat normally.
    /// </summary>
    private int PlacePocket(Position center, int radius, TerrainPixel pixel, float startTemperature)
    {
        var material = _simulationSettings.ThermalMaterials[Pixel.GetThermalMaterial(pixel)];
        float temperature = material.FixedTemperature ?? startTemperature;
        int radiusSq = radius * radius;
        int converted = 0;

        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > radiusSq)
                    continue;

                var pos = new Position(center.X + dx, center.Y + dy);
                // Keep a one-cell margin so neighbour lookups stay valid.
                if (pos.X < 1 || pos.Y < 1 || pos.X >= Width - 1 || pos.Y >= Height - 1)
                    continue;

                if (!Pixel.IsSolidGround(_data[pos.X + pos.Y * Width]))
                    continue;

                SeedHeatedPixel(pos, pixel, temperature);
                converted++;
            }

        return converted;
    }

    /// <summary>
    /// Writes a pixel and sets the cell to <paramref name="temperature"/> at once, recording the energy that adds in
    /// the <see cref="Ledger"/> as injected, and wakes its tile so the solver steps it.
    /// </summary>
    private void SeedHeatedPixel(Position pos, TerrainPixel pixel, float temperature)
    {
        int offset = pos.X + pos.Y * Width;
        SetPixel(pos, pixel);
        float old = _heatTemperature[offset];
        _heatTemperature[offset] = temperature;
        Ledger.RecordInjected((temperature - old) * (double)_heatEngine.GetHeatCapacity(Pixel.GetThermalMaterial(pixel)));
        MarkHeatDirty(pos.X, pos.Y);
        ActivateTileByCell(pos.X, pos.Y);
    }
}
