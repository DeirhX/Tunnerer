namespace Tunnerer.Browser.Client;

using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Bases;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Gui;
using Tunnerer.Desktop.Gui.Guidance;

/// <summary>Presentation data. Costs, coverage, placement and match outcome are queried from Core.</summary>
public static class BrowserFrameBuilder
{
    public static BrowserFrame Capture(BrowserSession session, bool includeEvents)
    {
        ArgumentNullException.ThrowIfNull(session);
        var world = session.World;
        var player = session.Player;
        var home = player.Base!;
        var builds = MachineBehaviors.All.Select(b =>
        {
            BuildQuote quote = player.QuoteBuild(b.Type);
            return new BuildOption((int)b.Key + 1, b.Type.ToString(), quote.Cost, quote.CanAfford, quote.Payer.ToString());
        }).OrderBy(b => b.Key).ToArray();
        var upgrades = Enum.GetValues<BaseUpgrade>().Select(u =>
        {
            var (name, effect) = BaseUpgrades.Describe(u);
            return new UpgradeOption((int)u + 1, name, effect, home.Upgrades.Level(u), home.Upgrades.NextCost(u), home.Upgrades.IsMaxed(u));
        }).ToArray();
        PlantPreview? preview = null;
        if (player.CarriedMachineType is MachineType carried)
        {
            var placement = world.PreviewPlacement(carried, player.Position);
            preview = new PlantPreview(world.IsInsideAnyBase(player.Position) ? "InsideBase" :
                MachineBehaviors.Get(carried).RequiresLinkToPlant ? placement.Link.Status.ToString() : "Ready",
                placement.Link.Target, placement.SenseHeat);
        }
        GoalStatus? goal = world.CorePocket is Position core && world.CoreTapOf(home) is { } tap
            ? new GoalStatus(core, tap.TapPerTick * Tweaks.Perf.TargetFps, Tweaks.Goal.GoalChargePerSecond,
                tap.HoldSeconds, Tweaks.Goal.GoalHoldSeconds, tap.HasTapped) : null;
        var status = new PlayerStatus(player.Position, home.Position, player.Heat, Tank.HeatBudgetCapacity,
            player.ShieldFraction, player.LivesLeft, player.OreHeld, player.OreHoldCapacity, home.Store.Ore,
            home.ChargeFill, home.HeatStoreFill, player.IsDockedAtOwnBase, player.IsOffNetwork,
            player.OffNetworkSecondsToOverheat, player.IsDead, player.RespawnRemaining.TotalSeconds,
            world.MatchOutcomeFor(player).ToString(), player.CarriedMachineType?.ToString());
        return new BrowserFrame(session.Seed, world.AdvanceCount, Tweaks.Perf.TargetFps, status,
            session.Sounds.Frame(takePlays: includeEvents), builds, upgrades, preview, goal, Guidance(session.Guidance));
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
