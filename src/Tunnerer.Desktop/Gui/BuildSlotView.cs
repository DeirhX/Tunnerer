namespace Tunnerer.Desktop.Gui;

using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Desktop.Presentation;

/// <summary>How a build slot of the BUILD STRUCTURES panel stands.</summary>
public enum BuildSlotState
{
    /// <summary>Not affordable yet: the slot fills toward the cost from the stock that would pay.</summary>
    Filling,

    /// <summary>Affordable: the key builds it.</summary>
    Ready,

    /// <summary>This machine is in hand: the key plants it.</summary>
    Carrying,

    /// <summary>Another machine is in hand, so this one cannot be built.</summary>
    Blocked,
}

/// <summary>Where the ore for a build slot comes from, as its caption colours it.</summary>
public enum BuildSlotCaption
{
    /// <summary>The key and name, dimmed: shown until the slot is affordable.</summary>
    KeyAndName,

    /// <summary>The cost, paid from the base store (the tank is in its own network).</summary>
    FromBase,

    /// <summary>The cost, paid from the tank's hold.</summary>
    FromHold,
}

/// <summary>
/// What a build slot shows: its state, how full it is, its label and the caption above it. The desktop's
/// <see cref="BuildPanel"/> draws it over the art; the browser draws the same slots from it.
/// </summary>
/// <param name="Progress">How far the paying stock is toward the cost, 0 to 1.</param>
/// <param name="Label">The line along the slot's foot.</param>
/// <param name="Caption">The line at the slot's top, or null while the machine is in hand.</param>
public sealed record BuildSlotView(
    MachinePresentation Machine, BuildSlotState State, float Progress, string Label, string? Caption,
    BuildSlotCaption CaptionKind)
{
    public static BuildSlotView Of(MachinePresentation machine, BuildQuote quote, MachineType? carried)
    {
        ArgumentNullException.ThrowIfNull(machine);
        string name = machine.SlotLabel, key = machine.KeyLabel;
        int cost = quote.Cost;
        float progress = Math.Clamp(quote.Stock / (float)Math.Max(1, cost), 0f, 1f);
        bool ready = quote.CanAfford;
        bool carryingThis = carried == machine.Type;
        BuildSlotState state = carryingThis ? BuildSlotState.Carrying
            : carried != null ? BuildSlotState.Blocked
            : ready ? BuildSlotState.Ready
            : BuildSlotState.Filling;

        string label = carryingThis ? $"PLANT [{key}]" : ready ? $"{name} [{key}]" : $"ORE {quote.Stock}/{cost}";
        // The key is always visible, even before the slot is affordable; once it is, what it costs and which stock
        // pays, so the player knows where the ore comes from.
        string? caption = carryingThis ? null
            : !ready ? $"[{key}] {name}"
            : $"{cost} ORE {(quote.Payer == OrePayer.BaseStore ? "BASE" : "HOLD")}";
        BuildSlotCaption kind = !ready ? BuildSlotCaption.KeyAndName
            : quote.Payer == OrePayer.BaseStore ? BuildSlotCaption.FromBase
            : BuildSlotCaption.FromHold;
        return new BuildSlotView(machine, state, progress, label, caption, kind);
    }

    /// <summary>The line in the chest window of the MATERIALS panel: the hold emptying into the base, else the base store.</summary>
    public static string OreLine(Tank player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return player.IsUnloadingOre
            ? "UNLOADING"
            : player.Base is { } home
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"BASE ORE {home.Store.Ore}")
                : "ORE";
    }
}
