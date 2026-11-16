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

    // --- Sparse tile acceleration ---
    public int ThermalActiveTileSize { get; init; }
    public float ThermalSparseFallbackCoverage { get; init; }
    public int ThermalParallelRegionThreshold { get; init; }
    public int ThermalMaxWorkers { get; init; }
    public float ThermalMaxDeltaSleepThreshold { get; init; }
    public float ThermalActiveTemperatureThreshold { get; init; }

    // --- Sleep and saturation throttle ---
    public int ThermalNoActiveSleepFrames { get; init; }
    public float ThermalSaturationCoverageThreshold { get; init; }
    public int ThermalSaturationFramesBeforeThrottle { get; init; }
    public int ThermalSaturationStepIntervalFrames { get; init; }

    // --- Thermal engine constants ---
    public float ThermalDt { get; init; }
    public float ThermalAmbientTemperature { get; init; }
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
    public float ThermalFixedFrostRockTemperature { get; init; }

    // --- Tank heat model ---
    public float TankHeatDigPerPixel { get; init; }
    public float TankHeatShootPerShot { get; init; }
    public float TankHeatCapacity { get; init; }
    public float TankFrostbiteTemperature { get; init; }
    public float TankFrostbiteDamagePerDegree { get; init; }
    public float TankTerrainConductance { get; init; }
    public float TankAmbientConductance { get; init; }
    public float TankHeatSafeMax { get; init; }
    public float TankOverheatDamagePerDegree { get; init; }

    // --- Tank network cooling ---
    public float TankOffNetworkHeatPerTick { get; init; }
    public float TankNetworkCoolingPerTick { get; init; }

    // --- Base ---
    public int BaseSize { get; init; }

    public static SimulationSettings FromTweaks() => new()
    {
        AdvanceStep = Tweaks.World.AdvanceStep,
        ThermalActiveTileSize = Tweaks.World.ThermalActiveTileSize,
        ThermalSparseFallbackCoverage = Tweaks.World.ThermalSparseFallbackCoverage,
        ThermalParallelRegionThreshold = Tweaks.World.ThermalParallelRegionThreshold,
        ThermalMaxWorkers = Tweaks.World.ThermalMaxWorkers,
        ThermalMaxDeltaSleepThreshold = Tweaks.World.ThermalMaxDeltaSleepThreshold,
        ThermalActiveTemperatureThreshold = Tweaks.World.ThermalActiveTemperatureThreshold,
        ThermalNoActiveSleepFrames = Tweaks.World.ThermalNoActiveSleepFrames,
        ThermalSaturationCoverageThreshold = Tweaks.World.ThermalSaturationCoverageThreshold,
        ThermalSaturationFramesBeforeThrottle = Tweaks.World.ThermalSaturationFramesBeforeThrottle,
        ThermalSaturationStepIntervalFrames = Tweaks.World.ThermalSaturationStepIntervalFrames,
        ThermalDt = Tweaks.World.ThermalDt,
        ThermalAmbientTemperature = Tweaks.World.ThermalAmbientTemperature,
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
        ThermalFixedFrostRockTemperature = Tweaks.World.ThermalFixedFrostRockTemperature,
        TankHeatDigPerPixel = Tweaks.Tank.HeatDigPerPixel,
        TankHeatShootPerShot = Tweaks.Tank.HeatShootPerShot,
        TankHeatCapacity = Tweaks.Tank.TankHeatCapacity,
        TankFrostbiteTemperature = Tweaks.Tank.FrostbiteTemperature,
        TankFrostbiteDamagePerDegree = Tweaks.Tank.FrostbiteDamagePerDegree,
        TankTerrainConductance = Tweaks.Tank.TankTerrainConductance,
        TankAmbientConductance = Tweaks.Tank.TankAmbientConductance,
        TankHeatSafeMax = Tweaks.Tank.HeatSafeMax,
        TankOverheatDamagePerDegree = Tweaks.Tank.OverheatDamagePerDegree,
        TankOffNetworkHeatPerTick = Tweaks.Tank.OffNetworkHeatPerSecond / Tweaks.Perf.TargetFps,
        TankNetworkCoolingPerTick = Tweaks.Tank.NetworkCoolingPerSecond / Tweaks.Perf.TargetFps,
        BaseSize = Tweaks.Base.BaseSize,
    };
}
