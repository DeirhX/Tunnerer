namespace Emberward.Core.Network;

using Emberward.Core.Entities.Machines;
using Emberward.Core.Terrain;

public sealed partial class EnergyNetwork
{
    /// <summary>
    /// Gives <paramref name="machine"/>'s capacitor a new size and idle draw (a machine upgrade,
    /// <see cref="MachineUpgradeSystem"/>). The capacitor is settled under its old numbers first, so the change applies
    /// from the next tick, and its island's idle sum is taken again. A fed capacitor with new room starts filling. A
    /// smaller size than the charge held is not asked for, but would keep the charge until it is spent.
    /// </summary>
    public void Resize(Machine machine, double capacity, double idleDrawPerTick, ITerrainAccess terrain)
    {
        if (Find(machine) is not Capacitor capacitor)
            return;
        Settle(capacitor, terrain);
        machine.ChargeCapacity = capacity;
        capacitor.IdleDrawPerTick = idleDrawPerTick;
        if (capacitor.Island is PowerIsland island)
            SumIdle(island, TierOf(capacitor));
        if (IsFed(capacitor))
            StartFilling(capacitor);
    }
}
