namespace Tunnerer.Browser.Client;

using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Bases;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Core.Types;

/// <summary>Presentation data. Costs, coverage, placement and match outcome are queried from Core.</summary>
public static class BrowserFrameBuilder
{
    public static BrowserFrame Capture(BrowserSession session, bool includeEvents)
    {
        ArgumentNullException.ThrowIfNull(session);
        var world = session.World;
        var player = session.Player;
        var home = player.Base!;
        var vision = session.Vision;
        var events = new List<SceneEvent>();
        if (includeEvents)
            foreach (var e in world.StepEvents)
                if (vision.IsVisible(e.Position))
                    events.Add(new SceneEvent(e.Kind.ToString(), e.Position.X, e.Position.Y));
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
        string? refusal = player.BuildRefusedTicks > 0 ? player.LastBuildRefusal.ToString() :
            player.UpgradeRefusedTicks > 0 ? player.LastUpgradeRefusal.ToString() : null;
        var status = new PlayerStatus(player.Position, home.Position, player.Heat, Tank.HeatBudgetCapacity,
            player.ShieldFraction, player.LivesLeft, player.OreHeld, player.OreHoldCapacity, home.Store.Ore,
            home.ChargeFill, home.HeatStoreFill, player.IsDockedAtOwnBase, player.IsOffNetwork,
            player.OffNetworkSecondsToOverheat, player.IsDead, player.RespawnRemaining.TotalSeconds,
            world.MatchOutcomeFor(player).ToString(), player.CarriedMachineType?.ToString(), refusal);
        return new BrowserFrame(session.Seed, world.AdvanceCount, Tweaks.Perf.TargetFps, status,
            events.ToArray(), builds, upgrades, preview, goal);
    }
}
