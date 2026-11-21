namespace Emberward.Core.Config;

public static partial class Tweaks
{
    /// <summary>
    /// Emberlings, the creatures that nest in emberite pockets and lumps (DESIGN.md §6.2, <c>MobSwarm</c>). Starting
    /// values; tune in play.
    /// </summary>
    public static class Mobs
    {
        // ---- Nests -------------------------------------------------------------------------------------------
        // One nest per pocket and lump at least this far (cells) from every base. A nest holds creatures by deposit
        // size: a pocket 7 to 18, a lump 32 to 60 (the deposit's cells between its kind's smallest and largest).
        public const int MinNestDistanceFromBase = 60;
        public const int PocketMinCreatures = 7;
        public const int PocketMaxCreatures = 18;
        public const int LumpMinCreatures = 32;
        public const int LumpMaxCreatures = 60;
        // The nest's ground: the deposit grows from its start cell by accretion, so its reach is estimated from its cells.
        public const float NestRadiusPerSqrtCell = 1.3f;
        public const int NestRadiusPadding = 3;

        // ---- Sensing -----------------------------------------------------------------------------------------
        // A cool tank is not sensed. A tank at WakeHeat is sensed from WakeRange cells, one at FullHeat from FullRange,
        // linearly between. A woken nest hunts a tank at LoseScentHeat or above within its heat leash (below), or one a
        // creature sees; a tank that cools below LoseScentHeat and is out of sight is lost. Rock stops sight and waking. A thief (the tank that dug the nest's ore) is hunted for ThiefHuntTicks whatever its heat.
        public const float WakeHeat = 45f;
        public const float WakeRange = 40f;
        public const float FullHeat = 100f;
        public const float FullRange = 110f;
        public const float LoseScentHeat = 35f;
        public const int HuntLeashCells = 220;
        // A hunt keeps a tank by heat while it is within a leash that grows from HuntLeashMinCells at LoseScentHeat to
        // HuntLeashCells at FullHeat (a cooler tank is lost nearer), or by sight: while a creature has a line to the tank
        // clear of rock within HuntLeashCells, whatever its heat. Creatures look again once in SightCheckTicks.
        public const int HuntLeashMinCells = 60;
        public const int SightCheckTicks = 6;
        public const int ThiefHuntTicks = 6 * Perf.TargetFps;
        // A dormant nest looks at the tanks once in this many ticks (nests are staggered).
        public const int WakeCheckTicks = 6;

        // ---- Release and heat --------------------------------------------------------------------------------
        // A woken nest releases a creature every ReleaseIntervalTicks (6 a second). Each carries one bite of heat: a bite
        // adds BiteDegrees to the tank (the overheat rule does the damage), a bite lands within BiteRange cells of the
        // tank's centre. A resting creature recharges from the deposit's heat, up to RechargeShare of a charge a tick for
        // the whole nest, once in RechargeIntervalTicks; a killed creature spills what it carries SpillRadius around it.
        public const int ReleaseIntervalTicks = 4;
        public const float BiteDegrees = 2f;
        public const float BiteRange = 5f;
        public const int RechargeIntervalTicks = 4;
        public const double RechargeCharges = 2.0;
        public const int SpillRadius = 2;

        // ---- Movement ----------------------------------------------------------------------------------------
        // Cells a tick: open ground (the tank walks one) and burrowing through soil (the tank digs about half a cell).
        public const float OpenSpeed = 1.5f;
        public const float SoilSpeed = 0.35f;
        // A creature is hit by a shot within this many cells of it.
        public const float HitRadius = 1.5f;

        // ---- Paths -------------------------------------------------------------------------------------------
        // Flow fields cover FieldWindowBlocks squared blocks of FieldBlockCells squared cells around their goal, are
        // rebuilt every FieldRefreshTicks while something reads them, and forgotten after as many ticks unread.
        public const int FieldBlockCells = 4;
        public const int FieldWindowBlocks = 33;
        public const int FieldRefreshTicks = 8;
        // A block's cost to cross, in 1/12 ticks: open ground at OpenSpeed, soil at SoilSpeed.
        public const int OpenBlockCost = 32;
        public const int SoilBlockCost = 137;

        // ---- Trails ------------------------------------------------------------------------------------------
        // A creature entering a block of soil wears it by TrailWearPerCrossing (up to TrailWearMax); the wear fades to
        // nothing over TrailLifetimeTicks. A block worn to TrailLaneThreshold is a lane: they run through it as through
        // open ground, and the flow field counts it as open. Nothing is written to the terrain.
        public const float TrailWearPerCrossing = 1f;
        public const float TrailWearMax = 8f;
        public const float TrailLaneThreshold = 3f;
        public const int TrailLifetimeTicks = 24 * 60;
    }
}
