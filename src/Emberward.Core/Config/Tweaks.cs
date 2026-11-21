namespace Emberward.Core.Config;

using System.Collections.Immutable;
using Emberward.Core.Terrain;
using Emberward.Core.Thermal;
using Emberward.Core.Types;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Regrowth;
using Emberward.Core.Entities.Bases;

public readonly record struct ExplosionParams(int ShrapnelCount, float Speed, int Frames);

public static partial class Tweaks
{
    public static class System
    {
        public const string WindowTitle = "Emberflow";
    }

    public static class Perf
    {
        public const int TargetFps = 24;
        public const int SectorSize = 64;
        public const int ProjectileCompactThreshold = 5000;
    }

    public static class World
    {
        public static readonly TimeSpan AdvanceStep = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / Perf.TargetFps);
        public const int MaxPlayers = 8;
        public static readonly TimeSpan DirtRecoverInterval = TimeSpan.FromMilliseconds(750);
        public const int DirtRecoverSpeed = 10;
        // Per-mille chance per regrowth check, per dirt neighbour (times the modifiers below).
        public const int DirtRegrowSpeed = 4;
        public const int DirtRegrowBlankModifier = 4;
        public const int DirtRegrowScorchedModifier = 1;
        public const int DecalIntensityDecay = 3;
        public const int DigThroughRockChance = 250;
        public static readonly TimeSpan RefreshLinkMapInterval = TimeSpan.FromMilliseconds(200);
        public const float MaximumLiveLinkDistance = 100f;
        public const float MaximumTheoreticalLinkDistance = 170f;

        public const float ThermalDt = 2.0f;
        public const float ThermalAmbientTemperature = 0f;

        // One row per thermal material (see ThermalMaterialProperties):
        // - capacity: energy per degree (higher means temperature changes slower);
        // - conductivity: half-cell conductance; two touching cells conduct ka * kb / (ka + kb), so a material
        //   against itself conducts half its value. Air's value is the air field's, between two neighbouring cells of
        //   which at least one is open;
        // - air exchange: conductance between a cell's terrain and the air in the same cell;
        // - loss: the fraction of its temperature above ambient the cell gives up to the outside every tick (solid
        //   ground to the deep ground, open air to the surface). It is the only way heat leaves the map other than
        //   through a base radiator, and it bounds how far the heat from a pocket spreads.
        //
        // Dirt is the insulator: it conducts 30 times worse than stone, so a pocket buried in it keeps its heat until a
        // tunnel reaches it, and the halo around the pocket stays about ten cells wide. Open air loses heat faster
        // than the ground does, so a dug-out chamber works as a radiator: the more open space around a heat source,
        // the cooler it runs.
        // Base walls and core conduct better than rock. Pockets take longer to change temperature than stone
        // because they hold more energy per degree. The hot rock's small loss preserves its stock during exploration.
        public static ThermalMaterialTable ThermalMaterials { get; } = new([
            (ThermalMaterial.Air, new(HeatCapacity: 0.8f, Conductivity: 0.48f, AirExchange: 0f, Loss: 0.0010f)),
            (ThermalMaterial.Dirt, new(HeatCapacity: 1.4f, Conductivity: 0.02f, AirExchange: 0.035f, Loss: 0.0003f)),
            (ThermalMaterial.Stone, new(HeatCapacity: 2.6f, Conductivity: 0.6f, AirExchange: 0.22f, Loss: 0.0003f)),
            (ThermalMaterial.Base, new(HeatCapacity: 2.6f, Conductivity: 1.8f, AirExchange: 0.45f, Loss: 0.0003f)),
            (ThermalMaterial.ConstantEnergy, new(HeatCapacity: 2.0f, Conductivity: 1.8f, AirExchange: 0.55f, Loss: 0.0003f)),
            (ThermalMaterial.HotRock, new(HeatCapacity: 128f, Conductivity: 0.6f, AirExchange: 0.22f, Loss: 0.00005f)),
            (ThermalMaterial.FrostRock, new(HeatCapacity: 16f, Conductivity: 0.6f, AirExchange: 0.22f, Loss: 0.0003f)),
            // The core of an pyrite lump is an energy-dense store: it conducts like stone but holds Ore.CoreHeatCapacity
            // of heat per degree where stone holds 2.6, so seeded hot (Ore.CoreStartTemperature) it keeps a lot of energy
            // and cools slowly. Nothing is pinned: it is finite, and it cools into the ground and the air like any rock.
            (ThermalMaterial.PyriteCore, new(HeatCapacity: Ore.CoreHeatCapacity, Conductivity: 0.6f, AirExchange: 0.22f, Loss: 0.0003f)),
        ]);

        // Air-field coupling knobs for conservative air/terrain exchange.
        public const float ThermalAirCellCoupling = 0.120f;
        public const float ThermalAirNeighborCoupling = 0.060f;

