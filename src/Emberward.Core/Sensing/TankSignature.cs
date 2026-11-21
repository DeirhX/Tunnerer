namespace Emberward.Core.Sensing;

using Emberward.Core.Config;

/// <summary>How well a radar sees a tank: its echo follows its heat (a cool tank gives none).</summary>
public static class TankSignature
{
    /// <summary>
    /// 0 below <see cref="Tweaks.Radar.SignatureFloorHeat"/> (no echo at all), rising linearly to 1 at
    /// <see cref="Tweaks.Radar.SignatureFullHeat"/>. The echo reaches <see cref="RadarReach.LifeRange"/> times this.
    /// </summary>
    public static float Of(float heat)
    {
        if (heat < Tweaks.Radar.SignatureFloorHeat)
            return 0f;
        return Math.Clamp((heat - Tweaks.Radar.SignatureFloorHeat) / (Tweaks.Radar.SignatureFullHeat - Tweaks.Radar.SignatureFloorHeat), 0f, 1f);
    }
}
