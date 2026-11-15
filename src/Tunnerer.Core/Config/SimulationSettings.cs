namespace Tunnerer.Core.Config;

/// <summary>
/// Injectable configuration for the thermal simulation engine and tank heat model.
/// Tests can construct custom instances to override any parameter.
/// Production code uses <see cref="FromTweaks"/>.
/// </summary>
public readonly record struct SimulationSettings
{
    // --- Simulation timing ---
    public TimeSpan AdvanceStep { get; init; }

    // --- Thermal simulation flags ---
    public bool EnableMaterialHeatExchange { get; init; }
    public bool EnableConservativeAirField { get; init; }

    // --- Sparse tile acceleration ---
    public int ThermalActiveTileSize { get; init; }
    public float ThermalSparseFallbackCoverage { get; init; }
    public int ThermalParallelRegionThreshold { get; init; }
    public int ThermalMaxWorkers { get; init; }
    public float ThermalMaxDeltaSleepThreshold { get; init; }
    public float ThermalActiveTemperatureThreshold { get; init; }

    // --- Thermal engine constants ---
    public float ThermalDt { get; init; }
    public float ThermalArtificialCoolingPerFrame { get; init; }

    // --- Material heat capacities ---
    public float ThermalCapacityAir { get; init; }
    public float ThermalCapacityDirt { get; init; }
    public float ThermalCapacityStone { get; init; }
    public float ThermalCapacityBase { get; init; }
    public float ThermalCapacityConstantEnergy { get; init; }

    // --- Pairwise conductance ---
    public float ThermalKAirAir { get; init; }
    public float ThermalKAirDirt { get; init; }
    public float ThermalKAirStone { get; init; }
    public float ThermalKAirBase { get; init; }
    public float ThermalKDirtDirt { get; init; }
    public float ThermalKDirtStone { get; init; }
    public float ThermalKDirtBase { get; init; }
    public float ThermalKStoneStone { get; init; }
    public float ThermalKStoneBase { get; init; }
    public float ThermalKBaseBase { get; init; }
    public float ThermalKAirConstantEnergy { get; init; }
    public float ThermalKDirtConstantEnergy { get; init; }
    public float ThermalKStoneConstantEnergy { get; init; }
    public float ThermalKBaseConstantEnergy { get; init; }
    public float ThermalKConstantEnergyConstantEnergy { get; init; }

    // --- Air-field coupling ---
    public float ThermalAirCellCoupling { get; init; }
    public float ThermalAirNeighborCoupling { get; init; }

    // --- Fixed temperature materials ---
    public float ThermalFixedBaseTemperature { get; init; }
    public float ThermalFixedConstantEnergyTemperature { get; init; }
    public float ThermalFixedHotRockTemperature { get; init; }

    // --- Tank heat model ---
    public float TankHeatDigPerPixel { get; init; }
    public float TankHeatShootPerShot { get; init; }
    public float TankHeatCapacity { get; init; }
    public float TankCoolantCapacity { get; init; }
    public float TankTerrainConductance { get; init; }
    public float TankAmbientConductance { get; init; }
    public float TankHeatSafeMax { get; init; }
    public float TankOverheatDamagePerDegree { get; init; }

    // --- Tank gameplay ---
    public int TankMaxLives { get; init; }
    public int TankInitialHeat { get; init; }
    public int TankInitialHealth { get; init; }
    public int TankHeatCapacityValue { get; init; }
    public int TankHealthCapacityValue { get; init; }
    public int TankResourceDirtCapacity { get; init; }
    public int TankResourceMineralsCapacity { get; init; }
    public float TankIdleHeatGain { get; init; }
    public float TankMoveHeatGain { get; init; }
    public float TankOffNetworkHeatPerTick { get; init; }
    public float TankNetworkCoolingPerTick { get; init; }
    public int TankDigRadius { get; init; }
    public float TankHeatBaseCoolBonus { get; init; }
    public int TankTorchTerrainHeatAmount { get; init; }
    public int TankTorchRockHeatAmount { get; init; }
    public int TankTorchRockHeatRadius { get; init; }
    public int TankCoolingPickupGold { get; init; }
    public TimeSpan TankRespawnDelay { get; init; }

    // --- Base interaction ---
    public int BaseHomeCooldownHeat { get; init; }
    public int BaseForeignCooldownHeat { get; init; }
    public int BaseSize { get; init; }
    public int BaseHomeAbsorbDirt { get; init; }
    public int BaseHomeAbsorbMinerals { get; init; }

    // --- World gameplay ---
    public int DigThroughRockChance { get; init; }

    public static SimulationSettings FromTweaks() => new()
    {
        AdvanceStep = Tweaks.World.AdvanceStep,
        EnableMaterialHeatExchange = Tweaks.World.EnableMaterialHeatExchange,
        EnableConservativeAirField = Tweaks.World.EnableConservativeAirField,
        ThermalActiveTileSize = Tweaks.World.ThermalActiveTileSize,
        ThermalSparseFallbackCoverage = Tweaks.World.ThermalSparseFallbackCoverage,
        ThermalParallelRegionThreshold = Tweaks.World.ThermalParallelRegionThreshold,
        ThermalMaxWorkers = Tweaks.World.ThermalMaxWorkers,
        ThermalMaxDeltaSleepThreshold = Tweaks.World.ThermalMaxDeltaSleepThreshold,
        ThermalActiveTemperatureThreshold = Tweaks.World.ThermalActiveTemperatureThreshold,
        ThermalDt = Tweaks.World.ThermalDt,
        ThermalArtificialCoolingPerFrame = Tweaks.World.ThermalArtificialCoolingPerFrame,
        ThermalCapacityAir = Tweaks.World.ThermalCapacityAir,
        ThermalCapacityDirt = Tweaks.World.ThermalCapacityDirt,
        ThermalCapacityStone = Tweaks.World.ThermalCapacityStone,
        ThermalCapacityBase = Tweaks.World.ThermalCapacityBase,
        ThermalCapacityConstantEnergy = Tweaks.World.ThermalCapacityConstantEnergy,
        ThermalKAirAir = Tweaks.World.ThermalKAirAir,
        ThermalKAirDirt = Tweaks.World.ThermalKAirDirt,
        ThermalKAirStone = Tweaks.World.ThermalKAirStone,
        ThermalKAirBase = Tweaks.World.ThermalKAirBase,
        ThermalKDirtDirt = Tweaks.World.ThermalKDirtDirt,
        ThermalKDirtStone = Tweaks.World.ThermalKDirtStone,
        ThermalKDirtBase = Tweaks.World.ThermalKDirtBase,
        ThermalKStoneStone = Tweaks.World.ThermalKStoneStone,
        ThermalKStoneBase = Tweaks.World.ThermalKStoneBase,
        ThermalKBaseBase = Tweaks.World.ThermalKBaseBase,
        ThermalKAirConstantEnergy = Tweaks.World.ThermalKAirConstantEnergy,
        ThermalKDirtConstantEnergy = Tweaks.World.ThermalKDirtConstantEnergy,
        ThermalKStoneConstantEnergy = Tweaks.World.ThermalKStoneConstantEnergy,
        ThermalKBaseConstantEnergy = Tweaks.World.ThermalKBaseConstantEnergy,
        ThermalKConstantEnergyConstantEnergy = Tweaks.World.ThermalKConstantEnergyConstantEnergy,
        ThermalAirCellCoupling = Tweaks.World.ThermalAirCellCoupling,
        ThermalAirNeighborCoupling = Tweaks.World.ThermalAirNeighborCoupling,
        ThermalFixedBaseTemperature = Tweaks.World.ThermalFixedBaseTemperature,
        ThermalFixedConstantEnergyTemperature = Tweaks.World.ThermalFixedConstantEnergyTemperature,
        ThermalFixedHotRockTemperature = Tweaks.World.ThermalFixedHotRockTemperature,
        TankHeatDigPerPixel = Tweaks.Tank.HeatDigPerPixel,
        TankHeatShootPerShot = Tweaks.Tank.HeatShootPerShot,
        TankHeatCapacity = Tweaks.Tank.TankHeatCapacity,
        TankCoolantCapacity = Tweaks.Tank.CoolantCapacity,
        TankTerrainConductance = Tweaks.Tank.TankTerrainConductance,
        TankAmbientConductance = Tweaks.Tank.TankAmbientConductance,
        TankHeatSafeMax = Tweaks.Tank.HeatSafeMax,
        TankOverheatDamagePerDegree = Tweaks.Tank.OverheatDamagePerDegree,
        TankMaxLives = Tweaks.Tank.MaxLives,
        TankInitialHeat = Tweaks.Tank.InitialHeat,
        TankInitialHealth = Tweaks.Tank.InitialHealth,
        TankHeatCapacityValue = Tweaks.Tank.HeatCapacity,
        TankHealthCapacityValue = Tweaks.Tank.HealthCapacity,
        TankResourceDirtCapacity = Tweaks.Tank.ResourceDirtCapacity,
        TankResourceMineralsCapacity = Tweaks.Tank.ResourceMineralsCapacity,
        TankIdleHeatGain = Tweaks.Tank.IdleHeatGain,
        TankMoveHeatGain = Tweaks.Tank.MoveHeatGain,
        TankOffNetworkHeatPerTick = Tweaks.Tank.OffNetworkHeatPerSecond / Tweaks.Perf.TargetFps,
        TankNetworkCoolingPerTick = Tweaks.Tank.NetworkCoolingPerSecond / Tweaks.Perf.TargetFps,
        TankDigRadius = Tweaks.Tank.DigRadius,
        TankHeatBaseCoolBonus = Tweaks.Tank.HeatBaseCoolBonus,
        TankTorchTerrainHeatAmount = Tweaks.Tank.TorchTerrainHeatAmount,
        TankTorchRockHeatAmount = Tweaks.Tank.TorchRockHeatAmount,
        TankTorchRockHeatRadius = Tweaks.Tank.TorchRockHeatRadius,
        TankCoolingPickupGold = Tweaks.Tank.CoolingPickupGold,
        TankRespawnDelay = Tweaks.Tank.RespawnDelay,
        BaseHomeCooldownHeat = Tweaks.Base.HomeCooldownHeat,
        BaseForeignCooldownHeat = Tweaks.Base.ForeignCooldownHeat,
        BaseSize = Tweaks.Base.BaseSize,
        BaseHomeAbsorbDirt = Tweaks.Base.HomeAbsorbDirt,
        BaseHomeAbsorbMinerals = Tweaks.Base.HomeAbsorbMinerals,
        DigThroughRockChance = Tweaks.World.DigThroughRockChance,
    };
}
