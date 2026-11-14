namespace Tunnerer.Desktop.Config;

using Tunnerer.Core.Config;
using Tunnerer.Core.Types;

/// <summary>Launch options for a match that come from the command line.</summary>
public static class GameLaunchOptions
{
    /// <summary>Map and simulation seed requested with --seed=N; null lets the game choose.</summary>
    public static int? Seed { get; set; }
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

/// <summary>
/// Sound settings from the command line (<c>--volume=N</c>, <c>--mute</c>) and the environment
/// (<c>TUNNERER_VOLUME</c>, <c>TUNNERER_MUTE</c>); M and - / = change them while playing.
/// </summary>
public static class AudioOptions
{
    public const float DefaultVolume = 0.7f;

    /// <summary>Master volume, 0 (silent) to 1.</summary>
    public static float Volume { get; set; } = DefaultVolume;

    public static bool Muted { get; set; }

    /// <summary>
    /// <c>TUNNERER_AUDIO_CAPTURE</c>: write what would have been played to this WAV file instead of opening a sound
    /// device, exactly one simulation step of sound per step. This is how sound is checked in automated runs.
    /// </summary>
    public static string? CapturePath { get; set; }

    /// <summary>Parses a volume in percent (0-100); null if the text is not a number in that range.</summary>
    public static float? ParseVolume(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return float.TryParse(text.Trim().TrimEnd('%'), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out float percent) &&
               percent >= 0f && percent <= 100f
            ? percent / 100f
            : null;
    }
}

/// <summary>Mix levels and limits for sound effects. Gains are relative to the master volume.</summary>
public static class AudioTweaks
{
    public const int SampleRate = 48000;

    /// <summary>Samples per device buffer; about 11 ms at 48 kHz.</summary>
    public const int DeviceBufferSamples = 512;

    /// <summary>
    /// Distance (cells) from the player's tank at which an effect is at about half loudness. The default map is
    /// 320 cells wide, so the far side of it is faint but not silent.
    /// </summary>
    public const float HalfLoudnessDistance = 80f;

    /// <summary>
    /// Inverse-square loudness below which an effect is silent; loudness runs smoothly down to zero there
    /// (about 320 cells away), so nothing cuts in or out at the edge of hearing.
    /// </summary>
    public const float InaudibleGain = 0.06f;

    /// <summary>Horizontal distance (cells) at which an effect is panned fully to one side.</summary>
    public const float FullPanDistance = 130f;

    /// <summary>How far a fully panned effect leans (1 would silence the other speaker).</summary>
    public const float MaxPan = 0.75f;

    public const int MaxVoices = 32;

    /// <summary>At most this many copies of one effect sound at once; a further one replaces the oldest.</summary>
    public const int MaxVoicesPerEffect = 4;

    public const float FireGain = 0.60f;
    public const float ImpactGain = 0.60f;
    public const float DebrisGain = 0.30f;
    public const float TankHitGain = 0.70f;
    public const float TankDeathGain = 1.00f;
    public const float RespawnGain = 0.55f;
    public const float DockGain = 0.45f;
    public const float BuildGain = 0.50f;
    public const float PlantGain = 0.60f;
    public const float RefusedGain = 0.45f;
    public const float PickupGain = 0.40f;
    public const float WarningGain = 0.38f;
    public const float EngineIdleGain = 0.10f;
    public const float EngineMovingGain = 0.22f;
    public const float DigGain = 0.34f;
    public const float TorchGain = 0.30f;
    public const float RechargeGain = 0.14f;

    // Shortest gap between two plays of a repeating effect, in simulation steps (24 per second).
    public const int ImpactsPerStep = 2;
    public const int DebrisIntervalSteps = 6;
    public const int DebrisMinShrapnel = 3;
    public const int PickupIntervalSteps = 3;
    public const int OverheatWarningIntervalSteps = 16;
    public const int ShieldWarningIntervalSteps = 40;
    public const int EnergyWarningIntervalSteps = 56;

    /// <summary>Steps a digging, torching or recharging loop keeps sounding after the last step that fed it.</summary>
    public const int LoopHoldSteps = 4;
}

/// <summary>
/// When the running-hot and low-shield warnings are on. They latch: on past one threshold, off again only past a
/// gentler one, so they do not flicker around it. The HUD banners and the warning sounds both ask here.
/// </summary>
public static class PlayerWarnings
{
    /// <summary>On above 80% of the safe maximum temperature, off again below 70%.</summary>
    public static bool RunningHot(bool latched, Tunnerer.Core.Entities.Tank player)
    {
        float heat = player.Heat / Tweaks.Tank.HeatSafeMax;
        return latched ? heat > 0.70f : heat > 0.80f;
    }

    public static bool LowShield(bool latched, Tunnerer.Core.Entities.Tank player)
    {
        float shield = player.Reactor.Health / (float)Math.Max(1, player.Reactor.HealthCapacity);
        return latched ? shield < 0.30f : shield < 0.25f;
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

    /// <summary>
    /// Upper bound on displayed frames per second. The display's refresh rate normally paces rendering; this only
    /// matters where presenting does not wait for it.
    /// </summary>
    public const int MaxRenderFps = 240;
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
