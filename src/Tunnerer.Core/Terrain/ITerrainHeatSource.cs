namespace Tunnerer.Core.Terrain;

using Tunnerer.Core.Types;

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
    float SampleAverageAirTemperature(Position center, int radius);
    int CountCellsInRadiusArea(Position center, int radius);
    int AddHeatTotalInRadiusArea(Position center, int radius, int totalAmount);
    int AddAirHeatTotalInRadiusArea(Position center, int radius, int totalAmount);
    double SumTotalThermalEnergy();

    // --- Energy-exact exchange (every unit moved is metered in the ledger) ---
    Thermal.ThermalLedger Ledger { get; }
    double SumHeatCapacityInRadiusArea(Position center, int radius, TerrainGrid.ThermalField field);
    double AddEnergyInRadiusArea(Position center, int radius, double energy, TerrainGrid.ThermalField field);
    double ExtractEnergyInRadiusArea(Position center, int radius, double maxEnergy, TerrainGrid.ThermalField field);
    double ExtractableHeatTemperature(Position center, int radius, TerrainGrid.ThermalField field);
    double SinkCellToAmbient(Position pos, TerrainGrid.ThermalField field);

    bool TryGetHeatDirtyRect(out Rect dirtyRect);
    void ClearHeatDirtyRect();
}
