namespace Tunnerer.Core.Sensing;

using Tunnerer.Core.Config;

/// <summary>
/// How far a radar reaches: every range in <see cref="Tweaks.Radar"/>, and the heat budget of its rays, scaled for one
/// tank (its base's radar upgrades, DESIGN.md §4.8). The rules that use them live in <see cref="Radar"/> alone.
/// The pulse's front runs faster in the same proportion as the ranges grow, so a pulse always takes as long to reach
/// its full range and a longer radar pings as often.
/// </summary>
public readonly record struct RadarReach(
    float Range,
    float SolidRangeMin,
    float MachineRange,
    float LifeRange,
    float CavityRange,
    float HeatRangeMin,
    float HeatRangeMax,
    float HeatBudgetDegreeCells,
    float OreRangeMin,
    float OreRangeMax,
    float FrontCellsPerTick)
{
    /// <summary>The ranges of <see cref="Tweaks.Radar"/> as they are.</summary>
    public static RadarReach Base { get; } = Scaled(1f, 1f);

    /// <summary>
    /// Every range and the front's speed times <paramref name="rangeScale"/>, the heat budget times
    /// <paramref name="heatBudgetScale"/>.
    /// </summary>
    public static RadarReach Scaled(float rangeScale, float heatBudgetScale) => new(
        Range: Tweaks.Radar.Range * rangeScale,
        SolidRangeMin: Tweaks.Radar.SolidRangeMin * rangeScale,
        MachineRange: Tweaks.Radar.MachineRange * rangeScale,
        LifeRange: Tweaks.Radar.LifeRange * rangeScale,
        CavityRange: Tweaks.Radar.CavityRange * rangeScale,
        HeatRangeMin: Tweaks.Radar.HeatRangeMin * rangeScale,
        HeatRangeMax: Tweaks.Radar.HeatRangeMax * rangeScale,
        HeatBudgetDegreeCells: Tweaks.Radar.HeatBudgetDegreeCells * heatBudgetScale,
        OreRangeMin: Tweaks.Radar.OreRangeMin * rangeScale,
        OreRangeMax: Tweaks.Radar.OreRangeMax * rangeScale,
        FrontCellsPerTick: Tweaks.Radar.FrontCellsPerTick * rangeScale);
}
