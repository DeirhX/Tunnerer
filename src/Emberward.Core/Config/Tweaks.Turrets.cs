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

    /// <summary>
    /// The gatling (DESIGN.md §6.2, <c>TurretAction</c>): the defence against emberites. A turret of the energy network
    /// that sprays light rounds at the creatures out of their nests, and at an enemy tank only when it sees no creature.
    /// It is powered, covered and slowed by heat exactly as the turret is (<see cref="Turret"/>'s scan interval, barrel
    /// and overheating numbers are its own too). Starting values; tune in play.
    /// </summary>
    public static class Gatling
    {
        public const int OreCost = 16;
        public static readonly Color Color = new(0xe8, 0xcc, 0x4a);

        // It fires at the nearest creature within Range cells once in FireIntervalTicks while cool: 6 a second,
        // half the rate a woken nest's pulse lets creatures out (Emberites.PulseSpacingTicks), so one gatling stops a small
        // pulse and thins a big one, and more gatlings or Spin make up the rest. Untuned.
        public const int Range = 20;
        public const int FireIntervalTicks = 4;

        // Its round: one kills a creature, as any shot does. Against a tank (1000 health) or a machine it is a
        // nuisance: 6 damage a round is 36 a second, a quarter of a turret's 160.
        public const int Damage = 6;
        // Cells a tick (a tank's shell flies Weapon.CannonBulletSpeed, 3; a creature runs Emberites.OpenSpeed, 1.5),
        // and the gatling aims ahead of a running creature by the round's flight time. A round that hits nothing is
        // spent RoundSpareCells past the gatling's range.
        public const float RoundSpeed = 4f;
        public const int RoundSpareCells = 6;
        // A round passes through this many cells of soil before one stops it, so a creature burrowing just inside a
        // tunnel wall is not safe from it. Rock, ore, concrete and base walls stop it at once.
        public const int SoilPierceCells = 3;

        // Each round spends this much charge, which becomes heat in the ground beside the gatling: firing without a
        // pause warms a lone gatling's ground by about 3 degrees a second, so a long siege slows it unless a relay
        // pumps the heat away.
        public const double ShotCharge = 20.0;

        // Its capacitor (the Turrets tier): a full one holds 90 rounds and refills at most TransferPerTick a tick,
        // six times what firing spends. Cut off, its idle draw empties it in 75 seconds.
        public const float Capacitor = 1800f;
        public const float TransferPerTick = 30f;
        public const float IdleDraw = 1f;

        // Per-machine upgrades (MachineUpgrades), at level 0, 1 and 2.
        // Spin: a shorter interval (4, 3, then 2 ticks), and each round spends (and so heats) more.
        public static readonly ImmutableArray<double> SpinIntervalScale = [1.0, 0.75, 0.5];
        public static readonly ImmutableArray<double> SpinShotChargeScale = [1.0, 1.1, 1.2];
        // Scope: more range, and rounds that pass through more soil.
        public const int ScopeRangePerLevel = 6;
        public const int ScopePiercePerLevel = 2;
    }
}
