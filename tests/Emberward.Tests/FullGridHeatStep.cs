using Emberward.Core.Config;
using Emberward.Core.Terrain;
using Emberward.Core.Thermal;

namespace Emberward.Tests;

/// <summary>
/// One heat step over a whole bare grid, built from the two pieces the game's solver uses
/// (<see cref="TerrainHeatEngine.ComputeRegionDeltaConservative"/> over one region covering the grid, then
/// <see cref="TerrainHeatEngine.ResolveCell"/> per cell), so thermal tests need no
/// <see cref="TerrainGrid"/>, scheduler or activity tiles.
/// </summary>
internal static class FullGridHeatStep
{
    /// <summary>
    /// The game's thermal settings with every material's loss to the outside switched off, for tests of a closed
    /// system: what the exchange alone does to energy that has nowhere to go.
    /// </summary>
    public static SimulationSettings LosslessSettings()
    {
        var settings = SimulationSettings.FromTweaks();
        var materials = settings.ThermalMaterials;
        foreach (ThermalMaterial material in Enum.GetValues<ThermalMaterial>())
            materials = materials.With(material, materials[material] with { Loss = 0f });
        return settings with { ThermalMaterials = materials };
    }

    public static void Step(TerrainHeatEngine engine, float[] terrain, float[] air, TerrainPixel[] pixels,
        int width, int height, ThermalLedger? ledger = null)
    {
        int len = width * height;
        var terrainDelta = new float[len];
        var airDelta = new float[len];
        engine.ComputeRegionDeltaConservative(
            terrain, air, pixels, width, height, 0, 0, width - 1, height - 1, terrainDelta, airDelta);

        double sunkFixed = 0.0, sunkLoss = 0.0;
        for (int i = 0; i < len; i++)
        {
            float nextAir = air[i] + airDelta[i];
            terrain[i] = engine.ResolveCell(
                Pixel.GetThermalMaterial(pixels[i]), terrain[i] + terrainDelta[i], ref nextAir, ref sunkFixed, ref sunkLoss);
            air[i] = nextAir;
        }

        ledger?.RecordSunkFixedTemperature(sunkFixed);
        ledger?.RecordSunkAmbientLoss(sunkLoss);
    }
}
