namespace Emberward.Core.Entities.Tanks;

using Emberward.Core.Config;

public static class TankHeatModel
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
    /// contact use the same formula, with the conductance and heat capacity of the contact.
    /// </summary>
    public static float ComputeHeatFlowIntoTank(in SimulationSettings s, float conductance, float tankTemperature,
        float surroundingTemperature, double surroundingCapacity)
    {
        if (s.TankHeatCapacity <= 0f || surroundingCapacity <= 0.0)
            return 0f;
        // Moving Q changes the temperature difference by Q * (1/Ctank + 1/Cfield).
        double equilibriumConductance = 1.0 / (1.0 / s.TankHeatCapacity + 1.0 / surroundingCapacity);
        return (float)(Math.Min(conductance, equilibriumConductance) * (surroundingTemperature - tankTemperature));
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

    /// <summary>Damage from deep cold: below the frostbite temperature, per degree, like overheating.</summary>
    public static int ComputeFrostbiteDamage(in SimulationSettings s, float heat)
    {
        if (heat >= s.TankFrostbiteTemperature) return 0;
        float deficit = s.TankFrostbiteTemperature - heat;
        return (int)(deficit * s.TankFrostbiteDamagePerDegree);
    }

    /// <summary>
    /// Fraction of its normal speed a tank at <paramref name="heat"/> moves at: full speed at or above
    /// <c>ColdSlowStart</c>, falling linearly to <c>ColdMinSpeed</c> at <c>ColdSlowFull</c> and below.
    /// </summary>
    public static float ColdSpeedFactor(float heat)
    {
        float start = Tweaks.Tank.ColdSlowStart, full = Tweaks.Tank.ColdSlowFull;
        if (heat >= start) return 1f;
        float t = Math.Clamp((start - heat) / (start - full), 0f, 1f);
        return 1f - (1f - Tweaks.Tank.ColdMinSpeed) * t;
    }
}
