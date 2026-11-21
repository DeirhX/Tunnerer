namespace Emberward.Desktop.Gui;

using Emberward.Core.Entities;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Tanks;
using Emberward.Desktop.Presentation;

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

    /// <summary>Frame in the eight-column Pyrite pile sprite sheet (40 frames, tools/hud/make_hold_pyrite.py), or
    /// -1 for an empty hold.</summary>
    public static int OrePileFrame(Tank player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.OreHeld == 0)
            return -1;
        const int frames = 40;
        // Even the first unit in an upgraded hold shows a stone. Reserve the final pile for a full hold.
        return player.IsOreHoldFull ? frames - 1 : Math.Clamp((int)MathF.Round(player.HoldFill * frames), 1, frames - 1) - 1;
    }

    /// <summary>
    /// The line beneath the hold window: the hold emptying into the base, else a plain label. The base's ore store is
    /// not here: it only changes at the base and at its machines, and shows on the upgrade panels there.
    /// </summary>
    public static string OreLine(Tank player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return player.IsUnloadingOre ? "UNLOADING" : "ORE";
    }
}