        public const int HotRockPocketCount = 8;
        public const float HotRockStartTemperature = 100f;
        // Gradient toward the centre: a large placeholder core at the map centre, and the other pockets biased toward
        // it (uniform sample ^ bias; 1 = uniform scatter) and bigger there. Starting values for the spike.
        public const int HotRockCoreRadius = 8;
        public const double HotRockCentreBias = 1.6;
        public const int HotRockMinRadius = 3;
        public const int HotRockMaxRadius = 5;
        public const int HotRockMinDistanceFromBase = 60;
        public const int HotRockMinDistanceBetween = 50;
        public const int HotRockPrewarmTicks = 240;

        // Frost pockets: indestructible rock seeded far below ambient. A tank beside one cools fast, a good place to
        // shed heat, but stay too long and it gets cold enough to slow down and then take frostbite. Placed at middle
        // distances from the bases, away from hot pockets.
        // Starting values; tune in play.
        public const int FrostRockPocketCount = 5;
        public const float FrostRockStartTemperature = -60f;
        public const int FrostRockMinRadius = 2;
        public const int FrostRockMaxRadius = 4;
        public const int FrostRockMinDistanceFromBase = 45;
        public const int FrostRockMinDistanceFromHot = 35;
        public const int FrostRockMinDistanceBetween = 40;

