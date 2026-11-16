namespace Tunnerer.Core;

using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Machines;

/// <summary>
/// One base's network as of the last tick (see <see cref="World.GetNetworkStatus"/>): its planted harvesters by
/// state, its planted relays and the heat they bring in, and how the base's heat store and radiator stand.
/// </summary>
/// <param name="Harvesters">Planted, live harvesters of the base.</param>
/// <param name="HarvestersReturning">Harvesters heading to a pylon to recharge.</param>
/// <param name="HarvestersOutOfCharge">Harvesters whose battery cannot pay for another harvest.</param>
/// <param name="HarvestersTooHot">Harvesters stalled because the ground around them is too hot.</param>
/// <param name="Relays">Planted, live relays of the base.</param>
/// <param name="PoweredRelays">Relays the base's link network reaches.</param>
/// <param name="PumpingRelays">Relays that moved a noticeable amount of heat in their last action.</param>
/// <param name="RelayInflow">Heat the relays moved into the base during the last tick.</param>
/// <param name="RadiatorInflow">The part of <paramref name="RelayInflow"/> the engine did not turn into charge.</param>
/// <param name="VentCapacity">Heat the base radiator vents per tick.</param>
/// <param name="HeatStoreFill">How full the base reactor (heat store) is, 0 to 1.</param>
public readonly record struct NetworkStatus(
    int Harvesters,
    int HarvestersReturning,
    int HarvestersOutOfCharge,
    int HarvestersTooHot,
    int Relays,
    int PoweredRelays,
    int PumpingRelays,
    double RelayInflow,
    double RadiatorInflow,
    double VentCapacity,
    double HeatStoreFill)
{
    /// <summary>Harvesters that are none of returning, out of charge or too hot.</summary>
    public int HarvestersWorking => Harvesters - HarvestersTooHot - HarvestersReturning - HarvestersOutOfCharge;

    /// <summary>The heat-store gauge: Warn while it fills, Bad once it is (nearly) full.</summary>
    public StatusLevel HeatStoreLevel => TankBase.HeatStoreLevelOf(HeatStoreFill);

    /// <summary>
    /// The heat store is full while relays are linked: relays and docking cool less until the radiator catches up.
    /// </summary>
    public bool IsHeatStoreFullWithRelays => HeatStoreLevel == StatusLevel.Bad && PoweredRelays > 0;

    /// <summary>A relay counts as pumping once its last action moved more than this much heat.</summary>
    public const double PumpingThreshold = 1.0;

    internal static NetworkStatus Collect(TankBase tankBase, IReadOnlyList<Machine> machines)
    {
        int harvesters = 0, returning = 0, outOfCharge = 0, tooHot = 0;
        int relays = 0, powered = 0, pumping = 0;
        double inflow = 0.0;
        foreach (var machine in machines)
        {
            if (!machine.IsAlive || machine.State != MachineState.Planted || machine.OwnerBase != tankBase)
                continue;
            if (machine.Behavior.IsMiner)
            {
                harvesters++;
                switch (MachineBehaviors.HarvesterStateOf(machine))
                {
                    case HarvesterState.Returning: returning++; break;
                    case HarvesterState.OutOfCharge: outOfCharge++; break;
                    case HarvesterState.TooHot: tooHot++; break;
                }
            }
            else if (machine.Behavior.IsHeatPump)
            {
                relays++;
                if (machine.LinkPoint?.IsPowered == true)
                    powered++;
                if (machine.LastPumpedEnergy > PumpingThreshold)
                    pumping++;
                inflow += machine.LastPumpedEnergy;
            }
        }

        return new NetworkStatus(
            harvesters, returning, outOfCharge, tooHot,
            relays, powered, pumping,
            RelayInflow: inflow,
            RadiatorInflow: inflow - tankBase.EngineOutputPerTick,
            VentCapacity: TankBase.VentCapacityPerTick,
            HeatStoreFill: tankBase.HeatStoreFill);
    }
}
