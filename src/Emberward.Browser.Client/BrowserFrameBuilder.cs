namespace Emberward.Browser.Client;

using System.Globalization;
using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities.Bases;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Types;
using Emberward.Desktop.Gui;
using Emberward.Desktop.Gui.Guidance;
using Emberward.Desktop.Presentation;

/// <summary>Presentation data. Costs, coverage, placement and match outcome are queried from Core.</summary>
public static class BrowserFrameBuilder
{
    /// <param name="includeEvents">The frame follows a step: it takes the sounds that step started.</param>
    /// <param name="includeDial">The match's first frame: it carries the heat dial (<see cref="Dial"/>).</param>
    /// <param name="net">The network match the session plays in, if any.</param>
    public static BrowserFrame Capture(BrowserSession session, bool includeEvents, bool includeDial = false,
        BrowserNetMatch? net = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var world = session.World;
        var player = session.Player;
        var home = player.Base!;
        var builds = MachinePresentations.Available(world.Rules).Select(machine =>
        {
            BuildQuote quote = player.QuoteBuild(machine.Type);
            var slot = BuildSlotView.Of(machine, quote, player.CarriedMachineType);
            return new BuildOption((int)machine.BuildKey + 1, machine.Type.ToString(), quote.Cost, quote.CanAfford, quote.Payer.ToString(),
                slot.Machine.BuildSlot, slot.State.ToString(), slot.Progress, slot.Label, slot.Caption,
                slot.CaptionKind.ToString());
        }).OrderBy(b => b.Key).ToArray();
        var upgrades = Enum.GetValues<BaseUpgrade>().Select(u =>
        {
            var (name, effect) = BaseUpgrades.Describe(u);
            return new UpgradeOption((int)u + 1, name, effect, BaseUpgrades.TrackOf(u).ToString(), home.Upgrades.Level(u),
                BaseUpgrades.MaxLevel, home.Upgrades.NextCost(u), home.Upgrades.IsMaxed(u));
        }).ToArray();
        PlantPreview? preview = null;
        if (player.CarriedMachineType is MachineType carried)
        {
            var placement = world.PreviewPlacement(carried, player.Position, player.Base);
            preview = new PlantPreview(world.IsInsideAnyBase(player.Position) ? "InsideBase" :
                MachineBehaviors.Get(carried).RequiresLinkToPlant ? placement.Link.Status.ToString() : "Ready",
                placement.Link.Target, placement.SenseHeat);
        }
        GoalStatus? goal = world.CorePocket is Position core && world.CoreTapOf(home) is { } tap
            ? new GoalStatus(core, tap.TapPerTick * Tweaks.Perf.TargetFps, Tweaks.Goal.GoalChargePerSecond,
                tap.HoldSeconds, Tweaks.Goal.GoalHoldSeconds, tap.HasTapped) : null;
        var (hitFromX, hitFromY) = HitFrom(player);
        var status = new PlayerStatus(player.Position, home.Position, player.Heat, Tank.HeatBudgetCapacity,
            HeatScale.Fraction(player.Heat), HexColor(HeatScale.ColorRgb(player.Heat)),
            player.IsDead ? 0f : player.HitFlashTicks / (float)Tank.HitFlashDuration, hitFromX, hitFromY,
            player.ShieldFraction, player.OreHeld, player.OreHoldCapacity, home.Store.Ore,
            home.ChargeFill, home.HeatStoreFill, home.ArmorFill, player.IsDockedAtOwnBase, player.IsOffNetwork,
            player.OffNetworkSecondsToOverheat, player.IsDead, player.RespawnRemaining.TotalSeconds, player.CanRecall,
            world.MatchOutcomeFor(player).ToString(), player.CarriedMachineType?.ToString(), home.IsDestroyed,
            Math.Max(0, player.Reactor.Health), HeatScale.Frozen(player.Heat), HeatScale.Boil(player.Heat),
            player.IsOverheating || player.IsAboveSafeHeat, player.IsShieldLow(latched: false),
            player.HitFlashTicks > Tank.HitFlashDuration - HitFreshTicks, BuildSlotView.OrePileFrame(player), player.IsUnloadingOre,
            player.IsOreHoldFull, player.IsOffNetworkUrgent, BuildSlotView.OreLine(player));
        return new BrowserFrame(session.Seed, world.AdvanceCount, Tweaks.Perf.TargetFps, status,
            session.Sounds.Frame(takePlays: includeEvents), builds, upgrades, preview, goal, Guidance(session.Guidance),
            includeDial ? Dial() : null, net?.Status(), BasePower(world, player), MachineUpgrade(session));
    }

