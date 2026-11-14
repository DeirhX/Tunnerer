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

    /// <summary>
    /// Heat flowing into the tank from what it touches this tick (negative: the tank loses heat). Terrain and air
    /// contact use this one formula; only the <paramref name="conductance"/> differs.
    /// </summary>
    public static float ComputeHeatFlowIntoTank(in SimulationSettings s, float conductance, float tankTemperature, float surroundingTemperature)
    {
        float deltaT = surroundingTemperature - tankTemperature;
        float dQ = conductance * deltaT;
        float maxStableDQ = s.TankHeatCapacity * MathF.Abs(deltaT) * 0.01f;
        if (dQ > maxStableDQ) dQ = maxStableDQ;
        else if (dQ < -maxStableDQ) dQ = -maxStableDQ;
        return dQ;
    }

    public static float ComputeTankDeltaFromHeatFlow(in SimulationSettings s, float dQ)
    {
        return s.TankHeatCapacity > 0f ? dQ / s.TankHeatCapacity : 0f;
    }

    public static int ComputeOverheatDamage(in SimulationSettings s, float heat)
    {
        if (heat <= s.TankHeatSafeMax) return 0;
        float excess = heat - s.TankHeatSafeMax;
        return (int)(excess * s.TankOverheatDamagePerDegree);
    }

    /// <summary>Heat cannot drop below the full coolant charge.</summary>
    public static float ClampHeat(in SimulationSettings s, float heat)
    {
        return Math.Max(-s.TankCoolantCapacity, heat);
    }
}
