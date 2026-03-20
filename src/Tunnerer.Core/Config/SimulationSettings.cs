namespace Tunnerer.Core.Config;

/// <summary>
/// Injectable configuration for the thermal simulation engine and tank heat model.
/// Tests can construct custom instances to override any parameter.
/// Production code uses <see cref="FromTweaks"/>.
/// </summary>
public readonly record struct SimulationSettings
{
    // --- Thermal simulation flags ---
    public bool EnableMaterialHeatExchange { get; init; }
    public bool EnableThermalAmbientExchange { get; init; }
    public bool EnableStoneAmbientExchange { get; init; }
    public bool EnableConservativeAirField { get; init; }

    // --- Sparse tile acceleration ---
    public int ThermalActiveTileSize { get; init; }
    public float ThermalSparseFallbackCoverage { get; init; }
    public int ThermalParallelRegionThreshold { get; init; }
    public int ThermalMaxWorkers { get; init; }
    public float ThermalActiveTemperatureThreshold { get; init; }

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

    // --- Ambient exchange ---
    public float ThermalKAmbientAir { get; init; }
    public float ThermalKAmbientDirt { get; init; }
    public float ThermalKAmbientStone { get; init; }
    public float ThermalKAmbientBase { get; init; }
    public float ThermalKAmbientConstantEnergy { get; init; }

    // --- Fixed temperature materials ---
    public float ThermalFixedBaseTemperature { get; init; }
    public float ThermalFixedConstantEnergyTemperature { get; init; }

    // --- Tank heat model ---
    public float TankHeatDigPerPixel { get; init; }
    public float TankHeatShootPerShot { get; init; }
    public float TankHeatCapacity { get; init; }
    public float TerrainHeatCapacity { get; init; }
    public float TankTerrainConductance { get; init; }
    public float TankAmbientConductance { get; init; }
    public float TankHeatSafeMax { get; init; }
    public float TankOverheatDamagePerDegree { get; init; }

    public static SimulationSettings FromTweaks() => new()
    {
        EnableMaterialHeatExchange = Tweaks.World.EnableMaterialHeatExchange,
        EnableThermalAmbientExchange = Tweaks.World.EnableThermalAmbientExchange,
        EnableStoneAmbientExchange = Tweaks.World.EnableStoneAmbientExchange,
        EnableConservativeAirField = Tweaks.World.EnableConservativeAirField,
        ThermalActiveTileSize = Tweaks.World.ThermalActiveTileSize,
        ThermalSparseFallbackCoverage = Tweaks.World.ThermalSparseFallbackCoverage,
        ThermalParallelRegionThreshold = Tweaks.World.ThermalParallelRegionThreshold,
        ThermalMaxWorkers = Tweaks.World.ThermalMaxWorkers,
        ThermalActiveTemperatureThreshold = Tweaks.World.ThermalActiveTemperatureThreshold,
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
        ThermalKAmbientAir = Tweaks.World.ThermalKAmbientAir,
        ThermalKAmbientDirt = Tweaks.World.ThermalKAmbientDirt,
        ThermalKAmbientStone = Tweaks.World.ThermalKAmbientStone,
        ThermalKAmbientBase = Tweaks.World.ThermalKAmbientBase,
        ThermalKAmbientConstantEnergy = Tweaks.World.ThermalKAmbientConstantEnergy,
        ThermalFixedBaseTemperature = Tweaks.World.ThermalFixedBaseTemperature,
        ThermalFixedConstantEnergyTemperature = Tweaks.World.ThermalFixedConstantEnergyTemperature,
        TankHeatDigPerPixel = Tweaks.Tank.HeatDigPerPixel,
        TankHeatShootPerShot = Tweaks.Tank.HeatShootPerShot,
        TankHeatCapacity = Tweaks.Tank.TankHeatCapacity,
        TerrainHeatCapacity = Tweaks.Tank.TerrainHeatCapacity,
        TankTerrainConductance = Tweaks.Tank.TankTerrainConductance,
        TankAmbientConductance = Tweaks.Tank.TankAmbientConductance,
        TankHeatSafeMax = Tweaks.Tank.HeatSafeMax,
        TankOverheatDamagePerDegree = Tweaks.Tank.OverheatDamagePerDegree,
    };
}
