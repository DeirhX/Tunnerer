namespace Tunnerer.Desktop.Presentation;

using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Machines;
using static SDL3.SDL;

/// <summary>
/// How the desktop client shows and controls one machine type: its names, the keyboard key that builds it, its slot
/// in the build panel and the hint for planting it. What it costs and does lives in Core (<see cref="MachineBehaviors"/>);
/// how it is drawn in <see cref="Rendering.Entities.MachineSprites"/> and what building it sounds like in
/// <see cref="Audio.StepEventSounds"/>, which the browser client shares.
/// </summary>
/// <param name="Name">Lower-case name used in sentences ("Build a relay").</param>
/// <param name="SlotLabel">Upper-case label on its build slot.</param>
/// <param name="Key">Keyboard key bound to the machine's <see cref="BuildKey"/>.</param>
/// <param name="KeyLabel">How <paramref name="Key"/> is written on screen.</param>
/// <param name="BuildSlot">Column of its slot in the build panel, from the left.</param>
/// <param name="PlantHint">What planting it here does, after "key:" in the network panel's hint.</param>
public sealed record MachinePresentation(
    MachineType Type,
    string Name,
    string SlotLabel,
    Scancode Key,
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
        MachineType.Relay, "relay", "RELAY", Scancode.E, "E", BuildSlot: 0,
        "plant relay here - it will link (green line)");

    public static readonly MachinePresentation Harvester = new(
        MachineType.Harvester, "harvester", "HARVEST", Scancode.R, "R", BuildSlot: 1,
        "plant harvester here - in your network it mines nearby ore into your base store");

    public static readonly MachinePresentation Pylon = new(
        MachineType.Pylon, "pylon", "PYLON", Scancode.T, "T", BuildSlot: 2,
        "plant pylon here - it charges harvesters it can see");

    /// <summary>Every machine's presentation, indexed by <see cref="MachineType"/>.</summary>
    public static readonly IReadOnlyList<MachinePresentation> All = BuildTable(Relay, Harvester, Pylon);

    /// <summary>Every machine's presentation in build-panel order, left to right.</summary>
    public static readonly IReadOnlyList<MachinePresentation> InSlotOrder = All.OrderBy(p => p.BuildSlot).ToArray();

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
