namespace Tunnerer.Desktop.Config;

using Tunnerer.Core.Config;
using Tunnerer.Core.Types;

public enum RenderBackendKind
{
    Dx11 = 0,
}

public static class DesktopTweaks
{
    public const RenderBackendKind DefaultRenderBackend = RenderBackendKind.Dx11;
}

/// <summary>
/// Which monitor the game window opens on. The window is borderless fullscreen-desktop, so it fills that display.
/// Unset (null) keeps the default: centered on the primary display.
/// </summary>
public static class DesktopDisplayOptions
{
    /// <summary>SDL display index (0 is the primary display).</summary>
    public static int? DisplayIndex { get; set; }

    /// <summary>Size of a normal bordered, resizable window to use instead of borderless fullscreen; null means fullscreen.</summary>
    public static Size? WindowedSize { get; set; }

    public static readonly Size DefaultWindowedSize = new(1280, 720);

    /// <summary>
    /// Create the window hidden (never shown). Rendering still happens at the window's size, so this is for automated
    /// runs and performance measurement: unless a size is given with <see cref="WindowedSize"/>, the window gets the
    /// resolution of the largest display.
    /// </summary>
    public static bool Hidden { get; set; }

    public static bool ParseFlag(string? text)
    {
        text = text?.Trim();
        return text != null &&
               (text.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses <c>--windowed</c> / <c>TUNNERER_WINDOWED</c> values: empty or a truthy word (<c>1</c>, <c>true</c>,
    /// <c>on</c>) for <see cref="DefaultWindowedSize"/>, or <c>WxH</c> for a custom size. Returns null when not requested.
    /// </summary>
    public static Size? ParseWindowed(string? text, bool flagPresent = false)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return flagPresent ? DefaultWindowedSize : null;

        var parts = text.Split('x', 'X');
        if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && w >= 320 && h >= 200)
            return new Size(w, h);

        return text.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("on", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("yes", StringComparison.OrdinalIgnoreCase)
            ? DefaultWindowedSize
            : null;
    }

    /// <summary>
    /// Parses <c>--display=N</c> / <c>TUNNERER_DISPLAY</c> values: a non-negative number, or <c>secondary</c> for the
    /// first display that is not the primary one. Returns null if the text is not recognised.
    /// </summary>
    public static int? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        if (text.Equals("secondary", StringComparison.OrdinalIgnoreCase) || text.Equals("other", StringComparison.OrdinalIgnoreCase))
            return 1;
        return int.TryParse(text, out int index) && index >= 0 ? index : null;
    }
}

public static class DesktopScreenTweaks
{
    public static Size WindowSize => Tweaks.Screen.WindowSize;
    public static Size RenderSurfaceSize => Tweaks.Screen.RenderSurfaceSize;
    public static int PixelScale => Tweaks.Screen.PixelScale;
    public static int NativeContinuousSampleLow => Tweaks.Screen.NativeContinuousSampleLow;
    public static int NativeContinuousSampleMedium => Tweaks.Screen.NativeContinuousSampleMedium;
    public static int NativeContinuousSampleHigh => Tweaks.Screen.NativeContinuousSampleHigh;
    public static float NativeContinuousEdgeSoftness => Tweaks.Screen.NativeContinuousEdgeSoftness;
    public static float NativeContinuousBoundaryBlend => Tweaks.Screen.NativeContinuousBoundaryBlend;
    public static float NativeContinuousRenderBudgetMs => Tweaks.Screen.NativeContinuousRenderBudgetMs;
    public static int NativeContinuousBudgetHysteresisFrames => Tweaks.Screen.NativeContinuousBudgetHysteresisFrames;
    public static float NativeContinuousBudgetUnderThreshold => Tweaks.Screen.NativeContinuousBudgetUnderThreshold;
    public static int NativeContinuousRecoveryFramesMultiplier => Tweaks.Screen.NativeContinuousRecoveryFramesMultiplier;
    public const int HeatAuxUpdateIntervalFrames = 2;
    public static float SolidAlphaFloor => Tweaks.Screen.SolidAlphaFloor;
    public static float ScorchTintDarken => Tweaks.Screen.ScorchTintDarken;
    public static float ScorchTintStrength => Tweaks.Screen.ScorchTintStrength;