    /// <summary>The machine next to the tank and its panel, as the desktop shows them (<see cref="MachineUpgradeView"/>).</summary>
    private static MachineUpgradeStatus? MachineUpgrade(BrowserSession session)
    {
        if (session.MachineUpgrades.Update(session.World, session.Player) is not { } view)
            return null;
        var choices = view.Choices.Select(c => new MachineUpgradeLine(c.Key, c.Name, c.Effect, c.Level, c.Price,
            c.State.ToString(), c.CanBuy, HudPalette.Hex(c.NameColor), HudPalette.Hex(c.PriceColor))).ToArray();
        return new MachineUpgradeStatus(view.Machine.Position, view.Title,
            view.HighlightColor >> 24 != 0 ? HudPalette.Hex(view.HighlightColor) : null,
            view.Highlight == MachineUpgradeHighlight.Affordable, view.Level, view.MaxLevel, session.MachineUpgrades.IsOpen,
            choices, view.StoreOre, view.Refusal, MachineUpgradeView.RingCells, MachineUpgradeView.PipDropCells,
            view.CatchesTaps(session.MachineUpgrades.IsOpen));
    }

    private static BaseCharge? BasePower(World world, Tank player)
    {
        if (world.Energy == null || player.Base is not { } home || world.BasePowerOf(home) is not { } store)
            return null;
        var row = PowerLabels.Of(store);
        return new BaseCharge(row.Store, row.Flow, row.Net, row.Fill, HudPalette.Hex(row.Color), row.Shield);
    }

    /// <summary>How many steps after a hit the desktop's shield crystal flashes white (<c>LiquidGauges</c>).</summary>
    private const int HitFreshTicks = 6;

    /// <summary>The heat dial as the desktop's <c>HeatGauge</c> colours it.</summary>
    public static HeatDial Dial()
    {
        var bands = new string[HeatScale.DialBands];
        for (int i = 0; i < bands.Length; i++)
            bands[i] = HexColor(HeatScale.ColorRgb(HeatScale.At((i + 0.5f) / bands.Length)));
        return new HeatDial(bands, HeatScale.Fraction(0f), HeatScale.Fraction(Tweaks.Tank.HeatSafeMax),
            HeatScale.Min, HeatScale.Max);
    }

    /// <summary>Which way the last hit came from: against the shot's velocity, as the desktop's hit wedge points.</summary>
    private static (float X, float Y) HitFrom(Tank player)
    {
        var v = player.LastHitVelocity;
        float length = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        return length < 1e-3f ? (0f, 0f) : (-v.X / length, -v.Y / length);
    }

    /// <summary>An RGB colour (0 to 1 each) as #rrggbb, its channels cut to bytes as the desktop's HUD does.</summary>
    public static string HexColor(System.Numerics.Vector3 rgb)
    {
        static int Byte(float c) => (int)(Math.Clamp(c, 0f, 1f) * 255f);
        return string.Create(CultureInfo.InvariantCulture, $"#{Byte(rgb.X):x2}{Byte(rgb.Y):x2}{Byte(rgb.Z):x2}");
    }

    /// <summary>What <paramref name="guidance"/> says now, as the page shows it.</summary>
    public static GuidanceStatus Guidance(PlayerGuidance guidance)
    {
        ArgumentNullException.ThrowIfNull(guidance);
        TutorialCard? tutorial = guidance.Tutorial is { } t
            ? new TutorialCard(t.Number, t.Count, t.Title, t.Body, t.Progress, t.ProgressFraction, t.Target)
            : null;
        var toasts = guidance.Notifications
            .Select(n => new ToastLine(n.Key, n.Title, n.Detail, Tone(n.Kind).ToString(), n.PostedAt)).ToArray();
        var (hint, tone) = guidance.Hint;
        WarningLine? warning = guidance.Warning is { } w
            ? new WarningLine(w.Kind.ToString(), w.Title, w.Detail, w.Tone.ToString(), w.Blinks)
            : null;
        return new GuidanceStatus(tutorial, toasts, new HintLine(hint, tone.ToString()), warning);
    }

    /// <summary>A toast's colour, in the tones the hint and the warnings use.</summary>
    private static HintTone Tone(NotificationKind kind) => kind switch
    {
        NotificationKind.Good => HintTone.Good,
        NotificationKind.Warning => HintTone.Warn,
        NotificationKind.Danger => HintTone.Bad,
        _ => HintTone.Info,
    };
}
