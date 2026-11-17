namespace Tunnerer.Core.Entities.Tanks;

using Tunnerer.Core.Entities.Machines;

/// <summary>Which stock pays for a machine.</summary>
public enum OrePayer
{
    /// <summary>Nothing in reach can pay.</summary>
    None,

    /// <summary>The tank's own hold.</summary>
    Hold,

    /// <summary>The base's store, reached through the own network (or docked).</summary>
    BaseStore,
}

/// <summary>Why a build key press did nothing.</summary>
public enum BuildRefusal
{
    None,

    /// <summary>Neither the hold nor (in the own network) the base store has the machine's ore cost.</summary>
    NotEnoughOre,

    /// <summary>The carried machine needs a link and would not get one where the tank stands.</summary>
    WillNotLink,
}

/// <summary>
/// What building a machine would take where the tank is (<see cref="Tank.QuoteBuild"/>): its ore cost, which stock
/// pays, and how much that stock holds.
/// </summary>
/// <param name="Stock">
/// Ore in the paying stock; when nothing can pay, the most any stock in reach holds (the hold, and the base store in
/// the own network), which is how far the tank is from affording it.
/// </param>
/// <param name="InNetwork">The base store is in reach: the tank is docked or in its own network's coverage.</param>
public readonly record struct BuildQuote(MachineType Type, int Cost, OrePayer Payer, int Stock, bool InNetwork)
{
    public bool CanAfford => Payer != OrePayer.None;

    public BuildRefusal Refusal => CanAfford ? BuildRefusal.None : BuildRefusal.NotEnoughOre;
}
