namespace Emberward.Core.Thermal;

using Emberward.Core.Snapshots;
using Emberward.Core.Config;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Terrain;

/// <summary>
/// Result of one tank heat step. Energies are expressed in the grid's units
/// (<c>temperature * heat capacity</c>); a tank's own energy is <c>Heat * SimulationSettings.TankHeatCapacity</c>.
/// </summary>
/// <param name="NextHeat">Tank heat after the step, not yet clamped to the physical floor.</param>
/// <param name="EnergyToTerrain">Energy the tank hands to the terrain field of its footprint (negative: it takes energy).</param>
/// <param name="EnergyToAir">Energy the tank hands to the air field of its footprint (negative: it takes energy).</param>
/// <param name="ActionHeat">Heat created by digging/shooting this frame, in tank heat units. This is an external source.</param>
/// <param name="OverheatDamage">Reactor damage caused by the resulting temperature being too hot.</param>
/// <param name="FrostbiteDamage">Reactor damage caused by the resulting temperature being too cold.</param>
public readonly record struct TankHeatStep(
    float NextHeat,
    double EnergyToTerrain,
    double EnergyToAir,
    float ActionHeat,
    int OverheatDamage,
    int FrostbiteDamage = 0);

/// <summary>
/// Computes a full tank heat step against terrain + air.
/// Extracted from Tank to make thermal behavior testable with real-world wiring.
/// <para>
/// The engine never invents or loses energy on its own: whatever the tank gains from a field is returned as a
/// negative energy for that field, so the caller can move exactly that amount in or out of the grid.
/// Only <see cref="TankHeatStep.ActionHeat"/> is created from nothing.
/// </para>
/// <para>
/// Exchange follows the temperature difference in both directions: a hot tank warms its surroundings, a cold one is
/// warmed by them (chilling the cells under it), so a tank always drifts toward the temperature of where it stands.
/// </para>
/// </summary>
[NotSaved("fixed for the match: built from the settings")]
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

    /// <summary>Heat capacity of the tank: tank energy is <c>Heat * TankHeatCapacity</c>.</summary>
    public float TankHeatCapacity => _settings.TankHeatCapacity;

    public TankHeatStep Advance(
        float currentHeat,
        int frameDugPixels,
        bool frameShotFired,
        in TankHeatContact contact)
    {
        float actionHeat = TankHeatModel.ComputeActionHeat(in _settings, frameDugPixels, frameShotFired);
        float nextHeat = currentHeat + actionHeat;

        float fromSolid = TankHeatModel.ComputeHeatFlowIntoTank(in _settings, _settings.TankTerrainConductance * contact.SolidFraction,
            nextHeat, contact.SolidTemperature, contact.SolidCapacity);
        nextHeat += TankHeatModel.ComputeTankDeltaFromHeatFlow(in _settings, fromSolid);
        float fromAir = TankHeatModel.ComputeHeatFlowIntoTank(in _settings, _settings.TankAmbientConductance,
            nextHeat, contact.AirTemperature, contact.AirCapacity);
        nextHeat += TankHeatModel.ComputeTankDeltaFromHeatFlow(in _settings, fromAir);

        int overheatDamage = TankHeatModel.ComputeOverheatDamage(in _settings, nextHeat);
        int frostbiteDamage = TankHeatModel.ComputeFrostbiteDamage(in _settings, nextHeat);
        return new TankHeatStep(nextHeat, -fromSolid, -fromAir, actionHeat, overheatDamage, frostbiteDamage);
    }
}
