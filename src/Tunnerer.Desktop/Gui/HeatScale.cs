namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Tanks;

/// <summary>
/// Temperature scale of the heat orb and bar, in tank heat units (0 = ambient): from past the point where frostbite
/// starts at the cold end, through the safe range, to a margin past the point where overheating burns the shield. The range is
/// Core's (<see cref="Tank.HeatGaugeMin"/>, <see cref="Tank.HeatGaugeMax"/>); the colours are the HUD's. Shared with the
/// browser, which shows the same colours.
/// </summary>
public static class HeatScale
{
    public static float Min => Tank.HeatGaugeMin;
    public static float Max => Tank.HeatGaugeMax;

    /// <summary>Where a temperature lies on the heat scale, 0 (deep frost) to 1 (well past overheating).</summary>
    public static float Fraction(float heat) =>
        Math.Clamp((heat - Min) / (Max - Min), 0f, 1f);

    /// <summary>
    /// Colour for a temperature (RGB, 0 to 1): icy blue below freezing, green around ambient, then amber, orange and
    /// red as the tank approaches and passes the safe maximum.
    /// </summary>
    public static Vector3 ColorRgb(float heat)
    {
        float safe = Tweaks.Tank.HeatSafeMax;
        ReadOnlySpan<float> at = [Min, 0f, 0.55f * safe, 0.85f * safe, safe];
        ReadOnlySpan<Vector3> rgb =
        [
            new(0.35f, 0.75f, 1.00f), new(0.40f, 0.90f, 0.40f), new(1.00f, 0.80f, 0.20f),
            new(1.00f, 0.45f, 0.10f), new(1.00f, 0.15f, 0.10f),
        ];
        Vector3 c = rgb[^1];
        if (heat <= at[0])
            c = rgb[0];
        else
        {
            for (int i = 1; i < at.Length; i++)
            {
                if (heat <= at[i])
                {
                    c = Vector3.Lerp(rgb[i - 1], rgb[i], (heat - at[i - 1]) / (at[i] - at[i - 1]));
                    break;
                }
            }
        }
        return c;
    }
}
