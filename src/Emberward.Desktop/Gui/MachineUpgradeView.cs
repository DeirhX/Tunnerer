namespace Emberward.Desktop.Gui;

using System.Globalization;
using Emberward.Core;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Tanks;
using Emberward.Desktop.Presentation;

/// <summary>How the machine next to the tank is lit: amber when its next level is affordable, dim when not, not at all when maxed.</summary>
public enum MachineUpgradeHighlight { None, Affordable, TooDear }

/// <summary>Where one path of a machine's upgrade stands for the tank next to it.</summary>
public enum MachineUpgradeChoiceState
{
    /// <summary>The next level can be bought now.</summary>
    Open,

    /// <summary>The next level is open but the base store cannot pay it.</summary>
    TooDear,

    /// <summary>The machine took this path and is at its top level.</summary>
    Maxed,

    /// <summary>The machine took another path, which its first purchase fixed.</summary>
    Locked,

    /// <summary>This match does not run the path (Conduit without the energy network).</summary>
    Unavailable,
}

/// <summary>One path in the machine upgrade panel: its key, name, what a level does, its level and price.</summary>
public readonly record struct MachineUpgradeChoice(
    int Key, MachineUpgradePath Path, string Name, string Effect, int Level, string Price, MachineUpgradeChoiceState State)
{
    public bool CanBuy => State == MachineUpgradeChoiceState.Open;

    /// <summary>The colour of its name: amber when it can be bought, dim otherwise.</summary>
    public uint NameColor => CanBuy ? HudPalette.Amber : HudPalette.TextDim;

    /// <summary>The colour of its price: green when payable, red when too dear, dim when nothing can be bought.</summary>
    public uint PriceColor => State switch
    {
        MachineUpgradeChoiceState.Open => HudPalette.Good,
        MachineUpgradeChoiceState.TooDear => HudPalette.Danger,
        _ => HudPalette.TextDim,
    };
}

/// <summary>
/// The machine upgrade highlight and panel for the machine next to the tank (DESIGN.md §4.9), shared by the desktop
/// HUD (ImGui) and the browser (DOM) so that both say the same. Output only: every number comes from
/// <see cref="World.MachineUpgradeOfferFor"/> and <see cref="World.MachineUpgradeRefusalOf"/>.
/// </summary>
/// <param name="Machine">The machine the upgrade keys buy for.</param>
/// <param name="Title">Its name, upper case, and the path it took: <c>RELAY - TAP</c>.</param>
/// <param name="Highlight">How it is lit.</param>
/// <param name="Level">Levels bought: the lit level pips.</param>
/// <param name="MaxLevel">How many pips there are.</param>
/// <param name="Choices">One per path, in key order.</param>
/// <param name="StoreOre">Ore in the base store, which pays.</param>
/// <param name="Refusal">Why the last press bought nothing, while that is still worth saying; else null.</param>
public sealed record MachineUpgradeView(
    Machine Machine, string Title, MachineUpgradeHighlight Highlight, int Level, int MaxLevel,
    IReadOnlyList<MachineUpgradeChoice> Choices, int StoreOre, string? Refusal)
{
    /// <summary>Cells around the machine's centre the highlight ring is drawn at, and a tap on it counts within.</summary>
    public const float RingCells = 4.5f;

    /// <summary>
    /// Cells below the machine's centre where the pips sit, under its ring. Not above it: the charge label
    /// (<see cref="PowerLabels.LiftCells"/>) is there, and pips over it hid its flow line.
    /// </summary>
    public const float PipDropCells = 6f;

    /// <summary>The ring's colour; zero alpha when it is not lit.</summary>
    public uint HighlightColor => Highlight switch
    {
        MachineUpgradeHighlight.Affordable => HudPalette.Amber,
        MachineUpgradeHighlight.TooDear => HudPalette.WithAlpha(HudPalette.AmberDim, 0.7f),
        _ => 0u,
    };

    /// <summary>
    /// Whether a tap within <see cref="RingCells"/> of the machine goes to the panel (on touch) instead of firing: only
    /// while the machine is lit, or to close an open panel. A maxed machine is not lit, so a tap near it fires.
    /// </summary>
    public bool CatchesTaps(bool panelOpen) => panelOpen || Highlight != MachineUpgradeHighlight.None;
}

/// <summary>Builds <see cref="MachineUpgradeView"/>s from what Core answers.</summary>
public static class MachineUpgradeViews
{
    /// <summary>The view for the machine <paramref name="player"/> would upgrade, or null when none is in reach.</summary>
    public static MachineUpgradeView? For(World world, Tank player)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(player);
        if (world.MachineUpgradeOfferFor(player) is not MachineUpgradeOffer offer)
            return null;
        var machine = offer.Machine;
        var paths = machine.Behavior.UpgradePaths;
        var choices = new MachineUpgradeChoice[paths.Length];
        for (int i = 0; i < paths.Length; i++)
            choices[i] = ChoiceFor(world, offer, paths[i], i + 1);