    public static float PostVignetteStrength => Tweaks.Screen.PostVignetteStrength;
    public static float PostVignetteInnerRadius => Tweaks.Screen.PostVignetteInnerRadius;
    public static float PostVignetteOuterRadius => Tweaks.Screen.PostVignetteOuterRadius;
    public static float PostTerrainHeatThreshold => Tweaks.Screen.PostTerrainHeatThreshold;
    public static float PostTerrainMaskEdgeStrength => Tweaks.Screen.PostTerrainMaskEdgeStrength;
    public static float PostTerrainMaskCaveDarken => Tweaks.Screen.PostTerrainMaskCaveDarken;
    public static float PostTerrainMaskSolidLift => Tweaks.Screen.PostTerrainMaskSolidLift;
    public static float PostTerrainMaskOutlineDarken => Tweaks.Screen.PostTerrainMaskOutlineDarken;
    public static float PostTerrainMaskRimLift => Tweaks.Screen.PostTerrainMaskRimLift;
    public static float PostTerrainMaskBoundaryScale => Tweaks.Screen.PostTerrainMaskBoundaryScale;
    public static float PostMaterialEmissiveGoldR => Tweaks.Screen.PostMaterialEmissiveGoldR;
    public static float PostMaterialEmissiveGoldG => Tweaks.Screen.PostMaterialEmissiveGoldG;
    public static float PostMaterialEmissiveGoldB => Tweaks.Screen.PostMaterialEmissiveGoldB;
    public static float PostMaterialEmissiveScorchedR => Tweaks.Screen.PostMaterialEmissiveScorchedR;
    public static float PostMaterialEmissiveScorchedG => Tweaks.Screen.PostMaterialEmissiveScorchedG;
    public static float PostMaterialEmissiveScorchedB => Tweaks.Screen.PostMaterialEmissiveScorchedB;
    public static float PostMaterialEmissiveGoldStrength => Tweaks.Screen.PostMaterialEmissiveGoldStrength;
    public static float PostMaterialEmissiveScorchedStrength => Tweaks.Screen.PostMaterialEmissiveScorchedStrength;
    public static float PostMaterialEmissivePulseFreq => Tweaks.Screen.PostMaterialEmissivePulseFreq;
    public static float PostMaterialEmissivePulseMin => Tweaks.Screen.PostMaterialEmissivePulseMin;
    public static float PostMaterialEmissivePulseRange => Tweaks.Screen.PostMaterialEmissivePulseRange;
    public static float PostTankHeatGlowR => Tweaks.Screen.PostTankHeatGlowR;
    public static float PostTankHeatGlowG => Tweaks.Screen.PostTankHeatGlowG;
    public static float PostTankHeatGlowB => Tweaks.Screen.PostTankHeatGlowB;
    public static bool PostTankHeatDistortionEnabled => Tweaks.Screen.PostTankHeatDistortionEnabled;
    public static float PostTankHeatGlowMinHeat => Tweaks.Screen.PostTankHeatGlowMinHeat;
    public static float PostTankHeatGlowBaseRadius => Tweaks.Screen.PostTankHeatGlowBaseRadius;
    public static float PostTankHeatGlowScaleRadius => Tweaks.Screen.PostTankHeatGlowScaleRadius;
    public static float PostTerrainHeatGlowR => Tweaks.Screen.PostTerrainHeatGlowR;
    public static float PostTerrainHeatGlowG => Tweaks.Screen.PostTerrainHeatGlowG;
    public static float PostTerrainHeatGlowB => Tweaks.Screen.PostTerrainHeatGlowB;
    public static byte PostEmissiveGold => Tweaks.Screen.PostEmissiveGold;
    public static byte PostEmissiveScorchedHigh => Tweaks.Screen.PostEmissiveScorchedHigh;
    public static byte PostEmissiveScorchedLow => Tweaks.Screen.PostEmissiveScorchedLow;
    public static float LightDirX => Tweaks.Screen.LightDirX;
    public static float LightDirY => Tweaks.Screen.LightDirY;
    public static float LightDirZ => Tweaks.Screen.LightDirZ;
    public static float LightAmbient => Tweaks.Screen.LightAmbient;
    public static float LightDiffuseWeight => Tweaks.Screen.LightDiffuseWeight;
    public static float LightNormalStrength => Tweaks.Screen.LightNormalStrength;
    public static float LightShininess => Tweaks.Screen.LightShininess;
    public static float LightSpecularIntensity => Tweaks.Screen.LightSpecularIntensity;
    public static float LightMicroNormalStrength => Tweaks.Screen.LightMicroNormalStrength;
}
