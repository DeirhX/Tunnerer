namespace Tunnerer.Core.Terrain;

using Tunnerer.Core.Config;
using Tunnerer.Core.Thermal;
using Tunnerer.Core.Types;

public partial class TerrainGrid
{
    /// <summary>
    /// Turns the dirt and rock inside a circle into a hot rock pocket: indestructible, solid, and pinned at
    /// <see cref="Tweaks.World.ThermalFixedHotRockTemperature"/>, so it is a permanent heat source for any air or
    /// tunnel it touches. Open air, bases and concrete are left alone. The initial heat is recorded in the
    /// <see cref="Ledger"/> as injected; the energy the pocket keeps emitting afterwards shows up as negative
    /// fixed-temperature sink (the pin re-creates what flowed out).
    /// Returns the number of cells converted.
    /// </summary>
    public int PlaceHotRockPocket(Position center, int radius)
    {
        float hot = _heatEngine.GetFixedTemperature(ThermalMaterial.HotRock);
        double capacity = _heatEngine.GetHeatCapacity(ThermalMaterial.HotRock);
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

                int offset = pos.X + pos.Y * Width;
                TerrainPixel pixel = _data[offset];
                if (!(Pixel.IsDirt(pixel) || pixel == TerrainPixel.DirtGrow || Pixel.IsRock(pixel)))
                    continue;

                SetPixel(pos, TerrainPixel.HotRock);
                float old = _heatTemperature[offset];
                _heatTemperature[offset] = hot;
                Ledger.RecordInjected((hot - old) * capacity);
                MarkHeatDirty(pos.X, pos.Y);
                ActivateTileByCell(pos.X, pos.Y);
                converted++;
            }

        if (converted > 0)
            RequestThermalWake();
        return converted;
    }
}