        // The solver steps only the tiles where heat is still changing, plus the ring of tiles around them; a tile
        // where nothing changes any more is in equilibrium and is left alone until something disturbs it. A cell
        // counts as changing while a step moves it by at least:
        // - ThermalSettledGrowStep degrees away from ambient. This is the far edge of a halo that is still creeping
        //   outward: slower than this (about 1.4 degrees a minute) it is taken as arrived, so a halo sleeps a little
        //   short of where it would end, and creeps on whenever something nearby keeps it stepped. Measured on a
        //   pocket buried in dirt: at 0.001 it sleeps after about 4 minutes with the rim (12 cells out) 30% short;
        //   at 0.0003 after 6 minutes, 10% short; at 0.0001 after 9 minutes, 4% short. The coarse value is chosen on
        //   purpose: running on weak computers matters more than an exact halo, and a tile that went to sleep early
        //   is woken by anything that writes heat into it or changes a material beside heat.
        // - ThermalSettledFadeStep degrees back toward ambient. Fading heat is followed until it loses less than this
        //   a tick. Ambient loss alone leaves up to a third of a degree in most ground, a tenth in open air and
        //   two degrees in hot rock when its tile goes to sleep.
        public const int ThermalActiveTileSize = 32;
        public const float ThermalSettledGrowStep = 0.001f;
        public const float ThermalSettledFadeStep = 0.0001f;
        // A change of material (digging, regrowth) wakes the solver only where a cell involved is further than this
        // from ambient. It exceeds the trace that fading leaves behind, including in hot rock.
        public const float ThermalWakeTemperature = 2.1f;
        // A temperature difference or a heat flow below this is treated as none. It has to stay far below the two
        // settle steps: a cut-off near them makes flows switch on and off, and a halo that should have settled
        // keeps twitching and never sleeps.
        public const float ThermalFlowCutoff = 1e-7f;
        // Side of the tiles regrowth keeps its appointments in (RegrowthSchedule); a tile with none holds no memory.
        public const int RegrowthTileSize = 32;
        // Side of the regions that each take their regrowth pass on a tick of their own, which spreads a large map's
        // work over the ticks between passes. It has to stay several times a look-out's reach: a look-out is recast
        // when ground in its sight changes, so ground in one view should change on one tick, not on many (at 256
        // vision cost three times as much).
        public const int RegrowthRegionSize = 1024;
        // The longest an open cell waits to be looked at again, and growing dirt to settle, in passes. The waits are
        // drawn from the chances above; the few that would be longer end here (two and three in a hundred thousand).
        public const int RegrowthLongestLookPasses = 80;
        public const int RegrowthLongestSettlePasses = 1024;
        // Tiles are stepped on several threads from this many up; fewer are not worth handing out.
        public const int ThermalParallelTileThreshold = 32;
        public const int ThermalMaxWorkers = 0; // 0 => default scheduler/CPU count
        public const float ThermalSaturationCoverageThreshold = 0.90f;
        public const int ThermalSaturationFramesBeforeThrottle = 90;
        public const int ThermalSaturationStepIntervalFrames = 4;
    }

    /// <summary>
    /// Pyrite ore (DESIGN.md §6.3): deposits seeded at generation (LevelGen.OreDeposits) and mined by the tank.
    /// Deposit counts scale with map area (one per <c>*EveryCells</c> cells), so the 1500x750 map gets about 2,500
    /// units: about 15% in specks, 35% in pockets and 50% in lumps. Starting values; tune in play.
    /// </summary>
    public static class Ore
    {
        public const int PlainCellUnits = 1;
        public const int CoreCellUnits = 3;
        // Ore starts the match with heat in it: each cell is seeded at generation with
        // capacity * start temperature of energy (booked as injected in the ledger), and that energy is all there is: it
        // conducts out into the ground and the air and is lost like any ground's heat, so the ore cools. A speck or a
        // shell cell (plain stone) starts barely warm; a lump's core cells (CoreHeatCapacity a degree, against stone's
        // 2.6) start far above Tank.HeatSafeMax. Bigger lumps have more core cells (LumpCoreShare), so they hold more
        // energy and stay dangerous for longer.
        public const float PlainStartTemperature = 9f;
        public const float CoreHeatCapacity = 16f;
        public const float CoreStartTemperature = 600f;

        // Mining (TankDigger). The tank's dig clears a plain ore cell with this per-mille chance per dig (dirt always
        // clears), so ore is slower than dirt; a core cell needs the torch and its own chance (rock is
        // World.DigThroughRockChance). The hold is small: a full one leaves ore in the ground.
        public const int DigOreChance = 300;
        public const int TorchCoreChance = 150;
        public const int TankHold = 40;

        // The economy (DESIGN.md §6.4): ore is the only currency. A base starts with BaseStartingStore and holds up to
        // BaseStoreCapacity. Docked, the hold empties into the store at Base.HomeAbsorbOre a tick; inside the coverage
        // of its own linked network (Coverage) it unloads at NetworkUnloadPerSecond, slower, so relays are also drop-off
        // points.
        public const int BaseStartingStore = 30;
        public const int BaseStoreCapacity = 20000;
        public const int NetworkUnloadPerSecond = 4;
        // How long a link shows ore passing (Link.OreFlowTicks): a little over the gap between two units.
        public const int LinkFlowShowTicks = Perf.TargetFps / NetworkUnloadPerSecond + 6;

        // Build costs, paid in ore (Entities/Tanks/TankBuildController: the base store inside the own network, else
        // the hold).
        public const int RelayCost = 4;
        public const int HarvesterCost = 30;
        public const int GeneratorCost = 18;

        // Harvesters (MachineBehaviors.Harvester) work plain ore within HarvesterReach of their centre (their
        // footprint is 5x5, so ore beside it is 3 cells off): a cell takes HarvesterPlainWork actions. At one action
        // per Machine.HarvesterIntervalMs that is about 1.1 units/s, so a harvester repays its cost in under a minute.
        // They leave a lump's core to the tank's torch.
        public const int HarvesterReach = 4;
        public const int HarvesterPlainWork = 6;

        // The bot does not prospect: a base whose side is played by the computer gets this much ore a second instead
        // (World.SetBotControlled). A stand-in until the bot mines.
        public const double BotOrePerSecond = 0.5;
        // A dead tank's hold goes back into the ground as plain ore within this many cells (OreDrop).
        public const int DeathDropRadius = 6;

        // Specks: 1-3 cells of ore in dirt, anywhere but right beside a base.
        public const int SpeckEveryCells = 6000;
        public const int SpeckMinCells = 1;
        public const int SpeckMaxCells = 3;
        public const int SpeckMinDistanceFromBase = 30;

        // Pockets: irregular blobs at some distance from the bases.
        public const int PocketEveryCells = 27500;
        public const int PocketMinCells = 12;
        public const int PocketMaxCells = 30;
        public const int PocketMinDistanceFromBase = 60;

        // Lumps: large blobs with a warm core, far from the bases and biased toward the map centre
        // (uniform sample ^ bias, as for hot rock).
        public const int LumpEveryCells = 125000;
        public const int LumpMinCells = 80;
        public const int LumpMaxCells = 150;
        // A lump's core is this share of its cells (at least LumpMinCoreCells), so the largest lumps run hottest.
        public const double LumpCoreShare = 0.10;
        public const int LumpMinCoreCells = 8;
        public const int LumpMinDistanceFromBase = 130;
        public const int LumpMinDistanceBetween = 90;
        public const double LumpCentreBias = 1.3;

        // Small maps: the minimum distances above shrink with the map, by the square root of its area against
        // DistanceReferenceArea, but never below MinDistanceScale of them, and every map gets at least MinPockets
        // pockets and MinLumps lumps when they fit. The reference map (1500x750) is unchanged.
        public const int DistanceReferenceArea = 1500 * 750;
        public const double MinDistanceScale = 0.4;
        public const int MinPockets = 3;
        public const int MinLumps = 1;
    }

    public static class Base
    {
        public const int MinDistance = 150;
        public const int BaseSize = 35;
        public const int InitialHeat = 0;
        // The core's armor: the only part of a base that takes damage (the keep's shape and its gate shield are in
        // Tweaks.Keep). A tank that has got inside wears it down in about 5 seconds of fire from a courtyard corner.
        public const int InitialHealth = 6000;
        public const int HeatCapacity = 100;
        public const int HealthCapacity = 6000;
        // Radiator. At 1 unit per tick (500 energy/tick), two relays (300 each) out-pump it, so the reservoir fills
        // and pumping stops: the shared exit is the real bottleneck. Starting value for the first-prototype spike.
        public const int HeatCooldown = 1;
        // Armor the core repairs a tick while the base has power: 48 a second, so damage done stays done for a
        // while (a full repair takes two minutes) and a second raid finds the core as the first left it.
        public const int HealthRegen = 2;
        public const double DarkDamageMultiplier = 1.25;
        public const int ChargePerRestartOre = 7_000;
        public const int RestartCharge = 15 * ChargePerRestartOre;
        public const int DarkSightDivisor = 2;
        // Energy stored by one unit of base reactor heat. One bullet impact injects ~10^4 energy into the grid,
        // so the reactor (HeatCapacity units) is a reservoir worth a few hotspots, and HeatCooldown units per tick
        // is the radiator that vents it out of the system (the base's finite sink rate).
        public const float ReactorEnergyPerHeatUnit = 500f;
        // Area cooling draws from the floor and air above ambient. Its combined maximum of 480 energy per tick
        // fits the stock radiator, while competing with relay deliveries and tank cooling for the same reservoir.
        public const int AreaCoolingInnerHeatTotal = 120;
        public const int AreaCoolingMidHeatTotal = 60;
        public const int AreaCoolingOuterHeatTotal = 20;
        public const int AreaCoolingInnerAirTotal = 160;
        public const int AreaCoolingMidAirTotal = 80;
        public const int AreaCoolingOuterAirTotal = 40;
        public const int HomeCooldownHeat = 6;
        public const int HomeRechargeHealth = 3;
        // Ore banked per tick while docked: a full hold empties in a few ticks.
        public const int HomeAbsorbOre = 10;

        // Power (DESIGN.md §4.5). Heat relays deliver passes through the base engine on its way to the dimensional-link
        // cold sink; a fraction becomes charge: eta = clamp(dT / (dT + EngineTemperatureScale), 0, EngineMaxEfficiency),
        // dT is the source temperature above the reactor's. An empty reactor is at ambient and warms by up to
        // EngineColdSideRise as it fills. An empty reactor gets 20% from 30-degree dirt and about 45% from fresh hot rock.
        public const float EngineColdSideRise = 20f;
        public const float EngineTemperatureScale = 120f;
        public const float EngineMaxEfficiency = 0.5f;
        public const float ChargeCapacity = 1_000_000f;
        // Enough to run the first harvesters before any engine output.
        public const float InitialCharge = 300_000f;

        // Base status (TankBase.ChargeLevel, NetworkStatus.HeatStoreLevel, TankBase.RadiatorLoadLevel), as fractions.
        public const float ChargeWarnFill = 0.3f;           // Warn below, Bad below ChargeLowFill
        public const float ChargeLowFill = 0.1f;
        public const float HeatStoreWarnFill = 0.6f;        // Warn from here, Bad (full) from HeatStoreFullFill
        public const float HeatStoreFullFill = 0.9f;
        public const float RadiatorWarnLoad = 0.8f;         // Warn from here; Bad past 1, when the store fills
    }

    /// <summary>
    /// Upgrades bought with the base's ore store while docked (DESIGN.md §4.8): a base track and a tank track, each
    /// upgrade with MaxLevel levels. Starting values; tune in play.
    /// </summary>
    public static class Upgrades
    {
        public const int MaxLevel = 3;
        // What level 1, 2 and 3 of any upgrade cost, in ore from the base store.
        public static readonly ImmutableArray<int> LevelCosts = [25, 60, 140];
        public const int RadiatorUnitsPerLevel = 1;
        public const int ReachCellsPerLevel = 6;
        public const double EngineEfficiencyPerLevel = 0.1;
        // Each engine level multiplies the temperature scale by this, so warm sources convert better too.
        public const double EngineScalePerLevel = 0.8;
        public const float InsulationPerLevel = 0.25f;
        // Each sensors level adds this many cells to the sight of every relay and the base (not to coverage).
        public const int SensorCellsPerLevel = 8;
        // Tank track. The cargo hold takes this much more ore per level (Ore.TankHold 40, then 70, 100, 130).
        public const int CargoHoldPerLevel = 30;
        // Each radar level adds this share of every radar range, and this share of the heat budget of its rays.
        public const float RadarRangePerLevel = 0.2f;
        public const float RadarHeatBudgetPerLevel = 0.25f;
    }

    /// <summary>
    /// The sector goal (DESIGN.md §4.8): tap the hot core at the map centre. Relays within CoreTapRadius of it must
    /// make at least GoalChargePerSecond of charge, held for GoalHoldSeconds (falling short drains the hold timer at
    /// GoalHoldDecay times real time).
    /// </summary>
    public static class Goal
    {
        public const int CoreTapRadius = 22;
        public const double GoalChargePerSecond = 5000;
        public const double GoalHoldSeconds = 60;
        public const double GoalHoldDecay = 2;
    }

    public static class Tank
    {
        public static readonly TimeSpan RespawnDarkDelay = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan RespawnDelay = TimeSpan.FromSeconds(3);
        public const int TurretDelay = 3;
        public const int TurretLength = 4;
        public const int DigRadius = 3;
        public const int InitialHeat = 0;
        public const int InitialHealth = 1000;
        public const int HeatCapacity = 100;
        public const int HealthCapacity = 1000;
        public const int IdleHeatGain = 0;
        public const int MoveHeatGain = 0;

        public const float HeatMax = 100f;
        public const float HeatDigPerPixel = 0.008f;
        public const float HeatShootPerShot = 0.25f;
        // A full 40-unit hold adds 4 degrees per second before cooling.
        public const float HeatPerOrePerSecond = 0.1f;
        public const float HeatBaseCoolBonus = 0.20f;
        public const int TorchTerrainHeatAmount = 8;
        public const int TorchRockHeatAmount = 14;
        public const int TorchRockHeatRadius = 3;
        public const float TankHeatCapacity = 20f;
        // Docked at its own base, the tank transfers this share of its heat above the reactor temperature per tick,
        // in addition to ordinary conduction with the base floor. A full reservoir stops the pump.
        public const float DockHeatPumpRate = 0.04f;
        // Cold (DESIGN.md §4.7). Below ColdSlowStart the tank slows, down to ColdMinSpeed of its speed at
        // ColdSlowFull; below FrostbiteTemperature it takes FrostbiteDamagePerDegree per degree per tick, as
        // overheating does above HeatSafeMax.
        public const float ColdSlowStart = 0f;
        public const float ColdSlowFull = -40f;
        public const float ColdMinSpeed = 0.35f;
        public const float FrostbiteTemperature = -25f;
        public const float FrostbiteDamagePerDegree = 0.5f;
        public const float TankTerrainConductance = 2.0f;
        public const float TankAmbientConductance = 0.2f;
        public const float HeatSafeMax = 100f;
        public const float OverheatDamagePerDegree = 0.5f;

        // Network coverage (DESIGN.md §4.6): outside it the tank's heat floor rises at OffNetworkHeatPerSecond and the
        // tank cannot cool below it; inside it the tank sheds up to NetworkCoolingPerSecond (times the coverage
        // strength) down the links into its base's reactor, never below zero, and the floor falls at that rate.
        // From 0, idle off network lasts HeatSafeMax / OffNetworkHeatPerSecond = 100 s (longer for a tank that left
        // cold, after a frost pocket); digging near the end shortens it.
        public const float OffNetworkHeatPerSecond = 1.0f;
        public const float NetworkCoolingPerSecond = 6.0f;

        // Player status (Tank.IsRunningHot, IsHullLow, HullLevel, ...). Warnings latch: on past the first
        // threshold, off again only past the gentler second one, so they do not flicker around it.
        public const float RunningHotOnFraction = 0.80f;    // of HeatSafeMax
        public const float RunningHotOffFraction = 0.70f;
        public const float HullLowOnFraction = 0.25f;     // of the hull's capacity
        public const float HullLowOffFraction = 0.30f;
        public const float HullWarnFraction = 0.5f;       // HullLevel: Ok above this, Bad at or below HullLowOnFraction
        public const float OffNetworkUrgentSeconds = 20f;   // off network, this close to overheating is urgent
        // Heat above 0 a docked tank must still shed before it counts as cooled down (Tank.IsCoolingDown).
        public const float FullyCooledMargin = 1f;
        // The heat gauge runs from this multiple of FrostbiteTemperature to this multiple of HeatSafeMax.
        public const float HeatGaugeFrostMargin = 1.6f;
        public const float HeatGaugeOverheatMargin = 1.25f;
    }

    /// <summary>Network coverage: the ground the base and its powered relays cool for their tanks.</summary>
    public static class Coverage
    {
        public const int NodeRadius = 20;
        public const int BaseRadius = Base.BaseSize / 2 + NodeRadius;
        // Cooling strength falls by this factor per live link between a node and its base.
        public const float HopFalloff = 0.85f;
    }

    /// <summary>
    /// Fog of war: what a side sees (<c>Sensing/VisionMap</c>). Presentation only; the simulation never reads vision.
    /// </summary>
    public static class Vision
    {
        // A tank sees this far along open tunnel...
        public const int TankRadius = 45;
        // ...and everything this close, through solid ground too, so digging is not blind.
        public const int TankNearRadius = 7;
        // A machine sees this close around itself. A link node (relay, generator) sees its whole coverage circle
        // (Coverage), through solid ground too.
        public const int MachineNearRadius = 6;
        // A link node sees this many cells beyond its coverage circle, before any sensors upgrade.
        public const int NodeSightBonus = 10;
        // The walls around open ground in sight are seen this many cells deep, fading with depth.
        public const int WallSkin = 5;
        // The level falls from full to nothing over the last cells of a source's reach (a short reach: its outer
        // half), so sight has no hard edge.
        public const int EdgeFade = 12;
        // Side of the tiles the vision map files its sources under, so a cell finds the sources that can see it.
        public const int SourceTileSize = 32;
    }

    /// <summary>
    /// The tank's radar (<c>Sensing/Radar</c>): pulses that run outward from the tank and leave blips where they
    /// reflect, well beyond sight. Presentation only, like vision.
    /// </summary>
    public static class Radar
    {
        // How far a pulse reaches, in cells. Only a large rock formation returns from that far; everything else
        // returns from nearer (below) and beyond its range blocks the ray without a blip.
        public const int Range = 150;
        // Rock, concrete and other sides' base walls: a thin shell returns from SolidRangeMin, growing to Range for
        // a formation FormationDepth cells deep along the ray.
        public const int SolidRangeMin = 60;
        public const int FormationDepth = 10;
        public const int MachineRange = 75;
        public const int LifeRange = 60;
        // A tank's echo depends on its heat: none below SignatureFloorHeat, then LifeRange growing linearly to its full
        // value at SignatureFullHeat (Sensing/TankSignature).
        public const float SignatureFloorHeat = 45f;
        public const float SignatureFullHeat = 100f;
        // The weak return of a tunnel or cave behind dirt.
        public const int CavityRange = 50;
        // Pyrite ore: a speck returns only from OreRangeMin, growing to OreRangeMax for a deposit FormationDepth
        // cells deep along the ray. Only cool plain ore counts toward the depth, so a lump's warm heart adds nothing:
        // a lump shows as heat (below) and what it holds is learned by going there.
        public const int OreRangeMin = 40;
        public const int OreRangeMax = 110;
        // Hot ground: barely warm from HeatRangeMin, at HeatFullTemperature and above from HeatRangeMax.
        public const int HeatRangeMin = 50;
        public const int HeatRangeMax = 120;
        // The tank sends a pulse every PulsePeriodTicks (4 s). Its front runs FrontCellsPerTick outward a tick, so
        // it reaches Range in 75 ticks (3.1 s) and the radar is quiet for the rest of the period. The period must
        // outlast the trip: one pulse is in flight at a time. The radar upgrade speeds the front up with the range
        // (RadarReach), so the trip always takes 75 ticks.
        public const int PulsePeriodTicks = 4 * Perf.TargetFps;
        public const int FrontCellsPerTick = 2;
        // A pulse is a fan of this many rays, turned a fraction of a ray spacing each pulse so pulses fill each
        // other's gaps. While a pulse travels each ray walks the cells the front crossed: about
        // RaysPerPulse * FrontCellsPerTick * 1.27 = 285 cells a tick (1.27 is the mean cells per cell of distance of
        // a walk that never cuts a corner), against 600 for the beam this replaced.
        public const int RaysPerPulse = 112;
        // A blip fades out over this many ticks (10 s): two and a half pulses, so the next pulse checks it first.
        public const int ContactLifetimeTicks = 10 * Perf.TargetFps;
        // Open ground behind dirt (a tunnel, a cave) gives a weak return and lets the ray on, this many times a ray.
        public const int WeakReturnsPerRay = 3;
        // A tank reflects as a square this many cells either side of its centre.
        public const int TankHalfSize = 3;
        // Heat uses the pulse up. A ray can cross HeatBudgetDegreeCells in all: every cell hotter than HeatThreshold
        // takes its degrees above the threshold, per cell of path, and the ray stops when the budget is gone. That
        // is about 3 cells of hot rock at 100, 6 cells of ground at 50, 25 cells at 20: the hotter the surroundings,
        // the shorter the radar reaches.
        public const float HeatThreshold = 10f;
        public const float HeatBudgetDegreeCells = 250f;
        // Each hot cell a ray goes through is marked with how hot it is, at most this many a ray. The mark is at
        // full intensity at HeatFullTemperature (hot rock) and above.
        public const int HeatReturnsPerRay = 16;
        public const float HeatFullTemperature = 100f;
        // A reflector this hot returns only its heat: the blip says "heat", not what the thing is.
        public const float HeatScrambleTemperature = 40f;
    }

    public static class Weapon
    {
        public const int CannonBulletSpeed = 3;
        public const int BotBaseAttackRange = 48;
        // The bot leaves the player's base alone for this long, so the opening (and the tutorial) can be played: a
        // base takes about thirteen shots to fall, and a bot that arrived in ten seconds used to end the match there.
        public const int BotBaseSiegeGraceSeconds = 240;
        public const int BulletDamage = 160;
    }

    public static class Machine
    {
        public const int ReactorHeatCapacity = 100;
        public const int ReactorHealthCapacity = 1000;
        // A harvester acts (works ore in reach, or crawls one cell) every this many milliseconds: it crawls about 6.7
        // cells a second. Working a cell takes Ore.HarvesterPlainWork of these, so the mining rate does not depend on it.
        public const int HarvesterIntervalMs = 150;
        // A harvester roams and mines within this distance of where it was planted.
        public const int HarvestRange = 20;
        // It clears the dirt (worth nothing) within this radius as it crawls, so its tunnel stays open; what it mines
        // is ore (Ore.HarvesterReach).
        public const int HarvesterMineRadius = 2;
        // Work runs on charge, and spent charge ends up as heat in the ground around the harvester: each action that
        // works ore spends this much, a crawl step spends HarvesterCrawlCharge.
        // Starting guesses for the first-prototype spike; tune in play.
        public const float HarvesterHeatPerHarvest = 500f;
        public const float HarvesterCrawlCharge = 200f;
        // On the energy network (DESIGN.md §4.9) a harvester pays from its capacitor: about 40 s of mining when full,
        // and it is planted full. It refills at up to this much a tick while its base's network covers it (more than
        // working spends, about 140 a tick), and it spends HarvesterIdleDraw a tick just standing, so a cut-off
        // harvester idles flat in about 20 minutes. Without the network it pays straight from its base's store.
        public const float HarvesterCapacitor = 120_000f;
        public const float HarvesterTransferPerTick = 400f;
        public const float HarvesterIdleDraw = 4f;
        public const int HarvesterHeatRadius = 2;
        // A harvester stalls while the ground around it is hotter than this (average over the sense radius), so the
        // heat it makes can never run away: it keeps itself bounded, and a relay that pumps the heat out is what
        // lets it work at full speed. Starting guess for the spike; tune in play.
        public const float HarvesterStallTemperature = 30f;
        public const int HarvesterHeatSenseRadius = 4;
        // Ore on a cell hotter than this is left alone, as if it were out of reach: it is the shell of a lump whose core
        // is still in (55 to 80 degrees; ore a harvester works warms to about 25), and a harvester that went for it would
        // sit stalled at the edge for ever. Cooled below this (a relay beside it, or the core out), it is worked again.
        public const float HarvesterHotOreTemperature = 45f;

        public const int BoundingBoxHalfSize = 2;
        // A destroyed machine scorches the open ground and dirt within this half-width of its centre; regrowth fades it.
        public const int WreckScorchHalfSize = 2;

        // Relay: a heat pipe. Planted within line of sight of a powered link network it pulls heat out of the
        // surrounding square (half-width RelayCoolingRadius) and pumps it into its owner's base reactor.
        public const int RelayCoolingRadius = 12;
        public const float RelayHeatPumpPerTick = 300f;
        // A relay's capacitor on the energy network: cut off, its idle draw empties a full one in 100 seconds.
        public const float GeneratorCapacitor = 4800f;
        public const float GeneratorTransferPerTick = 100f;
        public const float GeneratorIdleDraw = 2f;
        public const float GeneratorReserveCapacity = 4800f;
        public const int GeneratorIntakeRadius = 6;
        public const int GeneratorExhaustRadius = 3;
        public const float GeneratorMinTemperatureDifference = 0.01f;
        public const double GeneratorMaxEfficiency = 0.35;
        public const double GeneratorTemperatureScale = 80.0;
        public const double GeneratorMaxHeatPerTick = 220.0;
        public const double GeneratorMaxChargePerTick = 65.0;

        public const float RelayCapacitor = 3600f;
        public const float RelayTransferPerTick = 60f;
        public const float RelayIdleDraw = 1.5f;
    }

    /// <summary>
    /// The energy network (DESIGN.md §4.9, <c>EnergyNetwork</c>): every machine's capacitor, fed by its base's island.
    /// Each machine's capacitor numbers are in <see cref="Machine"/>.
    /// </summary>
    public static class Energy
    {
        // A capacitor at rest books the heat of its idle draw into the ground once in this many ticks (spread over the
        // ticks, a share of the machines each), not every tick.
        public const int SettlePeriodTicks = 5 * Perf.TargetFps;
        // A tier the island starved is fed again once the store holds this many ticks of its idle draw, so a store
        // living on its income does not switch a tier on and off every tick.
        public const int RefeedIdleTicks = Perf.TargetFps;
        // The window the charge flows the overlay reads are summed over (one second).
        public const int FlowWindowTicks = Perf.TargetFps;
        // Spent idle charge becomes heat within this radius of the machine.
        public const int IdleHeatRadius = 2;
    }

    /// <summary>
    /// Per-machine upgrades (DESIGN.md §4.9, <c>MachineUpgradeSystem</c>): bought next to a machine from the base's ore
    /// store, one path per machine, MaxLevel levels. Starting values; tune in play.
    /// </summary>
    public static class MachineUpgrades
    {
        public const int MaxLevel = 2;
        // What level 1 and 2 cost, in ore from the base store.
        public static readonly ImmutableArray<int> LevelCosts = [8, 16];
        // The tank buys for the nearest own machine within this many cells of it.
        public const int Reach = 12;
        // How long a refused press is shown, in ticks.
        public const int RefusalTicks = 48;
        // Tap: each level pumps this share more heat, and spends this much more charge per tick standing (as heat).
        public const double TapPumpPerLevel = 0.5;
        public const double TapIdleDrawPerLevel = 1.5;
        // Sensor: each level adds this many cells to the machine's sight.
        public const int SensorCellsPerLevel = 10;
        // Conduit: the capacitor's size at level 0, 1 and 2, as a multiple of the machine's own.
        public static readonly ImmutableArray<double> ConduitCapacityScale = [1.0, 1.5, 2.0];
    }

    public static class Explosion
    {
        public const int ChanceToDestroyConcrete = 50;
        public const int ChanceToDestroyRock = 50;
        public const float SpeedVarianceBase = 0.1f;
        public const float SpeedVarianceRange = 0.9f;

        public static readonly ExplosionParams Dirt = new(ShrapnelCount: 10, Speed: 0.375f, Frames: 10);
        public static readonly ExplosionParams Normal = new(ShrapnelCount: 14, Speed: 0.56f, Frames: 13);
        public static readonly ExplosionParams Death = new(ShrapnelCount: 100, Speed: 0.25f, Frames: 72);
        public const float BulletExplosionHeatScale = 1.5f;
        public const float DeathHeatScale = 1.0f;
        // A destroyed machine bursts once, smaller than a tank's death.
        public static readonly ExplosionParams MachineWreck = new(ShrapnelCount: 24, Speed: 0.4f, Frames: 20);
        public const float MachineWreckHeatScale = 1.0f;

        public const int BulletHeatAmount = 3;
        public const int BulletHeatRadius = 7;
        public const int ShrapnelHitHeat = 2;
        public const int ShrapnelDigHeatAmount = 2;
        public const int ShrapnelDigHeatRadius = 5;
    }

    public static class Colors
    {
        public static readonly Color FireHot = new(0xff, 0x34, 0x08);
        public static readonly Color Harvester = new(0x00, 0x88, 0x00);
        public static readonly Color Generator = new(0xc2, 0x9c, 0xff);
        public static readonly Color Relay = new(0x20, 0xd8, 0xe8);
    }

    public static class LevelGen
    {
        public const int BorderWidth = 30;
        public const int MaxDirtSpawnOdds = 300;
        public const int DirtSpawnProgression = 70;
        public const int DirtTargetPercent = 65;
        public const int TreeSize = 150;
        // Bases on a map unless a match asks for another number (up to World.MaxPlayers).
        public const int DefaultPlayers = 2;
        public const int SmoothingSteps = -1; // -1 = smooth until convergence (matches C++)
        public static int TargetDirtAmount(Size size) => size.X * size.Y * DirtTargetPercent / 100;

        public const int RuinAreaPerRuin = 16000;
        public const int RuinWallMinLength = 2;
        public const int RuinWallMaxLength = 5;
    }

    /// <summary>
    /// The computer opponent's heat discipline. It reads the same heat model as the player's gauge: fractions are of
    /// <c>Tank.HeatBudgetCapacity</c> (the safe heat range plus a full coolant charge), so a pre-cooled tank has more
    /// room before it heads home than a warm one.
    /// </summary>
    public static class Bot
    {
        // Roaming ends and the bot heads home when its heat budget falls below this share (heat above 70 of 100).
        public const float ReturnHeatBudgetFraction = 0.20f;
        // Off the network the tank cannot cool below its rising heat floor, so the trip home only adds heat: it
        // turns back earlier (heat above 47.5).
        public const float ReturnHeatBudgetFractionOffNetwork = 0.35f;
        // Docked, the bot waits until it is fully pre-cooled (Tank.IsCoolingDown is false). If its heat stops falling
        // for RechargeStallFrames (the base's reservoir is full), it leaves once it has at least this share.
        public const float RechargeStalledMinHeatBudgetFraction = 0.60f;
        public const int RechargeStallFrames = Perf.TargetFps;
    }
}
