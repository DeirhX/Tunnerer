namespace Emberward.Core.Config;

using Emberward.Core.Snapshots;
using Emberward.Core.Thermal;

/// <summary>
/// Injectable configuration for the thermal simulation engine and tank heat model.
/// Tests can construct custom instances to override any parameter.
/// Production code uses <see cref="FromTweaks"/>.
/// </summary>
[NotSaved("fixed for the match: built from the match settings")]
public readonly record struct SimulationSettings
{
    // --- Simulation timing ---
    public TimeSpan AdvanceStep { get; init; }

    // --- Sparse tile acceleration ---
    public int ThermalActiveTileSize { get; init; }
    public int ThermalParallelTileThreshold { get; init; }
    public int ThermalMaxWorkers { get; init; }
    public float ThermalSettledGrowStep { get; init; }
    public float ThermalSettledFadeStep { get; init; }
    public float ThermalWakeTemperature { get; init; }
    public float ThermalFlowCutoff { get; init; }

    // --- Saturation throttle ---
    public float ThermalSaturationCoverageThreshold { get; init; }
    public int ThermalSaturationFramesBeforeThrottle { get; init; }
    public int ThermalSaturationStepIntervalFrames { get; init; }

    // --- Thermal engine constants ---
    public float ThermalDt { get; init; }
    public float ThermalAmbientTemperature { get; init; }

    // --- Thermal materials: one row per ThermalMaterial, pair conductances derived from the rows ---
    public ThermalMaterialTable ThermalMaterials { get; init; }

    // --- Air-field coupling ---
    public float ThermalAirCellCoupling { get; init; }
    public float ThermalAirNeighborCoupling { get; init; }

    // --- Tank heat model ---
    public float TankHeatDigPerPixel { get; init; }
    public float TankHeatShootPerShot { get; init; }
    public float TankHeatPerOrePerTick { get; init; }
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

    // --- Tank thermals (TankThermals): work heat, docking-area cooling and the dock heat pump ---
    public int TankIdleHeatGain { get; init; }
    public int TankMoveHeatGain { get; init; }
    public float TankDockHeatPumpRate { get; init; }
    public int TankDigRadius { get; init; }
    public float TankHeatBaseCoolBonus { get; init; }
    public int BaseHomeCooldownHeat { get; init; }
    public int BaseForeignCooldownHeat { get; init; }

    public static SimulationSettings FromTweaks() => new()
    {
        AdvanceStep = Tweaks.World.AdvanceStep,
        ThermalActiveTileSize = Tweaks.World.ThermalActiveTileSize,
        ThermalParallelTileThreshold = Tweaks.World.ThermalParallelTileThreshold,
        ThermalMaxWorkers = Tweaks.World.ThermalMaxWorkers,
        ThermalSettledGrowStep = Tweaks.World.ThermalSettledGrowStep,
        ThermalSettledFadeStep = Tweaks.World.ThermalSettledFadeStep,
        ThermalWakeTemperature = Tweaks.World.ThermalWakeTemperature,
        ThermalFlowCutoff = Tweaks.World.ThermalFlowCutoff,
        ThermalSaturationCoverageThreshold = Tweaks.World.ThermalSaturationCoverageThreshold,
        ThermalSaturationFramesBeforeThrottle = Tweaks.World.ThermalSaturationFramesBeforeThrottle,
        ThermalSaturationStepIntervalFrames = Tweaks.World.ThermalSaturationStepIntervalFrames,
        ThermalDt = Tweaks.World.ThermalDt,
        ThermalAmbientTemperature = Tweaks.World.ThermalAmbientTemperature,
        ThermalMaterials = Tweaks.World.ThermalMaterials,
        ThermalAirCellCoupling = Tweaks.World.ThermalAirCellCoupling,
        ThermalAirNeighborCoupling = Tweaks.World.ThermalAirNeighborCoupling,
        TankHeatDigPerPixel = Tweaks.Tank.HeatDigPerPixel,
        TankHeatShootPerShot = Tweaks.Tank.HeatShootPerShot,
        TankHeatPerOrePerTick = Tweaks.Tank.HeatPerOrePerSecond / Tweaks.Perf.TargetFps,
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

        // Tank thermals
        TankIdleHeatGain = Tweaks.Tank.IdleHeatGain,
        TankMoveHeatGain = Tweaks.Tank.MoveHeatGain,
        TankDockHeatPumpRate = Tweaks.Tank.DockHeatPumpRate,
        TankDigRadius = Tweaks.Tank.DigRadius,
        TankHeatBaseCoolBonus = Tweaks.Tank.HeatBaseCoolBonus,
        BaseHomeCooldownHeat = Tweaks.Base.HomeCooldownHeat,
        BaseForeignCooldownHeat = Tweaks.Base.ForeignCooldownHeat,
    };
}
