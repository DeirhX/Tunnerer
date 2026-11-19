namespace Emberward.Desktop.Config;

using Emberward.Core.Types;

/// <summary>Window, render-surface and GPU post-processing / lighting tuning for the desktop client.</summary>
public static class DesktopScreenTweaks
{
    public static readonly Size WindowSize = new(1920, 1080);
    public static readonly Size RenderSurfaceSize = new(320, 200);
    /// <summary>Degrees from which the thermal debug overlay tints a tile as holding warm ground.</summary>
    public const float ThermalOverlayWarmTemperature = 2.5f;
    /// <summary>Terrain cell size in points, at a standard (1x) display. <c>RenderViewState.CellPixels</c> scales it to device pixels.</summary>
    public const int PixelScale = 6;
    public const int NativeContinuousSampleLow = 1;
    public const int NativeContinuousSampleMedium = 2;
    public const int NativeContinuousSampleHigh = 4;
    public const float NativeContinuousRenderBudgetMs = 5.0f;
    public const int NativeContinuousBudgetHysteresisFrames = 10;
    public const float NativeContinuousBudgetUnderThreshold = 0.65f;
    public const int NativeContinuousRecoveryFramesMultiplier = 20;
    public const int HeatAuxUpdateIntervalFrames = 2;

    /// <summary>
    /// Upper bound on displayed frames per second. The display's refresh rate normally paces rendering; this only
    /// matters where presenting does not wait for it.
    /// </summary>
    public const int MaxRenderFps = 240;

    public const float ScorchTintDarken = 0.35f;
    public const float ScorchTintStrength = 0.50f;

    // Whole-screen effects (Rendering/ScreenEffects.cs, Shaders/ScreenFxPs.hlsl)
    /// <summary>Seconds a hit's shake, colour split and flash take to fade out.</summary>
    public const float ScreenHitSeconds = 0.5f;
    /// <summary>How far a hit throws the view at first, in pixels of a 1080-pixel-high view (scaled to the view).</summary>
    public const float ScreenShakePixels = 14f;
    /// <summary>
    /// How far a hit zooms in at first, as a share of the view; it hides the clamped edge the shake would show, so it
    /// must leave more than the largest throw (<see cref="ScreenShakePixels"/> and its sideways part, about 15 pixels)
    /// at each side of a 1080-pixel-high view. Also in ScreenFxPs.hlsl and webgl.js (PostParamsLayoutGuardTests).
    /// </summary>
    public const float ScreenHitZoom = 0.03f;
    /// <summary>How fast the view rings back after a hit (about 7 swings a second).</summary>
    public const float ScreenShakeRadiansPerSecond = 44f;
    /// <summary>Seconds the overheat haze takes to come in fully.</summary>
    public const float ScreenOverheatRiseSeconds = 0.4f;
    /// <summary>Seconds the overheat haze takes to clear once the tank is back under the safe maximum, or dead.</summary>
    public const float ScreenOverheatFallSeconds = 0.9f;
    /// <summary>Seconds the frost takes to creep in fully.</summary>
    public const float ScreenFreezeRiseSeconds = 0.8f;
    /// <summary>Seconds the frost takes to thaw once the tank is warmer than frostbite, or dead.</summary>
    public const float ScreenFreezeFallSeconds = 1.6f;

    // Charge and flow labels over machines (Gui/PowerLabels.cs), while the energy network runs
    /// <summary>Cells from the player's tank within which a machine's charge label shows fully.</summary>
    public const float PowerLabelFullCells = 48f;
    /// <summary>Cells from the player's tank at which a machine's charge label has faded out; none show beyond.</summary>
    public const float PowerLabelGoneCells = 112f;
    /// <summary>UI points (CSS pixels in the browser) a cell at which, zoomed out, the charge labels have faded out.</summary>
    public const float PowerLabelZoomGone = 2.5f;
    /// <summary>UI points a cell from which the charge labels show fully.</summary>
    public const float PowerLabelZoomFull = 4f;

    // GPU post-processing / lighting tuning
    public const float PostVignetteStrength = 0.18f;
    public const float PostVignetteInnerRadius = 0.35f;
    public const float PostVignetteOuterRadius = 0.95f;
    public const float PostTerrainHeatThreshold = 0.10f;
    public const float PostTerrainMaskCaveDarken = 0.12f;
    public const float PostTerrainMaskSolidLift = 0.04f;
    public const float PostTerrainMaskRimLift = 0.07f;
    public const float PostMaterialEmissiveScorchedR = 0.7059f;
    public const float PostMaterialEmissiveScorchedG = 0.3137f;
    public const float PostMaterialEmissiveScorchedB = 0.1176f;
    public const float PostMaterialEmissiveScorchedStrength = 0.32f;
    public const float PostMaterialEmissivePulseFreq = 3.0f;
    public const float PostMaterialEmissivePulseMin = 0.8f;
    public const float PostMaterialEmissivePulseRange = 0.2f;
    public const float PostTankHeatGlowR = 0.78f;
    public const float PostTankHeatGlowG = 0.24f;
    public const float PostTankHeatGlowB = 0.04f;
    public const bool PostTankHeatDistortionEnabled = false;
    public const float PostTankHeatGlowMinHeat = 1.5f;
    public const float PostTankHeatGlowBaseRadius = 2.5f;
    public const float PostTankHeatGlowScaleRadius = 2.5f;
    public const float PostTerrainHeatGlowR = 1.0000f;
    public const float PostTerrainHeatGlowG = 0.4500f;
    public const float PostTerrainHeatGlowB = 0.0500f;

    // Directional lighting: top-left, slightly forward.
    public const float LightDirX = -0.35f;
    public const float LightDirY = -0.55f;
    public const float LightDirZ = 0.70f;
    public const float LightAmbient = 0.22f;
    public const float LightDiffuseWeight = 0.68f;
    public const float LightNormalStrength = 2.2f;
    public const float LightShininess = 8f;
    public const float LightSpecularIntensity = 0.10f;
    public const float LightMicroNormalStrength = 0.35f;
}
