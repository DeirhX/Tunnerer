namespace Emberward.Core.Config;

using System.Collections.Immutable;

public static partial class Tweaks
{
    /// <summary>
    /// Throughput on the energy network (DESIGN.md §4.9, <c>EnergyNetwork.Throughput</c>): what a node passes on to the
    /// machines that take their charge through it, what that costs as heat, and how a hot node slows. Starting values;
    /// tune in play.
    /// </summary>
    public static class Throughput
    {
        // The most charge a link node (relay, pylon, generator) passes on to the machines behind it in one tick. Its own
        // top-up does not count. One harvester refills at its full Machine.HarvesterTransferPerTick (400) behind a node;
        // a working harvester spends about 140 a tick, so about three of them fill a trunk without Conduit.
        public const double NodeLimitPerTick = 400.0;
        // Conduit: the limit at level 0, 1 and 2, as a multiple of NodeLimitPerTick, and the most any node passes on.
        public static readonly ImmutableArray<double> ConduitScale = [1.0, 1.5, 2.0];
        public const double CeilingPerTick = 800.0;
        // The share of what a node passes on that it loses as heat where it stands, at each node on the way.
        public const double LossFraction = 0.03;
        // A node carrying charge is checked once a settle period (Energy.SettlePeriodTicks): at or above this ground
        // temperature (the average over Energy.IdleHeatRadius) it is throttled, and it recovers below the temperature
        // less the hysteresis. Above the overheat temperature it is slowed further and loses health at each check.
        public const float ThrottleTemperature = 150f;
        public const float ThrottleHysteresis = 10f;
        public const float OverheatTemperature = 250f;
        public const double ThrottledScale = 0.5;
        public const double OverheatedScale = 0.25;
        // Health an overheated node loses at each check: a node holds Machine.ReactorHealthCapacity (1000), so one
        // that stays overheated is wrecked in about 50 s.
        public const int OverheatDamage = 100;
    }
}
