namespace Emberward.Core.Config;

public static partial class Tweaks
{
    /// <summary>
    /// The tank's reserve (DESIGN.md §4.9, <c>TankReserveSystem</c>): the off-network clock, which carried emberite can
    /// buy back, and the stages of running out. Only read while <c>MatchRules.TankReserve</c> is on.
    /// </summary>
    public static class Reserve
    {
        // A burned unit of emberite lowers the off-network heat floor by this many degrees: 5 s of reserve at the base
        // rate of Tank.OffNetworkHeatPerSecond. A full 40-unit hold is 200 s, twice an idle trip from 0.
        public const float FloorDropPerOre = 5f;
        // Holding the burn key burns a unit on the first tick and then one every BurnRepeatTicks (4 a second).
        public const int BurnRepeatTicks = 6;

        // The stages, by seconds left on the off-network clock (Tank.OffNetworkSecondsToOverheat): warning at or below
        // WarningSeconds, degraded at or below DegradedSeconds, stranded at or below StrandedSeconds, and heat death
        // (the floor at the safe maximum, overheating burns the shield as before) at 0.
        public const float WarningSeconds = 30f;
        public const float DegradedSeconds = 15f;
        public const float StrandedSeconds = 5f;

        // Degraded: the dig clears on this share of the ticks it would, the tank sees this percentage of its sight
        // radius, and its gun takes this many times as long between shots (stranded keeps the slower gun's cooldown).
        public const float DegradedDigFactor = 0.5f;
        public const int DegradedSightPercent = 60;
        public const int DegradedFireDelayFactor = 2;

        // Stranded (and heat death): the tank crawls at this share of its speed and can only move and dig: no gun, no
        // torch, no new machine (one it carries can still be planted). The crawl slows the dig too, since a blocked
        // tank digs on the ticks it would move, so no dig factor is laid on top of it.
        public const float StrandedMoveFactor = 0.35f;
        public const int StrandedSightPercent = 40;

        // Generators push the safe zone out: within this many cells of a standing generator of its side, in line of
        // sight, the floor of a tank off its network stops rising (it does not fall: only the network cools).
        public const int GeneratorHoldRadius = Coverage.NodeRadius;

        // What holding a tank's clock costs the generator, in charge a tick, paid from its capacitor and turned into
        // heat in the ground beside it as any spent charge is. An eighth of what a generator makes at most
        // (Machine.GeneratorMaxChargePerTick), so a working one keeps it up with ease; a generator that makes nothing
        // holds a lone tank only until its capacitor is spent (4,800 at 8 plus its idle draw of 2: 20 s), so a dead one
        // is no permanent safe zone.
        public const double GeneratorHoldChargePerTick = 8.0;
    }
}
