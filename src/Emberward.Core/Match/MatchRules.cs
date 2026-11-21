namespace Emberward.Core.Match;

using Emberward.Core.Snapshots;
using Emberward.Core.Config;
using Emberward.Core.Types;

/// <summary>
/// Which of the game's optional systems a world runs with, fixed when the world is built. This is the one place a
/// system is switched on or off for a match: <see cref="World"/> reads it, and a system whose flag is off costs
/// nothing. A system that can be switched off gets a flag here, not a constructor argument or a
/// property set after construction.
/// </summary>
/// <remarks>
/// A new <see cref="MatchRules"/> has every optional system off except regrowth, which is what most tests want;
/// <see cref="Standard"/> is a normal match. The rules are part of <see cref="WorldStateHash.ComputeReady"/>, so two
/// machines that would build the same map under different rules refuse to start a network match together.
/// </remarks>
[NotSaved("fixed for the match: the world is built with the same rules")]
public sealed record MatchRules
{
    /// <summary>Dirt grows back into open ground (<see cref="Regrowth.TerrainRegrowth"/>).</summary>
    public bool TerrainRegrowth { get; init; } = true;

    /// <summary>
    /// How many hot rock pockets level setup places. With at least one there is also a hot core at the map centre,
    /// and so a sector goal (<see cref="SectorGoal"/>).
    /// </summary>
    public int HotRockPockets
    {
        get;
        init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(HotRockPockets), value, "Cannot be negative.");
    }

    /// <summary>How many frost pockets level setup places.</summary>
    public int FrostRockPockets
    {
        get;
        init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(FrostRockPockets), value, "Cannot be negative.");
    }

    /// <summary>Level setup seeds pyrite deposits (<see cref="LevelGen.OreDeposits"/>).</summary>
    public bool SeedOre { get; init; }

    /// <summary>
    /// The first base to tap the core wins the match (a race, DESIGN.md §4.8). Off, the core is a goal that does not
    /// end the match. Has no effect without hot rock, since there is then no core.
    /// </summary>
    public bool CoreTapWins { get; init; }

    /// <summary>
    /// Every machine has a capacitor and islands with a base or generator feed their machines by priority tier (the energy network,
    /// DESIGN.md §4.9, <see cref="Network.EnergyNetwork"/>). Off, no machine has a capacitor and a harvester pays for
    /// its work straight from its base's store (§4.5).
    /// </summary>
    public bool EnergyNetwork { get; init; }

    /// <summary>
    /// A tank can buy upgrades for its own machines, next to them, from its base's ore store (DESIGN.md §4.9,
    /// <see cref="Entities.Machines.MachineUpgradeSystem"/>). Off, machines keep their base numbers.
    /// </summary>
    public bool MachineUpgrades { get; init; }

    /// <summary>
    /// The off-network clock is the tank's reserve (DESIGN.md §4.9, <see cref="Entities.Tanks.TankReserveSystem"/>):
    /// the recall key burns carried pyrite to buy it back, and running out goes through warning, degraded and
    /// stranded before heat death. Off, the clock runs out straight into overheating.
    /// </summary>
    public bool TankReserve { get; init; }

    /// <summary>
    /// Emberites nest in pyrite pockets and lumps, guard the ore and hunt tanks that run hot (DESIGN.md §6.2,
    /// <see cref="Entities.Emberites.EmberiteSwarm"/>). Needs <see cref="SeedOre"/>: without deposits there are no nests. Off, the
    /// world has no swarm and no harvester refuses ore.
    /// </summary>
    public bool Emberites { get; init; }

    /// <summary>A normal match: hot and frost pockets, ore, regrowth, and the energy rules (the energy network with its gate
    /// shields, machine upgrades and the tank's reserve); the core does not end it.
    /// </summary>
    public static MatchRules Standard { get; } = new()
    {
        HotRockPockets = Tweaks.World.HotRockPocketCount,
        FrostRockPockets = Tweaks.World.FrostRockPocketCount,
        SeedOre = true,
        EnergyNetwork = true,
        MachineUpgrades = true,
        TankReserve = true,
    };

    /// <summary>The rules as one number, the same on every machine and run (unlike <see cref="GetHashCode"/>).</summary>
    public ulong Fingerprint()
    {
        ulong hash = Fnv1a.Offset;
        hash = Fnv1a.Mix(hash, TerrainRegrowth ? 1u : 0u);
        hash = Fnv1a.Mix(hash, (uint)HotRockPockets);
        hash = Fnv1a.Mix(hash, (uint)FrostRockPockets);
        hash = Fnv1a.Mix(hash, SeedOre ? 1u : 0u);
        hash = Fnv1a.Mix(hash, CoreTapWins ? 1u : 0u);
        // Version of the base rules: a base falls when its core does, behind walls and gates (the keep).
        hash = Fnv1a.Mix(hash, 9u);
        // Version of the energy rules: raised with each machine they add (turrets, the gatling) and with the gate shield.
        if (EnergyNetwork)
            hash = Fnv1a.Mix(hash, 0xE4ECu);
        if (MachineUpgrades)
            hash = Fnv1a.Mix(hash, 0x4D55u);
        if (TankReserve)
            hash = Fnv1a.Mix(hash, 0x5253u);
        // Version of the emberite rules: raised when what creatures do changes (a dying one drops its nest's ore).
        if (Emberites)
            hash = Fnv1a.Mix(hash, 0x4D50u);
        return hash;
    }
}
