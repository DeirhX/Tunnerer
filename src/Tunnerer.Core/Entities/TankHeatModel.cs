namespace Tunnerer.Core.Entities;

using Tunnerer.Core.Config;

internal static class TankHeatModel
{
    public static float ComputeActionHeat(in SimulationSettings s, int frameDugPixels, bool frameShotFired)
    {
        float heat = frameDugPixels * s.TankHeatDigPerPixel;
        if (frameShotFired)
            heat += s.TankHeatShootPerShot;
        return heat;
    }

    public static float ComputeTankTerrainHeatFlow(in SimulationSettings s, float tankTemperature, float terrainTemperature)
    {
        float deltaT = terrainTemperature - tankTemperature;
        float dQ = s.TankTerrainConductance * deltaT;
        float maxStableDQ = s.TankHeatCapacity * MathF.Abs(deltaT) * 0.01f;
        if (dQ > maxStableDQ) dQ = maxStableDQ;
        else if (dQ < -maxStableDQ) dQ = -maxStableDQ;
        return dQ;
    }

    public static float ComputeTankDeltaFromHeatFlow(in SimulationSettings s, float dQ)
    {
        return s.TankHeatCapacity > 0f ? dQ / s.TankHeatCapacity : 0f;
    }

    public static float ComputeTerrainDeltaFromHeatFlow(in SimulationSettings s, float dQ)
    {
        return s.TerrainHeatCapacity > 0f ? -dQ / s.TerrainHeatCapacity : 0f;
    }

    public static float ComputeTankAirHeatFlow(in SimulationSettings s, float tankTemperature, float airTemperature)
    {
        float deltaT = airTemperature - tankTemperature;
        float dQ = s.TankAmbientConductance * deltaT;
        float maxStableDQ = s.TankHeatCapacity * MathF.Abs(deltaT) * 0.01f;
        if (dQ > maxStableDQ) dQ = maxStableDQ;
        else if (dQ < -maxStableDQ) dQ = -maxStableDQ;
        return dQ;
    }

    public static int ComputeOverheatDamage(in SimulationSettings s, float heat)
    {
        if (heat <= s.TankHeatSafeMax) return 0;
        float excess = heat - s.TankHeatSafeMax;
        return (int)(excess * s.TankOverheatDamagePerDegree);
    }

    public static float ClampHeat(float heat)
    {
        return Math.Max(0f, heat);
    }
}
