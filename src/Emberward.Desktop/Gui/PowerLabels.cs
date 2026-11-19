namespace Emberward.Desktop.Gui;

using System.Globalization;
using Emberward.Core;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Network;
using Emberward.Core.Sensing;
using Emberward.Desktop.Config;
using Emberward.Desktop.Rendering.Entities;

/// <summary>What a machine's charge label says (DESIGN.md §4.9): charge over capacity, flow in and out a second.</summary>
/// <param name="Charge">Charge over capacity, compact: <c>2.4k/3.6k</c>.</param>
/// <param name="Flow">Charge a second in and out: <c>+120 -40/s</c>.</param>
/// <param name="Fill">How full its capacitor is, 0 to 1.</param>
/// <param name="Level">Fed (<see cref="StatusLevel.Ok"/>), running on its own charge (Warn) or empty and cut off (Bad).</param>
public readonly record struct PowerLabel(string Charge, string Flow, float Fill, StatusLevel Level, bool Producing = false)
{
    /// <summary>The label's colour: the HUD's good green when fed, its warning and danger colours when not.</summary>
    public uint Color => Producing ? HudPalette.Producer : HudPalette.LevelColor(Level, HudPalette.Good);
}

/// <summary>What the base's charge row says: its store, production against draw a second, and the net.</summary>
/// <param name="Store">Store over capacity, compact.</param>
/// <param name="Flow">Production and draw a second: <c>+1.2k -300/s</c>.</param>
/// <param name="Net">Production less draw a second: <c>net +900/s</c>.</param>
/// <param name="Fill">How full the store is, 0 to 1.</param>
/// <param name="Level">Gaining (Ok), losing with charge in hand (Warn), losing and low, or empty (Bad).</param>
public readonly record struct BasePowerLabel(string Store, string Flow, string Net, float Fill, StatusLevel Level)
{
    public uint Color => HudPalette.LevelColor(Level, HudPalette.Good);
}

/// <summary>A machine the overlay labels, with its label and how strongly it shows (0 to 1, by distance).</summary>
public readonly record struct MachinePowerLabel(Machine Machine, PowerLabel Label, float Opacity);

/// <summary>
/// The charge and flow overlay's words, colours and choice of machines, shared by the desktop HUD and the browser
/// client so that both say the same. Output only: every number comes from <see cref="World.MachinePowerOf"/> and
/// <see cref="World.BasePowerOf"/>; this formats them and decides nothing the simulation owns.
/// </summary>
public static class PowerLabels
{
    /// <summary>A share of capacity under which a losing base store counts as low.</summary>
    public const double LowStoreShare = 0.25;

    /// <summary>Cells above a machine's cell where its label's bottom sits, clear of its sprite.</summary>
    public const float LiftCells = 4.5f;

    /// <summary>
    /// A charge, short: whole units under a thousand (<c>850</c>), then thousands with one decimal under ten thousand
    /// (<c>2.4k</c>), whole thousands under a million (<c>36k</c>) and millions with one decimal (<c>1.2M</c>).
    /// Negative values keep their sign.
    /// </summary>
    public static string Compact(double value)
    {
        if (!double.IsFinite(value))
            return "0";
        string sign = value < 0 ? "-" : "";
        double abs = Math.Abs(value);
        string Format(double v, string format, string unit) =>
            sign + v.ToString(format, CultureInfo.InvariantCulture) + unit;
        if (Math.Round(abs) < 1000.0)
        {
            double whole = Math.Round(abs);
            return whole == 0 ? "0" : Format(whole, "0", "");
        }
        if (Math.Round(abs / 100.0) < 100.0)
            return Format(Math.Round(abs / 100.0) / 10.0, "0.0", "k");
        if (Math.Round(abs / 1000.0) < 1000.0)
            return Format(Math.Round(abs / 1000.0), "0", "k");
        return Format(Math.Round(abs / 100_000.0) / 10.0, "0.0", "M");
    }

    /// <summary>Charge a second in and out: <c>+in -out/s</c>.</summary>
    public static string Flow(double inPerSecond, double outPerSecond) =>
        "+" + Compact(Math.Max(0.0, inPerSecond)) + " -" + Compact(Math.Max(0.0, outPerSecond)) + "/s";

    /// <summary>A rate with its sign always shown: <c>+900/s</c>, <c>-1.2k/s</c>, <c>0/s</c>.</summary>
    public static string SignedRate(double perSecond)
    {
        string text = Compact(perSecond);
        return (text == "0" || text.StartsWith('-') ? text : "+" + text) + "/s";
    }

