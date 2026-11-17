namespace Tunnerer.Core.Bots;

using Tunnerer.Core.Config;

/// <summary>
/// One of the bot's play styles: when it retreats, how readily it fires, how often it replans and how it rates tiles.
/// <see cref="RetreatHeatBudgetFraction"/> is a share of <c>Tank.HeatBudgetCapacity</c>: in a fight the bot backs off
/// once its heat budget (room left before it overheats, coolant included) falls below it.
/// <see cref="TargetPull"/> is what a step straight at the target is worth on the scale of the tile bonuses (a step
/// straight away costs as much): against the gap between open ground and dirt it decides how far out of its way the
/// bot follows a tunnel before it digs toward the target instead.
/// </summary>
internal readonly record struct BotBehaviorProfile(
    string Name,
    float LowHealthRatio,
    float RetreatHeatBudgetFraction,
    int OutgunnedHealthGap,
    int ShootChanceLosPermille,
    int ShootChanceDigPermille,
    int ReplanLosMin,
    int ReplanLosMax,
    int ReplanTunnelMin,
    int ReplanTunnelMax,
    float TargetPull,
    float OpenTileBonus,
    float SoftTileBonus);

/// <summary>Cycles the bot through its play styles every few seconds so it does not feel scripted.</summary>
internal sealed class BotProfileCycle
{
    private const int ProfileSwitchSeconds = 4;

    private static readonly BotBehaviorProfile[] Profiles =
    {
        new(
            Name: "cautious",
            LowHealthRatio: 0.70f,
            RetreatHeatBudgetFraction: 0.30f,
            OutgunnedHealthGap: 40,
            ShootChanceLosPermille: 500,
            ShootChanceDigPermille: 250,
            ReplanLosMin: 5,
            ReplanLosMax: 9,
            ReplanTunnelMin: 7,
            ReplanTunnelMax: 14,
            TargetPull: 24f,
            OpenTileBonus: 46f,
            SoftTileBonus: 10f),
        new(
            Name: "balanced",
            LowHealthRatio: 0.50f,
            RetreatHeatBudgetFraction: 0.22f,
            OutgunnedHealthGap: 100,
            ShootChanceLosPermille: 700,
            ShootChanceDigPermille: 450,
            ReplanLosMin: 6,
            ReplanLosMax: 12,
            ReplanTunnelMin: 8,
            ReplanTunnelMax: 20,
            TargetPull: 30f,
            OpenTileBonus: 36f,
            SoftTileBonus: 12f),
        new(
            Name: "rush",
            LowHealthRatio: 0.30f,
            RetreatHeatBudgetFraction: 0.13f,
            OutgunnedHealthGap: 220,
            ShootChanceLosPermille: 900,
            ShootChanceDigPermille: 700,
            ReplanLosMin: 4,
            ReplanLosMax: 8,
            ReplanTunnelMin: 6,
            ReplanTunnelMax: 12,
            TargetPull: 40f,
            OpenTileBonus: 26f,
            SoftTileBonus: 16f),
    };

    private int _activeIdx;
    private int _framesUntilSwitch = ProfileSwitchSeconds * Tweaks.Perf.TargetFps;

    public BotBehaviorProfile Active => Profiles[_activeIdx];

    public void Advance()
    {
        _framesUntilSwitch--;
        if (_framesUntilSwitch > 0)
            return;

        _activeIdx = (_activeIdx + 1) % Profiles.Length;
        _framesUntilSwitch = ProfileSwitchSeconds * Tweaks.Perf.TargetFps;
    }
}
