namespace Emberward.Core.Entities.Machines;

using System.Globalization;
using Emberward.Core.Config;
using Emberward.Core.Match;

/// <summary>
/// What a machine can be upgraded along (DESIGN.md §4.9). A machine takes one path, fixed by its first purchase, up to
/// <see cref="MachineUpgrades.MaxLevel"/> levels. Which paths a machine offers is <see cref="MachineBehavior.UpgradePaths"/>.
/// </summary>
public enum MachineUpgradePath
{
    /// <summary>A relay pumps more heat (so its base makes more charge) and runs hotter: more idle draw, as heat.</summary>
    Tap,

    /// <summary>A relay sees further. Presentation only: it changes no simulation result.</summary>
    Sensor,

    /// <summary>On the energy network, a relay passes more charge on to the machines behind it, and holds more.</summary>
    Conduit,
}

/// <summary>Why a machine upgrade press bought nothing.</summary>
public enum MachineUpgradeRefusal
{
    None,

    /// <summary>No own machine with upgrades stands within reach of the tank.</summary>
    OutOfReach,

    /// <summary>The machine is at its top level.</summary>
    Maxed,

    /// <summary>The machine already took another path, which its first purchase fixed.</summary>
    OtherPath,

    /// <summary>The key names no path of the machine, or a path this match does not run (Conduit without the energy network).</summary>
    Unavailable,

    /// <summary>The base store cannot pay for the next level.</summary>
    NotEnoughOre,
}

/// <summary>A machine's upgrade: the path it took (null before its first purchase) and its level on it.</summary>
public readonly record struct MachineUpgradeLevel(MachineUpgradePath? Path, int Level)
{
    public bool IsMaxed => Level >= MachineUpgrades.MaxLevel;

    /// <summary>The level of <paramref name="path"/>: <see cref="Level"/> when it is the path taken, else 0.</summary>
    public int LevelOf(MachineUpgradePath path) => Path == path ? Level : 0;
}

/// <summary>
/// What the tank could buy for the machine nearest to it (<see cref="World.MachineUpgradeOfferFor"/>): the machine, its
/// upgrade so far, the next level's price and whether the base store can pay it.
/// </summary>
public readonly record struct MachineUpgradeOffer(Machine Machine, MachineUpgradeLevel Upgrade, int NextCost, bool CanAfford)
{
    public bool IsMaxed => Upgrade.IsMaxed;

    /// <summary>Whether <paramref name="path"/> can still be bought here: not maxed, and no other path taken.</summary>
    public bool IsOpen(MachineUpgradePath path) => !IsMaxed && (Upgrade.Path is null || Upgrade.Path == path);
}

/// <summary>
/// What each path and level is worth: the one place the numbers of <see cref="Tweaks.MachineUpgrades"/> become effects,
/// read by the simulation (<see cref="MachineUpgradeSystem"/>) and described for the HUD (<see cref="Describe"/>).
/// </summary>
public static class MachineUpgrades
{
    public static int MaxLevel => Tweaks.MachineUpgrades.MaxLevel;

    /// <summary>What level <paramref name="level"/> + 1 costs, in ore from the base store; 0 past the top.</summary>
    public static int CostOfLevel(int level) =>
        level >= 0 && level < Tweaks.MachineUpgrades.LevelCosts.Length ? Tweaks.MachineUpgrades.LevelCosts[level] : 0;

    /// <summary>Whether a match with <paramref name="rules"/> runs <paramref name="path"/>: Conduit needs the energy network.</summary>
    public static bool IsAvailable(MachineUpgradePath path, MatchRules? rules) =>
        path != MachineUpgradePath.Conduit || rules is { EnergyNetwork: true };

    /// <summary>A relay's pump rate per tick at Tap level <paramref name="tapLevel"/>.</summary>
    public static double PumpPerTick(int tapLevel) =>
        Tweaks.Machine.RelayHeatPumpPerTick * (1.0 + Tweaks.MachineUpgrades.TapPumpPerLevel * tapLevel);

    /// <summary>The capacitor of a machine with <paramref name="behavior"/> and <paramref name="upgrade"/>: capacity and idle draw.</summary>
    public static (double Capacity, double IdleDrawPerTick) CapacitorOf(MachineBehavior behavior, MachineUpgradeLevel upgrade)
    {
        int conduit = Math.Clamp(upgrade.LevelOf(MachineUpgradePath.Conduit), 0, Tweaks.MachineUpgrades.ConduitCapacityScale.Length - 1);
        return (behavior.Capacitor.Capacity * Tweaks.MachineUpgrades.ConduitCapacityScale[conduit],
            behavior.Capacitor.IdleDrawPerTick + Tweaks.MachineUpgrades.TapIdleDrawPerLevel * upgrade.LevelOf(MachineUpgradePath.Tap));
    }

    /// <summary>
    /// The multiple of <c>Throughput.NodeLimitPerTick</c> a machine with <paramref name="upgrade"/> passes on (its Conduit
    /// level); <c>Throughput.CeilingPerTick</c> caps the result.
    /// </summary>
    public static double ThroughputScaleOf(MachineUpgradeLevel upgrade) =>
        Tweaks.Throughput.ConduitScale[Math.Clamp(upgrade.LevelOf(MachineUpgradePath.Conduit), 0, Tweaks.Throughput.ConduitScale.Length - 1)];

    /// <summary>Extra cells of sight at Sensor level <paramref name="sensorLevel"/>.</summary>
    public static int SightBonus(int sensorLevel) => Tweaks.MachineUpgrades.SensorCellsPerLevel * sensorLevel;

    /// <summary>A path's name and what each level of it does, for the HUD.</summary>
    public static (string Name, string Effect) Describe(MachineUpgradePath path) => path switch
    {
        MachineUpgradePath.Tap => ("Tap", string.Create(CultureInfo.InvariantCulture,
            $"+{Tweaks.MachineUpgrades.TapPumpPerLevel * 100:0}% heat pumped (more charge), runs hotter")),
        MachineUpgradePath.Sensor => ("Sensor", string.Create(CultureInfo.InvariantCulture,
            $"+{Tweaks.MachineUpgrades.SensorCellsPerLevel} cells of sight")),
        MachineUpgradePath.Conduit => ("Conduit", string.Create(CultureInfo.InvariantCulture,
            $"passes on x{Tweaks.Throughput.ConduitScale[1]:0.#}, then x{Tweaks.Throughput.ConduitScale[2]:0.#}, and holds more")),
        _ => (path.ToString(), ""),
    };
}
