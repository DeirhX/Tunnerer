namespace Tunnerer.Core.Input;

using Tunnerer.Core.Config;

/// <summary>One of the bot's play styles: when it retreats, how readily it fires, how often it replans and how it rates tiles.</summary>
internal readonly record struct BotBehaviorProfile(
    string Name,
    float LowHealthRatio,
    float HighHeatRatio,
    int OutgunnedHealthGap,
    int ShootChanceLosPermille,
    int ShootChanceDigPermille,
    int ReplanLosMin,
    int ReplanLosMax,
    int ReplanTunnelMin,
    int ReplanTunnelMax,
    float DistanceWeight,
    float OpenTileBonus,
    float SoftTileBonus,
    float HardTileBonus);

/// <summary>Cycles the bot through its play styles every few seconds so it does not feel scripted.</summary>
internal sealed class BotProfileCycle
{
    private const int ProfileSwitchSeconds = 4;

    private static readonly BotBehaviorProfile[] Profiles =
    {
        new(
            Name: "cautious",
            LowHealthRatio: 0.70f,
            HighHeatRatio: 0.55f,
            OutgunnedHealthGap: 40,
            ShootChanceLosPermille: 500,
            ShootChanceDigPermille: 250,
            ReplanLosMin: 5,
            ReplanLosMax: 9,
            ReplanTunnelMin: 7,
            ReplanTunnelMax: 14,
            DistanceWeight: 0.012f,
            OpenTileBonus: 46f,
            SoftTileBonus: 10f,
            HardTileBonus: -4f),
        new(
            Name: "balanced",
            LowHealthRatio: 0.50f,
            HighHeatRatio: 0.67f,
            OutgunnedHealthGap: 100,
            ShootChanceLosPermille: 700,
            ShootChanceDigPermille: 450,
            ReplanLosMin: 6,
            ReplanLosMax: 12,
            ReplanTunnelMin: 8,
            ReplanTunnelMax: 20,
            DistanceWeight: 0.015f,
            OpenTileBonus: 36f,
            SoftTileBonus: 12f,
            HardTileBonus: 2f),
        new(
            Name: "rush",
            LowHealthRatio: 0.30f,
            HighHeatRatio: 0.80f,
            OutgunnedHealthGap: 220,
            ShootChanceLosPermille: 900,
            ShootChanceDigPermille: 700,
            ReplanLosMin: 4,
            ReplanLosMax: 8,
            ReplanTunnelMin: 6,
            ReplanTunnelMax: 12,
            DistanceWeight: 0.020f,
            OpenTileBonus: 26f,
            SoftTileBonus: 16f,
            HardTileBonus: 10f),
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
