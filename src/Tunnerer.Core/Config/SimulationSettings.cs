namespace Tunnerer.Core.Config;

using Tunnerer.Core.Thermal;

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

    // --- Thermal materials: one row per ThermalMaterial, pair conductances derived from the rows ---
    public ThermalMaterialTable ThermalMaterials { get; init; }

    // --- Air-field coupling ---
    public float ThermalAirCellCoupling { get; init; }
    public float ThermalAirNeighborCoupling { get; init; }

    // --- Tank heat model ---
    public float TankHeatDigPerPixel { get; init; }
    public float TankHeatShootPerShot { get; init; }
    public float TankHeatCapacity { get; init; }
    public float TankCoolantCapacity { get; init; }
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
        ThermalMaterials = Tweaks.World.ThermalMaterials,
        ThermalAirCellCoupling = Tweaks.World.ThermalAirCellCoupling,
        ThermalAirNeighborCoupling = Tweaks.World.ThermalAirNeighborCoupling,
        TankHeatDigPerPixel = Tweaks.Tank.HeatDigPerPixel,
        TankHeatShootPerShot = Tweaks.Tank.HeatShootPerShot,
        TankHeatCapacity = Tweaks.Tank.TankHeatCapacity,
        TankCoolantCapacity = Tweaks.Tank.CoolantCapacity,
        TankTerrainConductance = Tweaks.Tank.TankTerrainConductance,
        TankAmbientConductance = Tweaks.Tank.TankAmbientConductance,
        TankHeatSafeMax = Tweaks.Tank.HeatSafeMax,
        TankOverheatDamagePerDegree = Tweaks.Tank.OverheatDamagePerDegree,
        TankOffNetworkHeatPerTick = Tweaks.Tank.OffNetworkHeatPerSecond / Tweaks.Perf.TargetFps,
        TankNetworkCoolingPerTick = Tweaks.Tank.NetworkCoolingPerSecond / Tweaks.Perf.TargetFps,
        BaseSize = Tweaks.Base.BaseSize,
    };
}
