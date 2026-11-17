namespace Tunnerer.Core.Sensing;

using Tunnerer.Core.Config;

/// <summary>
/// How far a radar reaches: every range in <see cref="Tweaks.Radar"/>, and the heat budget of its rays, scaled for one
/// tank (its base's radar upgrades, DESIGN.md §4.8). The rules that use them live in <see cref="Radar"/> alone.
/// </summary>
public readonly record struct RadarReach(
    float Range,
    float SolidRangeMin,
    float GoldRange,
    float MachineRange,
    float LifeRange,
    float CavityRange,
    float HeatRangeMin,
    float HeatRangeMax,
    float HeatBudgetDegreeCells)
{
    /// <summary>The ranges of <see cref="Tweaks.Radar"/> as they are.</summary>
    public static RadarReach Base { get; } = Scaled(1f, 1f);

    /// <summary>Every range times <paramref name="rangeScale"/>, the heat budget times <paramref name="heatBudgetScale"/>.</summary>
    public static RadarReach Scaled(float rangeScale, float heatBudgetScale) => new(
        Range: Tweaks.Radar.Range * rangeScale,
        SolidRangeMin: Tweaks.Radar.SolidRangeMin * rangeScale,
        GoldRange: Tweaks.Radar.GoldRange * rangeScale,
        MachineRange: Tweaks.Radar.MachineRange * rangeScale,
        LifeRange: Tweaks.Radar.LifeRange * rangeScale,
        CavityRange: Tweaks.Radar.CavityRange * rangeScale,
        HeatRangeMin: Tweaks.Radar.HeatRangeMin * rangeScale,
        HeatRangeMax: Tweaks.Radar.HeatRangeMax * rangeScale,
        HeatBudgetDegreeCells: Tweaks.Radar.HeatBudgetDegreeCells * heatBudgetScale);
}
