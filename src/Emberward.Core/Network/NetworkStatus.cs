namespace Emberward.Core.Network;

using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Bases;

/// <summary>
/// One base's network as of the last tick (see <see cref="World.GetNetworkStatus"/>): its planted harvesters by
/// state, its planted relays and the heat they bring in, and how the base's heat store and radiator stand.
/// </summary>
/// <param name="Harvesters">Planted, live harvesters of the base.</param>
/// <param name="HarvestersOutOfCharge">Harvesters that cannot pay for another harvest.</param>
/// <param name="HarvestersOffNetwork">Harvesters outside the network's coverage, which do not mine.</param>
/// <param name="HarvestersTooHot">Harvesters stalled because the ground around them is too hot.</param>
/// <param name="HarvestersNoOre">Harvesters with no ore in their work area that they can reach.</param>
/// <param name="HarvestersCoreOnly">Harvesters that can reach no plain ore beside a lump core, waiting for the tank's torch.</param>
/// <param name="HarvestersStoreFull">Harvesters with ore to work, waiting because the base store is full.</param>
/// <param name="Relays">Planted, live relays of the base.</param>
/// <param name="PoweredRelays">Relays the base's link network reaches.</param>
/// <param name="PumpingRelays">Relays that moved a noticeable amount of heat in their last action.</param>
/// <param name="RelayInflow">Heat the relays moved into the base during the last tick.</param>
/// <param name="RadiatorInflow">The part of <paramref name="RelayInflow"/> the engine did not turn into charge.</param>
/// <param name="VentCapacity">Heat the base radiator vents per tick.</param>
/// <param name="HeatStoreFill">How full the base reactor (heat store) is, 0 to 1.</param>
/// <param name="HarvestersGuarded">Harvesters whose reachable ore is guarded by a nest with creatures left.</param>
public readonly record struct NetworkStatus(
    int Harvesters,
    int HarvestersOutOfCharge,
    int HarvestersOffNetwork,
    int HarvestersTooHot,
    int HarvestersNoOre,
    int HarvestersCoreOnly,
    int HarvestersStoreFull,
    int Relays,
    int PoweredRelays,
    int PumpingRelays,
    double RelayInflow,
    double RadiatorInflow,
    double VentCapacity,
    double HeatStoreFill,
    int HarvestersGuarded = 0)
{
    /// <summary>
    /// Harvesters that are none of out of charge, off the network, too hot, out of ore, left a core or
    /// held up by a full store.
    /// </summary>
    public int HarvestersWorking =>
        Harvesters - HarvestersTooHot - HarvestersOutOfCharge - HarvestersOffNetwork -
        HarvestersNoOre - HarvestersCoreOnly - HarvestersStoreFull - HarvestersGuarded;

    /// <summary>The heat-store gauge: Warn while it fills, Bad once it is (nearly) full.</summary>
    public StatusLevel HeatStoreLevel => TankBase.HeatStoreLevelOf(HeatStoreFill);

    /// <summary>
    /// The heat store is full while relays are linked: relays and docking cool less until the radiator catches up.
    /// </summary>
    public bool IsHeatStoreFullWithRelays => HeatStoreLevel == StatusLevel.Bad && PoweredRelays > 0;

    /// <summary>A relay counts as pumping once its last action moved more than this much heat.</summary>
    public const double PumpingThreshold = 1.0;

    internal static NetworkStatus Collect(TankBase tankBase, IReadOnlyList<Machine> machines, bool baseDark = false)
    {
        int harvesters = 0, outOfCharge = 0, offNetwork = 0, tooHot = 0, noOre = 0, coreOnly = 0, storeFull = 0, guarded = 0;
        int relays = 0, powered = 0, pumping = 0;
        double inflow = 0.0;
        foreach (var machine in machines)
        {
            if (!machine.StandsFor(tankBase))
                continue;
            if (machine.Behavior.IsMiner)
            {
                harvesters++;
                switch (MachineBehaviors.HarvesterStateOf(machine, baseDark))
                {
                    case HarvesterState.OutOfCharge: outOfCharge++; break;
                    case HarvesterState.OffNetwork: offNetwork++; break;
                    case HarvesterState.TooHot: tooHot++; break;
                    case HarvesterState.NoOre: noOre++; break;
                    case HarvesterState.CoreOnly: coreOnly++; break;
                    case HarvesterState.StoreFull: storeFull++; break;
                    case HarvesterState.Guarded: guarded++; break;
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
            harvesters, outOfCharge, offNetwork, tooHot, noOre, coreOnly, storeFull,
            relays, powered, pumping,
            RelayInflow: inflow,
            RadiatorInflow: inflow - tankBase.EngineOutputPerTick,
            VentCapacity: tankBase.VentCapacityPerTick,
            HeatStoreFill: tankBase.HeatStoreFill,
            HarvestersGuarded: guarded);
    }
}
