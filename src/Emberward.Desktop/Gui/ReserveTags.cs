namespace Emberward.Desktop.Gui;

using System.Globalization;
using Emberward.Core;
using Emberward.Core.Entities.Tanks;

/// <summary>The tag beside OFF NETWORK that names the reserve's stage (DESIGN.md §4.9), and its counter.</summary>
/// <param name="Text">The stage and its counter: <c>RESERVE LOW 12s</c>, <c>DEGRADED 7s</c>, <c>STRANDED 4s</c>, <c>HEAT DEATH</c>.</param>
/// <param name="Level">Ok while a generator holds the clock and nothing is wrong yet, Warn for a warning, Bad from degraded on.</param>
/// <param name="Blink">Stranded and heat death blink, as the urgent OFF NETWORK tag does.</param>
public readonly record struct ReserveTag(string Text, StatusLevel Level, bool Blink)
{
    /// <summary>The tag's colour: teal while held, the HUD's warning and danger colours otherwise.</summary>
    public uint Color => HudPalette.LevelColor(Level, HudPalette.Teal);
}

/// <summary>
/// What the HUD says of the tank's reserve, shared by the desktop (ImGui) and the browser (DOM) so that both say the
/// same. Output only: everything comes from <see cref="World.ReserveOf"/>; this words it and decides nothing the
/// simulation owns.
/// </summary>
public static class ReserveTags
{
    /// <summary>The stage tag, or null when there is nothing to say (on the network, or off it with time to spare).</summary>
    public static ReserveTag? Of(in TankReserveView view)
    {
        if (!view.IsActive)
            return null;
        string counter = view.ClockHeld ? "HELD" : Seconds(view.SecondsToNextStage);
        return view.Stage switch
        {
            TankReserveStage.Warning => new ReserveTag("RESERVE LOW  " + counter, StatusLevel.Warn, false),
            TankReserveStage.Degraded => new ReserveTag("DEGRADED  " + counter, StatusLevel.Bad, false),
            TankReserveStage.Stranded => new ReserveTag("STRANDED  " + counter, StatusLevel.Bad, !view.ClockHeld),
            TankReserveStage.HeatDeath => new ReserveTag("HEAT DEATH", StatusLevel.Bad, true),
            _ => view.ClockHeld ? new ReserveTag("CLOCK HELD", StatusLevel.Ok, false) : null,
        };
    }

    /// <summary>
    /// What the recall key does now if it burns (without the key, which each platform shows its own way), or null
    /// when it does not burn. Offered from the warning on, while there is emberite to burn.
    /// </summary>
    public static string? BurnPrompt(in TankReserveView view) =>
        view.CanBurn && view.Stage >= TankReserveStage.Warning
            ? "Burn emberite  +" + Seconds(view.SecondsPerBurn) + " each"
            : null;

    private static string Seconds(float seconds) =>
        ((int)MathF.Ceiling(Math.Max(0f, seconds))).ToString(CultureInfo.InvariantCulture) + "s";
}
