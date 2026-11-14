namespace Tunnerer.Core.Thermal;

using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;

/// <summary>
/// Result of one tank heat step. Energies are expressed in the grid's units
/// (<c>temperature * heat capacity</c>); a tank's own energy is <c>Heat * Tweaks.Tank.TankHeatCapacity</c>.
/// </summary>
/// <param name="NextHeat">Tank heat after the step, not yet clamped to the physical floor.</param>
/// <param name="EnergyToTerrain">Energy the tank hands to the terrain field of its footprint (negative: it takes energy).</param>
/// <param name="EnergyToAir">Energy the tank hands to the air field of its footprint (negative: it takes energy).</param>
/// <param name="ActionHeat">Heat created by digging/shooting this frame, in tank heat units. This is an external source.</param>
/// <param name="OverheatDamage">Reactor damage caused by the resulting temperature.</param>
public readonly record struct TankHeatStep(
    float NextHeat,
    double EnergyToTerrain,
    double EnergyToAir,
    float ActionHeat,
    int OverheatDamage);

/// <summary>
/// Computes a full tank heat step against terrain + air.
/// Extracted from Tank to make thermal behavior testable with real-world wiring.
/// <para>
/// The engine never invents or loses energy on its own: whatever the tank gains from a field is returned as a
/// negative energy for that field, so the caller can move exactly that amount in or out of the grid.
/// Only <see cref="TankHeatStep.ActionHeat"/> is created from nothing.
/// </para>
/// <para>
/// Heat below ambient is the tank's coolant charge. Flows are driven by the heat <i>above</i> ambient only (both the
/// tank's and the surroundings'), so ambient air neither warms a cold tank nor is chilled by it: the charge is
/// spent only by absorbing real heat, and refrigeration happens at the base.
/// </para>
/// </summary>
public sealed class TankHeatEngine
{
    /// <param name="terrainCapacity">Sum of heat capacities of the terrain-field cells under the tank.</param>
    /// <param name="airCapacity">Sum of heat capacities of the air-field cells under the tank.</param>
    public TankHeatStep Advance(
        float currentHeat,
        int frameDugPixels,
        bool frameShotFired,
        float sampledTerrainTemperature,
        float sampledAirTemperature,
        double terrainCapacity,
        double airCapacity)
    {
        float actionHeat = TankHeatModel.ComputeActionHeat(frameDugPixels, frameShotFired);
        float nextHeat = currentHeat + actionHeat;

        double energyToTerrain = 0.0;
        if (terrainCapacity > 0.0)
        {
            float dQTerrain = TankHeatModel.ComputeTankTerrainHeatFlow(
                MathF.Max(nextHeat, 0f), MathF.Max(sampledTerrainTemperature, 0f));
            nextHeat += TankHeatModel.ComputeTankDeltaFromHeatFlow(dQTerrain);
            energyToTerrain = -dQTerrain;
        }

        double energyToAir = 0.0;
        if (airCapacity > 0.0)
        {
            float dQAir = TankHeatModel.ComputeTankAirHeatFlow(
                MathF.Max(nextHeat, 0f), MathF.Max(sampledAirTemperature, 0f));
            nextHeat += TankHeatModel.ComputeTankDeltaFromHeatFlow(dQAir);
            energyToAir = -dQAir;
        }

        int overheatDamage = TankHeatModel.ComputeOverheatDamage(nextHeat);
        return new TankHeatStep(nextHeat, energyToTerrain, energyToAir, actionHeat, overheatDamage);
    }
}
