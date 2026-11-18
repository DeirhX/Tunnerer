namespace Tunnerer.Core.Match;

using Tunnerer.Core.Snapshots;
using Tunnerer.Core.Config;
using Tunnerer.Core.Types;

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

    /// <summary>Level setup seeds emberite deposits (<see cref="LevelGen.OreDeposits"/>).</summary>
    public bool SeedOre { get; init; }

    /// <summary>
    /// The first base to tap the core wins the match (a race, DESIGN.md §4.8). Off, the core is a goal that does not
    /// end the match. Has no effect without hot rock, since there is then no core.
    /// </summary>
    public bool CoreTapWins { get; init; }

    /// <summary>A normal match: hot and frost pockets, ore, regrowth; the core does not end it.</summary>
    public static MatchRules Standard { get; } = new()
    {
        HotRockPockets = Tweaks.World.HotRockPocketCount,
        FrostRockPockets = Tweaks.World.FrostRockPocketCount,
        SeedOre = true,
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
        return hash;
    }
}
