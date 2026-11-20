namespace Emberward.Core.Thermal;

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
    /// <summary>Energy added by external sources (starting heat stock, explosions, torch, tank actions).</summary>
    public double Injected { get; private set; }

    /// <summary>Energy taken by fixed temperatures in custom material settings. Standard materials are all finite.</summary>
    public double SunkFixedTemperature { get; private set; }

    /// <summary>
    /// Energy the ground gave up to the deep ground and open air gave up to the surface (the <c>Loss</c> of each
    /// thermal material); negative where cold ground or air warmed back toward ambient.
    /// </summary>
    public double SunkAmbientLoss { get; private set; }

    /// <summary>Energy vented by the base radiators.</summary>
    public double SunkBase { get; private set; }

    /// <summary>Energy a wrecked tank still held when it respawned cold (<c>TankThermals.ResetForSpawn</c>).</summary>
    public double SunkTank { get; private set; }

    /// <summary>
    /// Net heat turned into charge: positive when a base engine or local generator converts heat to charge, reduced when a machine
    /// spends charge as work heat. Charge is not thermal energy, so the thermal balance treats it as leaving the
    /// system; thermal energy plus all stored charge balances against <c>Injected - TotalSunk</c>.
    /// </summary>
    public double ConvertedToCharge { get; private set; }

    /// <summary>Charge released from carried ore to restart a dark base, also counted as injected energy.</summary>
    public double RestartOreCharge { get; private set; }

    public void RecordFromRestartOre(double charge)
    {
        RestartOreCharge += charge;
        RecordInjected(charge);
        RecordToCharge(charge);
    }

    public double TotalSunk =>
        SunkFixedTemperature + SunkAmbientLoss + SunkBase + SunkTank;

    /// <summary>
    /// <c>Injected - TotalSunk - ConvertedToCharge</c>: the net thermal energy the system should have gained since the
    /// last reset.
    /// </summary>
    public double NetExternal => Injected - TotalSunk - ConvertedToCharge;

    public void RecordInjected(double energy) => Injected += energy;
    public void RecordToCharge(double energy) => ConvertedToCharge += energy;
    public void RecordFromCharge(double energy) => ConvertedToCharge -= energy;
    public void RecordSunkFixedTemperature(double energy) => SunkFixedTemperature += energy;
    public void RecordSunkAmbientLoss(double energy) => SunkAmbientLoss += energy;
    public void RecordSunkBase(double energy) => SunkBase += energy;
    public void RecordSunkTank(double energy) => SunkTank += energy;

    public void Reset()
    {
        Injected = 0;
        SunkFixedTemperature = 0;
        SunkAmbientLoss = 0;
        SunkBase = 0;
        SunkTank = 0;
        ConvertedToCharge = 0;
        RestartOreCharge = 0;
    }
}
