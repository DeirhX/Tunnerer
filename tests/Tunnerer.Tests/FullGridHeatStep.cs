using Tunnerer.Core.Terrain;
using Tunnerer.Core.Thermal;

namespace Tunnerer.Tests;

/// <summary>
/// One heat step over a whole bare grid, built from the two pieces the game's solver uses
/// (<see cref="TerrainHeatEngine.ComputeRegionDeltaConservative"/> over one region covering the grid, then
/// <see cref="TerrainHeatEngine.ResolveTerrainTemperature"/> per cell), so thermal tests need no
/// <see cref="TerrainGrid"/>, scheduler or activity tiles.
/// </summary>
internal static class FullGridHeatStep
{
    public static void Step(TerrainHeatEngine engine, float[] terrain, float[] air, TerrainPixel[] pixels,
        int width, int height, ThermalLedger? ledger = null)
    {
        int len = width * height;
        var terrainDelta = new float[len];
        var airDelta = new float[len];
        engine.ComputeRegionDeltaConservative(
            terrain, air, pixels, width, height, 0, 0, width - 1, height - 1, terrainDelta, airDelta);

        double sunkFixed = 0.0;
        for (int i = 0; i < len; i++)
        {
            float nextAir = air[i] + airDelta[i];
            terrain[i] = engine.ResolveTerrainTemperature(
                Pixel.GetThermalMaterial(pixels[i]), terrain[i] + terrainDelta[i], nextAir, ref sunkFixed);
            air[i] = nextAir;
        }

        ledger?.RecordSunkFixedTemperature(sunkFixed);
    }
}
