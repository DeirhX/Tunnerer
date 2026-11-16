namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using Tunnerer.Core.Config;

/// <summary>
/// Temperature scale of the heat orb and bar, in tank heat units (0 = ambient): from a full coolant charge at the cold
/// end, through the safe range, to a margin past the point where overheating starts burning the shield.
/// </summary>
public static class HeatScale
{
    public static float Min => -Tweaks.Tank.CoolantCapacity;
    public static float Max => Tweaks.Tank.HeatSafeMax * 1.25f;

    /// <summary>Where a temperature lies on the heat scale, 0 (full coolant charge) to 1 (well past overheating).</summary>
    public static float Fraction(float heat) =>
        Math.Clamp((heat - Min) / (Max - Min), 0f, 1f);

    /// <summary>
    /// Colour for a temperature: icy blue with coolant, green around ambient, then amber, orange and red as the
    /// tank approaches and passes the safe maximum.
    /// </summary>
    public static uint Color(float heat) => HudDraw.PackColor(ColorRgb(heat), 1f);

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
