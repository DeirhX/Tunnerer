namespace Emberward.Core.Config;

using System.Collections.Immutable;
using Emberward.Core.Types;

public static partial class Tweaks
{
    /// <summary>
    /// The turret (DESIGN.md §4.9, <c>TurretAction</c>): a machine of the energy network that fires at enemy tanks in a
    /// short range and in line of sight while its island feeds it. Every shot spends charge, which becomes heat in the
    /// ground beside it, and a hot turret fires more slowly. Starting values; tune in play.
    /// </summary>
    public static class Turret
    {
        // Dear on purpose (TODO.md item 6: a turret beside a relay must not make raiding impossible).
        public const int OreCost = 24;
        public static readonly Color Color = new(0xff, 0x8a, 0x3c);

        // It fires at enemy tanks within Range cells, in direct line of sight through open ground, once in
        // FireIntervalTicks (0.5 s) while cool. With nothing to shoot at it looks again after ScanIntervalTicks.
        public const int Range = 24;
        public const int FireIntervalTicks = 12;
        public const int ScanIntervalTicks = 6;
        // Its bullet: a tank's shell does Weapon.BulletDamage (160); a tank has 1000 health, so a lone turret takes
        // about six seconds to kill one that stays in its range.
        public const int Damage = 80;
        public const int BarrelLength = 4;

        // Each shot spends this much charge from its capacitor, and the charge becomes heat in the ground beside it.
        public const double ShotCharge = 150.0;

        // Its capacitor (the Turrets tier, between base defence and the economy): a full one holds 16 shots, and it
        // refills at most TransferPerTick a tick (four times what firing spends). Cut off, its idle draw empties it in
        // 100 seconds.
        public const float Capacitor = 2400f;
        public const float TransferPerTick = 40f;
        public const float IdleDraw = 1f;

        // Overheating (DESIGN.md §4.9): above SlowTemperature (average over HeatSenseRadius) it fires more slowly, up to
        // MaxSlowdown times its interval at MaxSlowTemperature and above. A relay nearby pumps the heat away.
        public const int HeatSenseRadius = 2;
        public const float SlowTemperature = 60f;
        public const float MaxSlowTemperature = 140f;
        public const double MaxSlowdown = 4.0;

        // Per-machine upgrades (MachineUpgrades), at level 0, 1 and 2.
        // Rapid: a shorter interval, and each shot spends (and so heats) more.
        public static readonly ImmutableArray<double> RapidIntervalScale = [1.0, 0.7, 0.5];
        public static readonly ImmutableArray<double> RapidShotChargeScale = [1.0, 1.2, 1.4];
        // Long: more range and damage, a longer interval.
        public const int LongRangePerLevel = 8;
        public static readonly ImmutableArray<double> LongDamageScale = [1.0, 1.5, 2.0];
        public static readonly ImmutableArray<double> LongIntervalScale = [1.0, 1.25, 1.5];
    }
}
