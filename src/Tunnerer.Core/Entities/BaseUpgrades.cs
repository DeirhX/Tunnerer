namespace Tunnerer.Core.Entities;

using Tunnerer.Core.Config;
using Tunnerer.Core.Types;

/// <summary>What a base can spend its dirt store on (DESIGN.md §4.8).</summary>
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
}

/// <summary>Why an upgrade key press bought nothing.</summary>
public enum UpgradeRefusal { None, NotDocked, Maxed, CannotPay }

/// <summary>A base's upgrade levels (0 to <see cref="MaxLevel"/> each) and what each level is worth.</summary>
public sealed class BaseUpgrades
{
    public const int Count = 4;
    public static int MaxLevel => Tweaks.Upgrades.MaxLevel;

    private readonly int[] _levels = new int[Count];

    public int Level(BaseUpgrade upgrade) => _levels[(int)upgrade];

    public bool IsMaxed(BaseUpgrade upgrade) => Level(upgrade) >= MaxLevel;

    /// <summary>Dirt the next level of <paramref name="upgrade"/> costs (levels get dearer).</summary>
    public int NextCost(BaseUpgrade upgrade) => Tweaks.Upgrades.CostPerLevel * (Level(upgrade) + 1);

    internal void Raise(BaseUpgrade upgrade) => _levels[(int)upgrade]++;

    public int RadiatorUnits => Tweaks.Base.HeatCooldown + Level(BaseUpgrade.Radiator) * Tweaks.Upgrades.RadiatorUnitsPerLevel;

    public int CoverageBonusCells => Level(BaseUpgrade.Reach) * Tweaks.Upgrades.ReachCellsPerLevel;

    public double EngineMaxEfficiency =>
        Tweaks.Base.EngineMaxEfficiency + Level(BaseUpgrade.Engine) * Tweaks.Upgrades.EngineEfficiencyPerLevel;

    public double EngineTemperatureScale =>
        Tweaks.Base.EngineTemperatureScale * DeterministicMath.Pow(Tweaks.Upgrades.EngineScalePerLevel, (double)Level(BaseUpgrade.Engine));

    public float OffNetworkHeatFactor =>
        Math.Max(0.1f, 1f - Level(BaseUpgrade.Insulation) * Tweaks.Upgrades.InsulationPerLevel);

    /// <summary>Short name and what the next level does, for the HUD.</summary>
    public static (string Name, string Effect) Describe(BaseUpgrade upgrade) => upgrade switch
    {
        BaseUpgrade.Radiator => ("Radiator", $"+{Tweaks.Upgrades.RadiatorUnitsPerLevel * Tweaks.Base.ReactorEnergyPerHeatUnit:0} heat vented per tick"),
        BaseUpgrade.Reach => ("Network reach", $"+{Tweaks.Upgrades.ReachCellsPerLevel} cells on every coverage circle"),
        BaseUpgrade.Engine => ("Engine", $"+{Tweaks.Upgrades.EngineEfficiencyPerLevel * 100:0}% max efficiency, better on warm ground"),
        BaseUpgrade.Insulation => ("Insulation", $"off-network clock {Tweaks.Upgrades.InsulationPerLevel * 100:0}% slower"),
        _ => (upgrade.ToString(), ""),
    };
}
