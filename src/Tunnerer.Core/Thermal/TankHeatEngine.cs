namespace Tunnerer.Core.Thermal;

using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;

/// <summary>
/// Computes a full tank heat step against terrain + ambient.
/// Extracted from Tank to make thermal behavior testable with real-world wiring.
/// </summary>
public sealed class TankHeatEngine
{
    private readonly SimulationSettings _settings;

    public TankHeatEngine()
        : this(SimulationSettings.FromTweaks())
    {
    }

    public TankHeatEngine(SimulationSettings settings)
    {
        _settings = settings;
    }

    public float Advance(
        float currentHeat,
        int frameDugPixels,
        bool frameShotFired,
        float sampledTerrainTemperature,
        float sampledAirTemperature,
        int sampleCells,
        Func<int, int> applyTerrainTotalDelta,
        Func<int, int> applyAirTotalDelta,
        out int overheatDamage)
    {
        float nextHeat = currentHeat;
        nextHeat += TankHeatModel.ComputeActionHeat(in _settings, frameDugPixels, frameShotFired);

        float dQTerrain = TankHeatModel.ComputeTankTerrainHeatFlow(in _settings, nextHeat, sampledTerrainTemperature);

        if (sampleCells > 0 && _settings.TerrainHeatCapacity > 0f)
        {
            float desiredTerrainAvgDelta = TankHeatModel.ComputeTerrainDeltaFromHeatFlow(in _settings, dQTerrain);
            int desiredTerrainTotalDelta = (int)MathF.Round(desiredTerrainAvgDelta * sampleCells);
            int appliedTerrainTotalDelta = applyTerrainTotalDelta(desiredTerrainTotalDelta);
            float appliedTerrainAvgDelta = appliedTerrainTotalDelta / (float)sampleCells;
            float appliedDQTerrain = -appliedTerrainAvgDelta * _settings.TerrainHeatCapacity;
            nextHeat += TankHeatModel.ComputeTankDeltaFromHeatFlow(in _settings, appliedDQTerrain);
        }
        else
        {
            nextHeat += TankHeatModel.ComputeTankDeltaFromHeatFlow(in _settings, dQTerrain);
        }

        float dQAir = TankHeatModel.ComputeTankAirHeatFlow(in _settings, nextHeat, sampledAirTemperature);
        if (sampleCells > 0 && _settings.ThermalCapacityAir > 0f)
        {
            float desiredAirAvgDelta = -dQAir / _settings.ThermalCapacityAir;
            int desiredAirTotalDelta = (int)MathF.Round(desiredAirAvgDelta * sampleCells);
            int appliedAirTotalDelta = applyAirTotalDelta(desiredAirTotalDelta);
            float appliedAirAvgDelta = appliedAirTotalDelta / (float)sampleCells;
            float appliedDQAir = -appliedAirAvgDelta * _settings.ThermalCapacityAir;
            nextHeat += TankHeatModel.ComputeTankDeltaFromHeatFlow(in _settings, appliedDQAir);
        }
        else
        {
            nextHeat += TankHeatModel.ComputeTankDeltaFromHeatFlow(in _settings, dQAir);
        }

        overheatDamage = TankHeatModel.ComputeOverheatDamage(in _settings, nextHeat);
        return TankHeatModel.ClampHeat(nextHeat);
    }
}
