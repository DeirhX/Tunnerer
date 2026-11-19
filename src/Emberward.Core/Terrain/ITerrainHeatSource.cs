namespace Emberward.Core.Terrain;

using Emberward.Core.Types;

/// <summary>
/// Thermal read/write access to the terrain heat field.
/// Consumers that need temperature sampling, heat injection, or cooldown depend on this.
/// </summary>
public interface ITerrainHeatSource
{
    float GetHeatTemperature(int offset);
    float GetHeatTemperature(Position pos);
    float GetAirTemperature(int offset);
    float GetAirTemperature(Position pos);

    void AddHeat(Position pos, int amount);
    void AddHeatRadius(Position center, int amount, int radius);
    void CoolDown();

    float SampleAverageHeat(Position center, int radius);
    int CountCellsInRadiusArea(Position center, int radius);
    int AddHeatTotalInRadiusArea(Position center, int radius, int totalAmount);
    int AddAirHeatTotalInRadiusArea(Position center, int radius, int totalAmount);
    double SumTotalThermalEnergy();

    // --- Energy-exact exchange (every unit moved is metered in the ledger) ---
    Thermal.ThermalLedger Ledger { get; }
    TankHeatContact SampleTankContact(Position center, int radius);
    void ApplyTankExchange(Position center, int radius, in TankHeatContact contact, double toSolid, double toAir);
    double SumHeatCapacityInRadiusArea(Position center, int radius, TerrainGrid.ThermalField field);
    double AddEnergyInRadiusArea(Position center, int radius, double energy, TerrainGrid.ThermalField field);
    double ExtractEnergyInRadiusArea(Position center, int radius, double maxEnergy, TerrainGrid.ThermalField field);
    double ExtractableHeatTemperature(Position center, int radius, TerrainGrid.ThermalField field);
    (double Energy, double Temperature) SampleHeatAround(Position center, int radius, int excludeRadius, float floor);
    double TakeHeatAround(Position center, int radius, int excludeRadius, float floor, double fraction);
    long QuietHeatStamp(Position center, int radius);

    bool TryGetHeatDirtyRect(out Rect dirtyRect);
    void ClearHeatDirtyRect();
}
