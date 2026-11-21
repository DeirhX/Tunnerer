namespace Emberward.Desktop.Presentation;

using Emberward.Core.Match;
using Emberward.Core.Entities.Machines;

/// <summary>
/// How the clients show and control one machine type: its names, the key that builds it, its slot in the build
/// panel and the hint for planting it. The desktop and the browser share it; the desktop's keyboard reads the key by
/// its label (<c>KeyboardController</c>). What it costs and does lives in Core (<see cref="MachineBehaviors"/>);
/// how it is drawn in <see cref="Rendering.Entities.MachineSprites"/> and what building it sounds like in
/// <see cref="Audio.StepEventSounds"/>, which the browser client shares.
/// </summary>
/// <param name="Name">Lower-case name used in sentences ("Build a relay").</param>
/// <param name="SlotLabel">Upper-case label on its build slot.</param>
/// <param name="KeyLabel">The key bound to the machine's <see cref="BuildKey"/>, as written on screen and as SDL names it.</param>
/// <param name="BuildSlot">Slot in the build panel, in row order from the top left.</param>
/// <param name="PlantHint">What planting it here does, after "key:" in the network panel's hint.</param>
public sealed record MachinePresentation(
    MachineType Type,
    string Name,
    string SlotLabel,
    string KeyLabel,
    int BuildSlot,
    string PlantHint)
{
    public MachineBehavior Behavior => MachineBehaviors.Get(Type);

    /// <summary>The tank's build input this machine is built with.</summary>
    public BuildKey BuildKey => Behavior.Key;

    public int OreCost => Behavior.OreCost;
}

public static class MachinePresentations
{
    public static readonly MachinePresentation Relay = new(
        MachineType.Relay, "relay", "RELAY", "E", BuildSlot: 0,
        "plant relay here - it will link (green line)");

    public static readonly MachinePresentation Harvester = new(
        MachineType.Harvester, "harvester", "HARVEST", "R", BuildSlot: 1,
        "plant harvester here - in your network it mines nearby ore into your base store");

    public static readonly MachinePresentation Generator = new(
        MachineType.Generator, "generator", "GEN", "Y", BuildSlot: 2,
        "plant generator here - use nearby heat to supply the local network");

    public static readonly MachinePresentation Turret = new(
        MachineType.Turret, "turret", "TURRET", "U", BuildSlot: 3,
        "plant turret here - in your network it fires at enemy tanks in range");

    public static readonly MachinePresentation Gatling = new(
        MachineType.Gatling, "gatling", "GATLING", "T", BuildSlot: 4,
        "plant gatling here - in your network it sprays emberites in range");

    /// <summary>Every machine's presentation, indexed by <see cref="MachineType"/>.</summary>
    public static readonly IReadOnlyList<MachinePresentation> All = BuildTable(Relay, Harvester, Generator, Turret, Gatling);

    /// <summary>Every machine's presentation in build-panel row order.</summary>
    public static readonly IReadOnlyList<MachinePresentation> InSlotOrder = All.OrderBy(p => p.BuildSlot).ToArray();

    public static IEnumerable<MachinePresentation> Available(MatchRules rules) =>
        InSlotOrder.Where(p => p.Behavior.IsAvailable(rules));

    public static MachinePresentation Get(MachineType type) =>
        (uint)type < (uint)All.Count ? All[(int)type] : throw new ArgumentOutOfRangeException(nameof(type));

    private static MachinePresentation[] BuildTable(params MachinePresentation[] entries)
    {
        var table = new MachinePresentation[Enum.GetValues<MachineType>().Length];
        foreach (var entry in entries)
            table[(int)entry.Type] = entry;
        if (Array.IndexOf(table, null) >= 0)
            throw new InvalidOperationException("Every MachineType needs a MachinePresentation.");
        return table;
    }
}
