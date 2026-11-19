namespace Emberward.Core.Entities.Bases;

using Emberward.Core.Config;
using Emberward.Core.Types;

/// <summary>
/// What a base can spend its ore store on (DESIGN.md §4.8), bought while its tank is docked. The first four are the
/// base track; <see cref="CargoHold"/> and <see cref="Radar"/> are the tank track (<see cref="BaseUpgrades.TrackOf"/>).
/// </summary>
public enum BaseUpgrade
{
    /// <summary>Vents more heat per tick: more relays and more tank cooling before the base fills up.</summary>
    Radiator,
    /// <summary>Widens every coverage circle of the network: the tank can roam further from its nodes.</summary>
    Reach,
    /// <summary>Turns more of the relay heat into charge.</summary>
    Engine,
    /// <summary>Slows the off-network heat floor: longer dives beyond the network.</summary>
    Insulation,
    /// <summary>Relays, pylons and the base see further than they cool. Presentation only: it changes no simulation result.</summary>
    Sensors,
    /// <summary>A bigger ore hold on the tank.</summary>
    CargoHold,
    /// <summary>Longer radar ranges and more heat budget per ray. Presentation only: it changes no simulation result.</summary>
    Radar,
}

/// <summary>The two upgrade tracks: what the base does, and what its tank carries.</summary>
public enum UpgradeTrack { Base, Tank }

/// <summary>Why an upgrade key press bought nothing.</summary>
public enum UpgradeRefusal { None, NotDocked, Maxed, NotEnoughOre }

/// <summary>A base's upgrade levels (0 to <see cref="MaxLevel"/> each) and what each level is worth.</summary>
public sealed class BaseUpgrades
{
    public const int Count = 7;
    public static int MaxLevel => Tweaks.Upgrades.MaxLevel;

    private readonly int[] _levels = new int[Count];

    public static UpgradeTrack TrackOf(BaseUpgrade upgrade) =>
        upgrade is BaseUpgrade.CargoHold or BaseUpgrade.Radar ? UpgradeTrack.Tank : UpgradeTrack.Base;

    public int Level(BaseUpgrade upgrade) => _levels[(int)upgrade];

    public bool IsMaxed(BaseUpgrade upgrade) => Level(upgrade) >= MaxLevel;

    /// <summary>Ore the next level of <paramref name="upgrade"/> costs (<see cref="Tweaks.Upgrades.LevelCosts"/>); 0 once maxed.</summary>
    public int NextCost(BaseUpgrade upgrade) => IsMaxed(upgrade) ? 0 : Tweaks.Upgrades.LevelCosts[Level(upgrade)];

    internal void Raise(BaseUpgrade upgrade) => _levels[(int)upgrade]++;

    public int RadiatorUnits => Tweaks.Base.HeatCooldown + Level(BaseUpgrade.Radiator) * Tweaks.Upgrades.RadiatorUnitsPerLevel;

    public int CoverageBonusCells => Level(BaseUpgrade.Reach) * Tweaks.Upgrades.ReachCellsPerLevel;

    /// <summary>How far the base itself covers (and sees), with the reach upgrade.</summary>
    public int BaseCoverageRadius => Tweaks.Coverage.BaseRadius + CoverageBonusCells;

    /// <summary>How far each of the base's link nodes (relays, pylons) covers (and sees), with the reach upgrade.</summary>
    public int NodeCoverageRadius => Tweaks.Coverage.NodeRadius + CoverageBonusCells;

    private int SensorBonusCells => Level(BaseUpgrade.Sensors) * Tweaks.Upgrades.SensorCellsPerLevel;

    /// <summary>How far the base sees along open ground: its coverage circle plus the sensors.</summary>
    public int BaseSightRadius => BaseCoverageRadius + SensorBonusCells;

    /// <summary>
    /// How far each relay or pylon sees, through the ground too: its coverage circle, <see cref="Tweaks.Vision.NodeSightBonus"/>
    /// more, and the sensors. Presentation only.
    /// </summary>
    public int NodeSightRadius => NodeCoverageRadius + Tweaks.Vision.NodeSightBonus + SensorBonusCells;

    public double EngineMaxEfficiency =>
        Tweaks.Base.EngineMaxEfficiency + Level(BaseUpgrade.Engine) * Tweaks.Upgrades.EngineEfficiencyPerLevel;

    public double EngineTemperatureScale =>
        Tweaks.Base.EngineTemperatureScale * DeterministicMath.Pow(Tweaks.Upgrades.EngineScalePerLevel, (double)Level(BaseUpgrade.Engine));

    public float OffNetworkHeatFactor =>
        Math.Max(0.1f, 1f - Level(BaseUpgrade.Insulation) * Tweaks.Upgrades.InsulationPerLevel);

    /// <summary>The ore hold of this base's tank: <see cref="Tweaks.Ore.TankHold"/> plus the cargo hold levels.</summary>
    public int TankHoldCapacity => Tweaks.Ore.TankHold + Level(BaseUpgrade.CargoHold) * Tweaks.Upgrades.CargoHoldPerLevel;

    /// <summary>What every radar range of this base's tank is multiplied by.</summary>
    public float RadarRangeScale => 1f + Level(BaseUpgrade.Radar) * Tweaks.Upgrades.RadarRangePerLevel;

    /// <summary>What the heat budget of this base's tank's radar rays is multiplied by.</summary>
    public float RadarHeatBudgetScale => 1f + Level(BaseUpgrade.Radar) * Tweaks.Upgrades.RadarHeatBudgetPerLevel;

    /// <summary>Short name and what the next level does, for the HUD.</summary>
    public static (string Name, string Effect) Describe(BaseUpgrade upgrade) => upgrade switch
    {
        BaseUpgrade.Radiator => ("Radiator", $"+{Tweaks.Upgrades.RadiatorUnitsPerLevel * Tweaks.Base.ReactorEnergyPerHeatUnit:0} heat vented per tick"),
        BaseUpgrade.Reach => ("Network reach", $"+{Tweaks.Upgrades.ReachCellsPerLevel} cells on every coverage circle"),
        BaseUpgrade.Engine => ("Engine", $"+{Tweaks.Upgrades.EngineEfficiencyPerLevel * 100:0}% max efficiency, better on warm ground"),
        BaseUpgrade.Insulation => ("Insulation", $"off-network clock {Tweaks.Upgrades.InsulationPerLevel * 100:0}% slower"),
        BaseUpgrade.Sensors => ("Sensors", $"+{Tweaks.Upgrades.SensorCellsPerLevel} cells of sight around relays and base"),
        BaseUpgrade.CargoHold => ("Cargo hold", $"+{Tweaks.Upgrades.CargoHoldPerLevel} ore in the tank's hold"),
        BaseUpgrade.Radar => ("Radar", $"+{Tweaks.Upgrades.RadarRangePerLevel * 100:0}% range, +{Tweaks.Upgrades.RadarHeatBudgetPerLevel * 100:0}% heat budget"),
        _ => (upgrade.ToString(), ""),
    };
}
