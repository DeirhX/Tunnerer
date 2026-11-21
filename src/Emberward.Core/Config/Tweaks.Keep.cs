namespace Emberward.Core.Config;

public static partial class Tweaks
{
    /// <summary>
    /// The base's keep (DESIGN.md §4.9, <c>BaseLayout</c>, <c>BasePowerSystem</c>): walls nothing gets through, a gate
    /// in each side, a core at the centre that is the only part to take damage, and baffle plates that hide the core
    /// from every line through a gate. Distances are cells from the base's centre. Starting values; tune in play.
    /// </summary>
    public static class Keep
    {
        // A gate is wider than the tank (7 cells), so the owner drives in without lining up.
        public const int GateSize = 11;
        // The core is a disc this many cells across (BaseLayout.IsCore).
        public const int CoreSize = 5;
        // A baffle plate stands this far out, in front of each gate, and reaches this far to either side. The gap
        // left at each corner between two plates is the only way a shot reaches the core, and only from the corner
        // of the courtyard (BaseLayoutTests casts every ray from outside to check it).
        public const int BaffleDistance = 6;
        public const int BaffleHalfLength = 4;
        // The middle of the courtyard between the baffles and the wall: where the tank spawns and the bot drives.
        public const int LaneDistance = 11;

        // The gate shield, in points of shell damage. An enemy shell that hits a gate takes its damage off the
        // shield; at nothing the gates open to the enemy, and they shut again once it is back to ShieldRestoreFill.
        // One tank fires 1280 damage a second (160 a shell, 8 shells a second), so a full shield that also recharges
        // holds a lone attacker for about 75 seconds.
        public const double ShieldCapacity = 60_000;
        public const double ShieldRechargePerTick = 20;
        public const double ShieldRestoreFill = 0.25;
        // Charge the base store pays for a point of shield; it ends as heat in the reactor, so a base under fire
        // loads its radiator (2400 charge a second at the full recharge rate, a fifth of the stock radiator).
        public const double ChargePerShieldPoint = 5;
        // The shield leaves this much in the store: defending never blacks a base out.
        public const double ShieldReserveCharge = Base.RestartCharge;
    }
}
