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

        // It refills only while the tank is on its network and its base is not dark; elsewhere it holds what it has. After a hit it waits this long (4 s at TargetFps 24), then refills RegenPerTick a tick:
        // empty to full in about 5.6 s.
        public const int RegenDelayTicks = 96;
        public const int RegenPerTick = 3;

        // Fire (DESIGN.md §4.7). A hull below FireIgniteHullFraction burns, and the fire grows as the hull falls: nothing
        // at that fraction, full size at an empty hull. At full size it adds FireTankHeatPerTickMax degrees a tick to
        // the tank (6 a second at 24 ticks: exactly what a full-strength link takes away, so a tank on its network holds
        // its fire at bay, and one beyond it, or at the end of a weak link, does not) and puts
        // FireAirEnergyPerTickMax energy a tick into the air around it, twice what the tank itself keeps: a burning tank
        // warms its surroundings more than itself. Only the base puts it out, by repairing the hull.
        public const float FireIgniteHullFraction = 0.75f;
        public const float FireTankHeatPerTickMax = 0.25f;
        public const double FireAirEnergyPerTickMax = 10.0;
        public const int FireAirRadius = 3;
    }
}
