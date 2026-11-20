namespace Emberward.Core.Network;

using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Terrain;

public sealed partial class EnergyNetwork
{
    /// <summary>Converts heat from outside the exhaust area, storing charge and returning the waste heat locally.</summary>
    internal void Generate(Machine machine, ITerrainAccess terrain)
    {
        machine.LastChargeMade = 0.0;
        if (Find(machine) is not { Island: { } island } producer)
            return;

        var home = island.Base;
        double baseRoom = home == null ? 0.0 : Math.Max(0.0, home.ChargeCapacity - home.Charge);
        double room = baseRoom + _settings.GeneratorReserveCapacity - producer.Reserve;
        if (room <= 0.0)
            return;

        var position = machine.Position;
        int intake = _settings.GeneratorIntakeRadius, exhaust = _settings.GeneratorExhaustRadius;
        long stamp = terrain.QuietHeatStamp(position, intake);
        if (stamp >= 0 && producer.QuietGeneratorStamp == stamp)
            return;
        // Use the same ambient floor as a relay. Cold rock can improve cooling but is not a source of heat.
        float cold = terrain.SampleAverageHeat(position, exhaust) * 255f;
        float floor = Math.Max(cold, _settings.ThermalAmbientTemperature) + _settings.GeneratorMinTemperatureDifference;
        var (available, hot) = terrain.SampleHeatAround(position, intake, exhaust, floor);
        if (available <= 0.0)
        {
            producer.QuietGeneratorStamp = stamp;
            return;
        }
        producer.QuietGeneratorStamp = -1;

        double difference = hot - cold;
        double eta = Math.Min(_settings.GeneratorMaxEfficiency, difference / (difference + _settings.GeneratorTemperatureScale));
        double cap = Math.Min(room, _settings.GeneratorMaxChargePerTick);
        double wanted = Math.Min(Math.Min(available, _settings.GeneratorMaxHeatPerTick), cap / eta);
        double taken = terrain.TakeHeatAround(position, intake, exhaust, floor, wanted / available);
        double charge = Math.Min(cap, eta * taken);
        if (charge <= 0.0)
            return;
        double atBase = home?.StoreCharge(charge) ?? 0.0;
        producer.Reserve += charge - atBase;
        terrain.AddEnergyInRadiusArea(position, exhaust, taken - charge, TerrainGrid.ThermalField.Terrain);
        terrain.Ledger.RecordToCharge(charge);
        machine.LastChargeMade = charge;
        producer.Production.Add(_paidThrough + 1, charge, 0.0);
        island.GeneratorProduction.Add(_paidThrough + 1, charge, 0.0);
    }
}
