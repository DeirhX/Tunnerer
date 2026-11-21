namespace Emberward.Core.Config;

public static partial class Tweaks
{
    /// <summary>
    /// The tank's shield (DESIGN.md §4.7), in a match whose rules run it (<c>MatchRules.TankShield</c>): a regenerating
    /// layer in front of the hull. Damage takes the shield first and the overflow goes to the hull, which only the
    /// base repairs. Starting values; tune in play.
    /// </summary>
    public static class TankShield
    {
        // 400 points is two and a half tank shells (BulletDamage 160) in front of the 1000-point hull.
        public const int Capacity = 400;

        // After a hit the shield waits this long (4 s at TargetFps 24), then refills RegenPerTick a tick:
        // empty to full in about 5.6 s.
        public const int RegenDelayTicks = 96;
        public const int RegenPerTick = 3;
    }
}