    public static PowerLabel Of(MachinePower power)
    {
        StatusLevel level = power.IsFed ? StatusLevel.Ok : power.Charge > 0.0 ? StatusLevel.Warn : StatusLevel.Bad;
        bool producing = power.ProductionPerSecond > 0.0;
        string flow = producing ? "gen " + SignedRate(power.ProductionPerSecond) : Flow(power.InPerSecond, power.OutPerSecond);
        return new PowerLabel(Compact(power.Charge) + "/" + Compact(power.Capacity), flow,
            Fill(power.Charge, power.Capacity), level, producing);
    }

    public static BasePowerLabel Of(BasePower power)
    {
        double net = power.ProductionPerSecond - power.DrawPerSecond;
        float fill = Fill(power.Charge, power.Capacity);
        StatusLevel level = power.Charge <= 0.0 || power.Capacity <= 0.0 ? StatusLevel.Bad
            : net >= 0.0 ? StatusLevel.Ok
            : fill > LowStoreShare ? StatusLevel.Warn
            : StatusLevel.Bad;
        return new BasePowerLabel(Compact(power.Charge) + "/" + Compact(power.Capacity),
            Flow(power.ProductionPerSecond, power.DrawPerSecond), "net " + SignedRate(net) +
            (power.GeneratorProductionPerSecond > 0.0 ? " · gen " + SignedRate(power.GeneratorProductionPerSecond) : ""), fill, level);
    }

    private static float Fill(double charge, double capacity) =>
        capacity > 0.0 ? (float)Math.Clamp(charge / capacity, 0.0, 1.0) : 0f;

    /// <summary>
    /// How strongly a label shows at <paramref name="cells"/> from the player's tank: fully up to
    /// <see cref="DesktopScreenTweaks.PowerLabelFullCells"/>, fading out to nothing at
    /// <see cref="DesktopScreenTweaks.PowerLabelGoneCells"/>.
    /// </summary>
    public static float DistanceFade(float cells) =>
        Ramp(DesktopScreenTweaks.PowerLabelGoneCells, DesktopScreenTweaks.PowerLabelFullCells, cells);

    /// <summary>
    /// How strongly labels show at a zoom of <paramref name="cellSize"/> UI points (CSS pixels in the browser) a cell:
    /// none when zoomed out to <see cref="DesktopScreenTweaks.PowerLabelZoomGone"/>, fully from
    /// <see cref="DesktopScreenTweaks.PowerLabelZoomFull"/>.
    /// </summary>
    public static float ZoomFade(float cellSize) =>
        Ramp(DesktopScreenTweaks.PowerLabelZoomGone, DesktopScreenTweaks.PowerLabelZoomFull, cellSize);

    private static float Ramp(float zeroAt, float oneAt, float x) =>
        float.IsFinite(x) ? Math.Clamp((x - zeroAt) / (oneAt - zeroAt), 0f, 1f) : 0f;

    /// <summary>
    /// The machines to label: standing machines near <paramref name="player"/> (within
    /// <see cref="DesktopScreenTweaks.PowerLabelGoneCells"/>) that the player can see (<see cref="Sight"/>), in list
    /// order, each with its label and its distance fade. Empty while the rules run no energy network.
    /// </summary>
    /// <param name="near">Scratch for <see cref="MachineList.CollectNear"/>.</param>
    public static void Collect(World world, Tank player, VisionMap? vision, List<MachinePowerLabel> into, List<Machine> near)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(near);
        into.Clear();
        if (world.Energy == null)
            return;
        var center = player.Position;
        world.Machines.CollectNear(center, (int)MathF.Ceiling(DesktopScreenTweaks.PowerLabelGoneCells), near);
        foreach (var machine in near)
        {
            // The rule the machine sprites follow: what is out of sight is not shown (the player's own machines see
            // the ground they stand on, so they always are).
            if (!machine.IsStanding || !Sight.Sees(vision, machine.Position))
                continue;
            if (world.MachinePowerOf(machine) is not MachinePower power)
                continue;
            float dx = machine.Position.X - center.X, dy = machine.Position.Y - center.Y;
            float opacity = DistanceFade(MathF.Sqrt(dx * dx + dy * dy));
            if (opacity > 0f)
                into.Add(new MachinePowerLabel(machine, Of(power), opacity));
        }
    }
}
