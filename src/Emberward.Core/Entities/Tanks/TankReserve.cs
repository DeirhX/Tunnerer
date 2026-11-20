namespace Emberward.Core.Entities.Tanks;

using Emberward.Core.Config;

/// <summary>
/// How far a tank off its network has run down its reserve, the off-network clock (DESIGN.md §4.9). The stages follow
/// one another as the clock runs down, and a burn of carried emberite or a return to the network brings it back up.
/// </summary>
public enum TankReserveStage
{
    /// <summary>On the network, or off it with time to spare.</summary>
    None,

    /// <summary>The clock is getting short: a warning only.</summary>
    Warning,

    /// <summary>Slower dig, shorter sight, slower gun.</summary>
    Degraded,

    /// <summary>Crawl and dig only: no gun, no torch, no new machine (a carried one can still be planted).</summary>
    Stranded,

    /// <summary>The floor has reached the safe maximum: overheating burns the shield, as it always has.</summary>
    HeatDeath,
}

/// <summary>What a reserve stage does to the tank (<see cref="TankReserveSystem"/>).</summary>
/// <param name="MoveFactor">Share of its speed the tank moves at, on top of the cold's.</param>
/// <param name="DigFactor">Share of a blocked tank's digs that clear ground, on top of what <paramref name="MoveFactor"/> slows.</param>
/// <param name="FireDelayFactor">How many times as long the gun takes between shots.</param>
/// <param name="SightPercent">Percentage of its sight radius the tank sees.</param>
/// <param name="CanShoot">Whether the gun (and so the torch) fires.</param>
/// <param name="CanBuild">Whether the build keys build a new machine; one the tank carries can be planted either way.</param>
public readonly record struct TankReserveEffects(
    float MoveFactor, float DigFactor, int FireDelayFactor, int SightPercent, bool CanShoot, bool CanBuild)
{
    /// <summary>No effect: the tank as it is without the reserve rule.</summary>
    public static TankReserveEffects Normal { get; } = new(1f, 1f, 1, 100, CanShoot: true, CanBuild: true);

    public static TankReserveEffects For(TankReserveStage stage) => stage switch
    {
        >= TankReserveStage.Stranded => new(Tweaks.Reserve.StrandedMoveFactor, DigFactor: 1f,
            Tweaks.Reserve.DegradedFireDelayFactor, Tweaks.Reserve.StrandedSightPercent, CanShoot: false, CanBuild: false),
        TankReserveStage.Degraded => new(1f, Tweaks.Reserve.DegradedDigFactor, Tweaks.Reserve.DegradedFireDelayFactor,
            Tweaks.Reserve.DegradedSightPercent, CanShoot: true, CanBuild: true),
        _ => Normal,
    };
}

/// <summary>
/// A tank's reserve as the HUD and the bot read it (<see cref="World.ReserveOf"/>). Output only.
/// </summary>
/// <param name="IsActive">The reserve rule is on and the tank is alive and off its network.</param>
/// <param name="Stage">Its stage now.</param>
/// <param name="SecondsLeft">Seconds on the off-network clock (<see cref="Tank.OffNetworkSecondsToOverheat"/>).</param>
/// <param name="SecondsToNextStage">The stage's counter: seconds until the next stage at the present rate (0 at heat death).</param>
/// <param name="SecondsInStage">How long the tank has been in this stage.</param>
/// <param name="CanBurn">The recall key burns a unit of emberite now, instead of recalling.</param>
/// <param name="SecondsPerBurn">Seconds of reserve one burned unit buys at the present rate.</param>
/// <param name="ClockHeld">A generator nearby holds the clock: it does not run down while the tank stays.</param>
public readonly record struct TankReserveView(
    bool IsActive, TankReserveStage Stage, float SecondsLeft, float SecondsToNextStage, float SecondsInStage,
    bool CanBurn, float SecondsPerBurn, bool ClockHeld)
{
    /// <summary>The bot's rule: degraded or worse with something to burn, it burns.</summary>
    public bool ShouldBurn => CanBurn && Stage >= TankReserveStage.Degraded;
}
