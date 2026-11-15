namespace Tunnerer.Core.Config;

using Tunnerer.Core.Types;

public readonly record struct ExplosionParams(int ShrapnelCount, float Speed, int Frames);

public static class Tweaks
{
    public static class System
    {
        public const string WindowTitle = "Tunnerer";
        public const string Version = "0.1 alpha";
    }

    public static class Perf
    {
        public const int TargetFps = 24;
        public const int SectorSize = 64;
        public const int ProjectileCompactThreshold = 5000;
        public static int ParallelismDegree => Math.Max(1, Environment.ProcessorCount);
    }

    public static class World
    {
        public static readonly TimeSpan AdvanceStep = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / Perf.TargetFps);
        public const int MaxPlayers = 8;
        public static readonly TimeSpan DirtRecoverInterval = TimeSpan.FromMilliseconds(250);
        public const int DirtRecoverSpeed = 10;
        public const int DirtRegrowSpeed = 4;
        public const int DirtRegrowBlankModifier = 4;
        public const int DirtRegrowScorchedModifier = 1;
        public const int DecalDecaySpeed = 40;
        public const int DecalIntensityDecay = 3;
        public const int DigThroughRockChance = 250;
        public static readonly TimeSpan RefreshLinkMapInterval = TimeSpan.FromMilliseconds(200);
        public const float MaximumLiveLinkDistance = 100f;
        public const float MaximumTheoreticalLinkDistance = 170f;

        // Physically-inspired material heat exchange model.
        public static bool EnableMaterialHeatExchange => true;
        // Ambient sink toggle; conservative mode keeps this off.
        public static bool EnableThermalAmbientExchange => false;
        public static bool EnableStoneAmbientExchange { get; set; } = true;
        public static bool EnableConservativeAirField => true;
        public const float ThermalDt = 2.0f;
        public const float ThermalAmbientTemperature = 0f;

        // Effective heat capacities (higher means temperature changes slower).
        public const float ThermalCapacityAir = 0.8f;
        public const float ThermalCapacityDirt = 1.4f;
        public const float ThermalCapacityStone = 2.6f;
        public const float ThermalCapacityBase = 2.6f;
        public const float ThermalCapacityConstantEnergy = 2.0f;

        // Pairwise transmission speed (conductance).
        public const float ThermalKAirAir = 0.240f;
        public const float ThermalKAirDirt = 0.035f;
        public const float ThermalKAirStone = 0.220f;
        public const float ThermalKAirBase = 0.450f;
        public const float ThermalKDirtDirt = 0.045f;
        public const float ThermalKDirtStone = 0.070f;
        public const float ThermalKDirtBase = 0.090f;
        public const float ThermalKStoneStone = 0.300f;
        public const float ThermalKStoneBase = 0.450f;
        public const float ThermalKBaseBase = 0.200f;
        public const float ThermalKAirConstantEnergy = 0.550f;
        public const float ThermalKDirtConstantEnergy = 0.100f;
        public const float ThermalKStoneConstantEnergy = 0.450f;
        public const float ThermalKBaseConstantEnergy = 0.450f;
        public const float ThermalKConstantEnergyConstantEnergy = 0.300f;

        // Air-field coupling knobs for conservative air/terrain exchange.
        public const float ThermalAirCellCoupling = 0.120f;
        public const float ThermalAirNeighborCoupling = 0.060f;

        public const float ThermalFixedBaseTemperature = 0f;
        public const float ThermalFixedConstantEnergyTemperature = 0f;

