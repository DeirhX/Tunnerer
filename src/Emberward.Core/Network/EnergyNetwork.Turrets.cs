namespace Emberward.Core.Network;

using Emberward.Core.Entities.Machines;

public sealed partial class EnergyNetwork
{
    /// <summary>
    /// Whether <paramref name="machine"/> belongs to an island (its base's, or a local generator's), fed or starved; false
    /// for a machine cut off from every island or not on the network. A turret fires only while this holds.
    /// </summary>
    public bool IsOnIsland(Machine machine) => Find(machine) is { Island: not null };
}
