namespace Emberward.Core.Terrain;

using Emberward.Core.Config;
using Emberward.Core.Thermal;
using Emberward.Core.Types;

public partial class TerrainGrid
{
    /// <summary>
    /// Turns the dirt and rock inside a circle into a hot rock pocket: indestructible, solid, and pinned at
    /// <see cref="Tweaks.World.ThermalFixedHotRockTemperature"/>, so it is a permanent heat source for any air or
    /// tunnel it touches. Returns the number of cells converted (see <see cref="PlacePinnedPocket"/>).
    /// </summary>
    public int PlaceHotRockPocket(Position center, int radius) =>
        PlacePinnedPocket(center, radius, TerrainPixel.HotRock, ThermalMaterial.HotRock);

    /// <summary>
    /// Turns the dirt and rock inside a circle into a frost pocket: indestructible, solid, and pinned at
    /// <see cref="Tweaks.World.ThermalFixedFrostRockTemperature"/>, a permanent cold source for anything nearby.
    /// Returns the number of cells converted (see <see cref="PlacePinnedPocket"/>).
    /// </summary>
    public int PlaceFrostRockPocket(Position center, int radius) =>
        PlacePinnedPocket(center, radius, TerrainPixel.FrostRock, ThermalMaterial.FrostRock);

    /// <summary>
    /// Converts the dirt and rock inside a circle into <paramref name="pixel"/>, a fixed-temperature material. Open air,
    /// bases and concrete are left alone. The cells' change to the pinned temperature is recorded in the
    /// <see cref="Ledger"/> as injected; what the pocket keeps emitting or absorbing afterwards shows up as the
    /// fixed-temperature sink (the pin re-creates what flowed out or destroys what flowed in).
    /// </summary>
    private int PlacePinnedPocket(Position center, int radius, TerrainPixel pixel, ThermalMaterial material)
    {
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

                SetPinnedPixel(pos, pixel, material);
                converted++;
            }

        return converted;
    }


    /// <summary>
    /// Writes a fixed-temperature pixel and sets the cell to its pinned temperature at once, recording the change in
    /// the <see cref="Ledger"/> as injected. The cell's tile is woken, so the solver steps the new source.
    /// </summary>
    private void SetPinnedPixel(Position pos, TerrainPixel pixel, ThermalMaterial material)
    {
        SeedHeatedPixel(pos, pixel, _heatEngine.GetFixedTemperature(material));
    }

    /// <summary>
    /// Writes a pixel and sets the cell to <paramref name="temperature"/> at once, recording the energy that adds in
    /// the <see cref="Ledger"/> as injected, and wakes its tile so the solver steps it. For a pinned material the
    /// temperature is its pin; for ore it is only a starting temperature, and the cell cools like any ground.
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