        // Hot rock pockets: indestructible rock pinned at a fixed high temperature. They are permanent heat sources
        // that push heat into any tunnel they touch. Pocket placement is opt-in per world (World ctor).
        public const float ThermalFixedHotRockTemperature = 100f;
        public const int HotRockPocketCount = 8;
        // Gradient toward the centre: a large placeholder core at the map centre, and the other pockets biased toward
        // it (uniform sample ^ bias; 1 = uniform scatter) and bigger there. Starting values for the spike.
        public const int HotRockCoreRadius = 8;
        public const double HotRockCentreBias = 1.6;
        public const int HotRockMinRadius = 3;
        public const int HotRockMaxRadius = 5;
        public const int HotRockMinDistanceFromBase = 60;
        public const int HotRockMinDistanceBetween = 50;
        public const int HotRockPrewarmTicks = 240;
        public const float ThermalKAmbientAir = 0.0120f;
        public const float ThermalKAmbientDirt = 0.0120f;
        public const float ThermalKAmbientStone = 0.0100f;
        public const float ThermalKAmbientBase = 0.0000f;
        public const float ThermalKAmbientConstantEnergy = 0.0000f;
        public const int ThermalActiveTileSize = 32;
        public const float ThermalSparseFallbackCoverage = 0.45f;
        public const int ThermalParallelRegionThreshold = 4;
        public const int ThermalMaxWorkers = 0; // 0 => default scheduler/CPU count
        public const float ThermalActiveTemperatureThreshold = 2.5f;
        public const float ThermalArtificialCoolingPerFrame = 0.02f;
        public const float ThermalMaxDeltaSleepThreshold = 0.005f;
        public const int ThermalNoActiveSleepFrames = 120;
        public const float ThermalSaturationCoverageThreshold = 0.90f;
        public const int ThermalSaturationFramesBeforeThrottle = 90;
        public const int ThermalSaturationStepIntervalFrames = 4;
    }

    public static class Base
    {
        public const int MinDistance = 150;
        public const int BaseSize = 35;
        public const int DoorSize = 7;
        public const int InitialHeat = 0;
        public const int InitialHealth = 2000;
        public const int HeatCapacity = 100;
        public const int HealthCapacity = 2000;
        public const int MaterialDirtCapacity = 2000;
        public const int MaterialMineralsCapacity = 20000;
        // Radiator. At 1 unit per tick (500 energy/tick), two relays (300 each) out-pump it, so the reservoir fills
        // and pumping stops: the shared exit is the real bottleneck. Starting value for the first-prototype spike.
        public const int HeatCooldown = 1;
        public const int HealthRegen = 10;
        // Energy stored by one unit of base reactor heat. One bullet impact injects ~10^4 energy into the grid,
        // so the reactor (HeatCapacity units) is a reservoir worth a few hotspots, and HeatCooldown units per tick
        // is the radiator that vents it out of the system (the base's finite sink rate).
        public const float ReactorEnergyPerHeatUnit = 500f;
        // Per-tick energy caps for the base's area cooling (the "HeatTotal"/"AirTotal" names are legacy:
        // these are energy, pulled only from cells that are warmer than ambient).
        public const int AreaCoolingInnerHeatTotal = 6;
        public const int AreaCoolingMidHeatTotal = 3;
        public const int AreaCoolingOuterHeatTotal = 1;
        public const int AreaCoolingInnerAirTotal = 8;
        public const int AreaCoolingMidAirTotal = 4;
        public const int AreaCoolingOuterAirTotal = 2;
        public const int HomeCooldownHeat = 6;
        public const int HomeRechargeHealth = 3;
        public const int ForeignCooldownHeat = 2;
        public const int ForeignRechargeHealth = 1;
        public const int HomeAbsorbDirt = 15;
        public const int HomeAbsorbMinerals = 15;

        // Power (DESIGN.md §4.5). Heat relays deliver passes through the base engine on its way to the dimensional-link
        // cold sink; a fraction becomes charge: eta = clamp(dT / (dT + EngineTemperatureScale), 0, EngineMaxEfficiency),
        // dT = source temperature above the base's. Warm dirt (~30) gives ~0.17, hot rock (~100) 0.4.
        public const float EngineTemperatureScale = 150f;
        public const float EngineMaxEfficiency = 0.5f;
        public const float ChargeCapacity = 1_000_000f;
        // Enough to run the first harvester and pylon before any engine output.
        public const float InitialCharge = 300_000f;
    }

    public static class Tank
    {
        public const int MaxLives = 3;
        public static readonly TimeSpan RespawnDelay = TimeSpan.FromSeconds(3);
        public const int TurretDelay = 3;
        public const int TurretLength = 4;
        public const int DigRadius = 3;
        public const int InitialHeat = 0;
        public const int InitialHealth = 1000;
        public const int HeatCapacity = 100;
        public const int HealthCapacity = 1000;
        public const int ResourceDirtCapacity = 10000;
        public const int ResourceMineralsCapacity = 10000;
        public const int IdleHeatGain = 0;
        public const int MoveHeatGain = 0;
        public const float ShootHeatGain = 2.0f;
        public const int CoolingPickupGold = 14;

        public const float HeatMax = 100f;
        public const float HeatDigPerPixel = 0.008f;
        public const float HeatShootPerShot = 0.25f;
        public const float HeatTerrainAbsorb = 0.004f;
        public const float HeatCoolPerFrame = 0.12f;
        public const float HeatBaseCoolBonus = 0.20f;
        public const float HeatColdCoolingBoost = 0.60f;
        public const int TorchTerrainHeatAmount = 8;
        public const int TorchRockHeatAmount = 14;
        public const int TorchRockHeatRadius = 3;
        public const float HeatAmbientOutsideBase = 0f;
        public const float TankHeatCapacity = 20f;
        // Pre-cooling: a tank docked at its own base is refrigerated below ambient, down to -CoolantCapacity heat,
        // at DockCoolingPerTick heat units per tick. The tank can then absorb CoolantCapacity more heat than
        // HeatSafeMax before it overheats. The removed energy goes into the base reservoir (so a full reservoir
        // blocks pre-cooling).
        public const float CoolantCapacity = 50f;
        public const float DockCoolingPerTick = 1.0f;
        public const float TankTerrainConductance = 2.0f;
        public const float TankAmbientConductance = 0.2f;
        public const float TankBaseConductance = 0.4f;
        public const float HeatSafeMax = 100f;
        public const float OverheatDamagePerDegree = 0.5f;

        // Network coverage (DESIGN.md §4.6): outside it the tank's heat floor rises at OffNetworkHeatPerSecond and the
        // tank cannot cool below it; inside it the tank sheds up to NetworkCoolingPerSecond (times the coverage
        // strength) down the links into its base's reactor, never below zero (coolant is only refilled by docking), and
        // the floor falls at that rate. From a full coolant charge, idle off network lasts
        // (HeatSafeMax + CoolantCapacity) / OffNetworkHeatPerSecond = 150 s; digging near the end shortens it.
        public const float OffNetworkHeatPerSecond = 1.0f;
        public const float NetworkCoolingPerSecond = 6.0f;
    }

    /// <summary>Network coverage: the ground the base and its powered relays and pylons cool for their tanks.</summary>
    public static class Coverage
    {
        public const int NodeRadius = 20;
        public const int BaseRadius = Base.BaseSize / 2 + NodeRadius;
        // Cooling strength falls by this factor per live link between a node and its base.
        public const float HopFalloff = 0.85f;
    }

    public static class Weapon
    {
        public const int CannonBulletSpeed = 3;
        public const float ConcreteBarrelSpeed = 2f;
        public const float DirtBarrelSpeed = 2f;
        public const int BulletDamage = 160;
    }

    public static class Machine
    {
        public const int ReactorHeatCapacity = 100;
        public const int ReactorHealthCapacity = 1000;
        // A harvester acts (mines what it can reach, or crawls one cell) every this many milliseconds.
        public const int HarvesterIntervalMs = 300;
        // A harvester roams and mines within this distance of where it was planted.
        public const int HarvestRange = 20;
        // It eats settled dirt within this radius of itself into the owner base's store. Crawling through solid dirt
        // that is about 5 new cells per step, roughly 16 dirt/s, so it repays HarvesterDirtCost in about a minute.
        public const int HarvesterMineRadius = 2;
        // Work runs on charge, and spent charge ends up as heat in the ground around the harvester: each action that
        // mines dirt spends this much, a crawl step that mines nothing spends HarvesterCrawlCharge.
        // Starting guesses for the first-prototype spike; tune in play.
        public const float HarvesterHeatPerHarvest = 1000f;
        public const float HarvesterCrawlCharge = 200f;
        // About 40 s of mining on a full battery. A harvester is planted full.
        public const float HarvesterBatteryCapacity = 120_000f;
        // Below this fraction it heads back to a pylon; it goes back to work once charged above HarvesterResumeAbove.
        public const float HarvesterReturnBelow = 0.25f;
        public const float HarvesterResumeAbove = 0.8f;
        public const int HarvesterHeatRadius = 2;
        // A harvester stalls while the ground around it is hotter than this (average over the sense radius), so the
        // heat it makes can never run away: it keeps itself bounded, and a relay that pumps the heat out is what
        // lets it work at full speed. Starting guess for the spike; tune in play.
        public const float HarvesterStallTemperature = 30f;
        public const int HarvesterHeatSenseRadius = 4;
        public const int HarvesterDirtCost = 1000;

        // Pylon: a link node that hands base charge to batteries in direct line of sight within PylonRange, drawing
        // it from its base over the link network.
        public const int PylonDirtCost = 300;
        public const int PylonIntervalMs = 200;
        public const int PylonRange = 12;
        public const float PylonChargePerAction = 6000f;
        public const int BoundingBoxHalfSize = 2;

        // Relay: a heat pipe. Planted within line of sight of a powered link network it pulls heat out of the
        // surrounding square (half-width RelayCoolingRadius) and pumps it into its owner's base reactor.
        public const int RelayDirtCost = 150;
        public const int RelayCoolingRadius = 12;
        public const float RelayHeatPumpPerTick = 300f;
    }

    public static class Explosion
    {
        public const float MadnessLevel = 1f;
        public const int ChanceToDestroyConcrete = 50;
        public const int ChanceToDestroyRock = 50;
        public const float SpeedVarianceBase = 0.1f;
        public const float SpeedVarianceRange = 0.9f;

        public static readonly ExplosionParams Dirt = new(ShrapnelCount: 10, Speed: 0.375f, Frames: 10);
        public static readonly ExplosionParams Normal = new(ShrapnelCount: 14, Speed: 0.56f, Frames: 13);
        public static readonly ExplosionParams Death = new(ShrapnelCount: 100, Speed: 0.25f, Frames: 72);
        public const float BulletExplosionHeatScale = 1.5f;
        public const float DeathHeatScale = 1.0f;

        public const int BulletHeatAmount = 3;
        public const int BulletHeatRadius = 7;
        public const int ShrapnelHitHeat = 2;
        public const int ShrapnelDigHeatAmount = 2;
        public const int ShrapnelDigHeatRadius = 5;
    }

    public static class Colors
    {
        public static readonly Color BaseOutline = new(40, 40, 40);
        public static readonly Color TurretBarrelTip = new(0xf3, 0xeb, 0x1c);
        public static readonly Color FireHot = new(0xff, 0x34, 0x08);
        public static readonly Color Concrete = new(0xba, 0xba, 0xcc);
        public static readonly Color DirtProjectile = new(0xaa, 0x50, 0x03);
        public static readonly Color Harvester = new(0x00, 0x88, 0x00);
        public static readonly Color Pylon = new(0x4f, 0x8f, 0xff);
        public static readonly Color Relay = new(0x20, 0xd8, 0xe8);
        public static readonly Color FailedInteraction = new(0xff, 0x34, 0x08);
        public static readonly Color InfoMarker = new(0xff, 0xff, 0x4a);
        public static readonly Color LinkLive = new(0xa0, 0xa0, 0x19);
        public static readonly Color LinkBlocked = new(0xa0, 0x30, 0x30);
    }

    public static class LevelGen
    {
        public const int BorderWidth = 30;
        public const int MaxDirtSpawnOdds = 300;
        public const int DirtSpawnProgression = 70;
        public const int DirtTargetPercent = 65;
        public const int TreeSize = 150;
        public const int MaxPlayers = 2;
        public const int MinSpawnDistanceSq = 150 * 150;
        public const int SmoothingSteps = -1; // -1 = smooth until convergence (matches C++)
        public static int TargetDirtAmount(Size size) => size.X * size.Y * DirtTargetPercent / 100;

        public const int GoldVeinCountDivisor = 40;
        public const int GoldVeinMinLength = 5;
        public const int GoldVeinMaxLength = 16;
        public const int RuinAreaPerRuin = 16000;
        public const int RuinWallMinLength = 2;
        public const int RuinWallMaxLength = 5;
    }
}