        string name = MachinePresentations.Get(machine.Type).SlotLabel;
        string title = offer.Upgrade.Path is MachineUpgradePath taken
            ? $"{name} - {MachineUpgrades.Describe(taken).Name.ToUpperInvariant()}"
            : name;
        var highlight = offer.IsMaxed ? MachineUpgradeHighlight.None
            : offer.CanAfford ? MachineUpgradeHighlight.Affordable
            : MachineUpgradeHighlight.TooDear;
        var (refusal, ticks) = world.MachineUpgradeRefusalOf(player);
        return new MachineUpgradeView(machine, title, highlight, offer.Upgrade.Level, MachineUpgrades.MaxLevel, choices,
            player.Base?.Store.Ore ?? 0, ticks > 0 ? RefusalText(refusal) : null);
    }

    /// <summary>What the panel says after a refused press.</summary>
    public static string? RefusalText(MachineUpgradeRefusal refusal) => refusal switch
    {
        MachineUpgradeRefusal.OutOfReach => "No machine of yours within reach.",
        MachineUpgradeRefusal.Maxed => "That machine is at its top level.",
        MachineUpgradeRefusal.OtherPath => "That machine already took another path.",
        MachineUpgradeRefusal.Unavailable => "That upgrade is not available in this match.",
        MachineUpgradeRefusal.NotEnoughOre => "Not enough ore in the base store.",
        _ => null,
    };

    private static MachineUpgradeChoice ChoiceFor(World world, MachineUpgradeOffer offer, MachineUpgradePath path, int key)
    {
        var (name, effect) = MachineUpgrades.Describe(path);
        int level = offer.Upgrade.LevelOf(path);
        MachineUpgradeChoiceState state =
            !MachineUpgrades.IsAvailable(path, world.Rules) ? MachineUpgradeChoiceState.Unavailable
            : offer.Upgrade.Path is MachineUpgradePath taken && taken != path ? MachineUpgradeChoiceState.Locked
            : offer.IsMaxed ? MachineUpgradeChoiceState.Maxed
            : offer.CanAfford ? MachineUpgradeChoiceState.Open
            : MachineUpgradeChoiceState.TooDear;
        string price = state switch
        {
            MachineUpgradeChoiceState.Unavailable => "needs energy",
            MachineUpgradeChoiceState.Locked => "locked",
            MachineUpgradeChoiceState.Maxed => "maxed",
            _ => string.Create(CultureInfo.InvariantCulture, $"{offer.NextCost} ore"),
        };
        return new MachineUpgradeChoice(key, path, name, effect, level, price, state);
    }
}

/// <summary>
/// Whether the machine upgrade panel is open, and its closing rules (TODO.md item 4): a key opens it on the machine
/// next to the tank and closes it again; it also closes once nothing is in reach, the tank dies, or a level is
/// bought. While it is open the upgrade keys buy for the machine (<see cref="Core.Input.ControllerOutput.UpgradeMachine"/>).
/// It follows the nearest machine as the tank moves. Shared by the desktop and the browser; UI state only.
/// </summary>
public sealed class MachineUpgradePanel
{
    private Machine? _machine;
    private int _openedLevel;

    public bool IsOpen { get; private set; }

    /// <summary>The panel key: closes an open panel, or opens it on the machine in reach (nothing happens with none).</summary>
    public void Toggle(World world, Tank player)
    {
        if (IsOpen)
        {
            Close();
            return;
        }
        if (MachineUpgradeViews.For(world, player) is { } view)
            Open(view);
    }

    /// <summary>Opens the panel on <paramref name="view"/>'s machine (a tap on it, in the browser).</summary>
    public void Open(MachineUpgradeView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        IsOpen = true;
        _machine = view.Machine;
        _openedLevel = view.Level;
    }

    public void Close()
    {
        IsOpen = false;
        _machine = null;
    }

    /// <summary>
    /// Once a frame: the view of the machine in reach (null when none), after applying the closing rules to an open
    /// panel.
    /// </summary>
    public MachineUpgradeView? Update(World world, Tank player)
    {
        var view = player.IsDead ? null : MachineUpgradeViews.For(world, player);
        if (!IsOpen)
            return view;
        if (view is null || (ReferenceEquals(view.Machine, _machine) && view.Level != _openedLevel))
            Close();
        else if (!ReferenceEquals(view.Machine, _machine))
            Open(view);
        return view;
    }
}
