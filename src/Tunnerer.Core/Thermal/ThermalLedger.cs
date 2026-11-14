namespace Tunnerer.Core.Thermal;

/// <summary>
/// Book-keeping for energy that crosses the boundary of the simulated thermal system.
/// <para>
/// Energy is <c>temperature * heat capacity</c>. Everything inside the system (terrain field, air field,
/// tank heat, base reactor heat) only ever moves through exact transfers. Energy that is created or
/// destroyed has to be recorded here, so that for any tick:
/// <c>TotalEnergy(now) == TotalEnergy(start) + Injected - Sunk</c>.
/// </para>
/// Positive <see cref="Injected"/> values add energy to the system (explosions, torch, tank actions);
/// positive <c>Sunk*</c> values remove it. Negative values mean the opposite (energy created by a sink).
/// </summary>
public sealed class ThermalLedger
{
    /// <summary>Energy added by external sources (explosions, torch, tank action heat, test injections).</summary>
    public double Injected { get; private set; }

    /// <summary>Energy deleted by cells pinned to a fixed temperature (base barrier and core cells).</summary>
    public double SunkFixedTemperature { get; private set; }

    /// <summary>Energy removed by the optional global dirt cooling.</summary>
    public double SunkArtificialCooling { get; private set; }

    /// <summary>Energy removed by base cooling of a tank standing in a base and by the base-centre sink.</summary>
    public double SunkBase { get; private set; }

    /// <summary>Energy removed from tanks by cooling pickups; negative if a tank was clamped up to zero heat.</summary>
    public double SunkTank { get; private set; }

    public double TotalSunk =>
        SunkFixedTemperature + SunkArtificialCooling + SunkBase + SunkTank;

    /// <summary><c>Injected - TotalSunk</c>: the net energy the system should have gained since the last reset.</summary>
    public double NetExternal => Injected - TotalSunk;

    public void RecordInjected(double energy) => Injected += energy;
    public void RecordSunkFixedTemperature(double energy) => SunkFixedTemperature += energy;
    public void RecordSunkArtificialCooling(double energy) => SunkArtificialCooling += energy;
    public void RecordSunkBase(double energy) => SunkBase += energy;
    public void RecordSunkTank(double energy) => SunkTank += energy;

    public void Reset()
    {
        Injected = 0;
        SunkFixedTemperature = 0;
        SunkArtificialCooling = 0;
        SunkBase = 0;
        SunkTank = 0;
    }
}
