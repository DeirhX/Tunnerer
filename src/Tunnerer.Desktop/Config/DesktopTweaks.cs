namespace Tunnerer.Desktop.Config;

using Tunnerer.Core.Config;
using Tunnerer.Core.Types;

/// <summary>Defaults for network matches (docs/multiplayer-design.md).</summary>
public static class NetworkTweaks
{
    /// <summary>The relay <c>--net</c> connects to when <c>--relay</c> names no other.</summary>
    public const string DefaultRelay = "wss://crust.bokovka.cz/play";

    /// <summary>Tanks in a network match unless the host asks for another number; bots take the slots nobody joins.</summary>
    public const int DefaultPlayers = 4;

    /// <summary>
    /// The map of a network match with more than two tanks, unless the host passes --size. Every player's machine
    /// must simulate it in real time, so it is kept modest: about twice the cells of the single-player map.
    /// </summary>
    public static readonly Size LargeMatchMapSize = new(480, 300);

    /// <summary>
    /// Ticks received and not yet simulated beyond which the player is told their machine is not keeping up
    /// (two seconds' worth).
    /// </summary>
    public const int BehindWarningTicks = 48;

    /// <summary>
    /// Ticks received and not yet simulated beyond which the client stops playing at the normal rate and replays as
    /// fast as it can (one second's worth): it has just rejoined, or it stalled.
    /// </summary>
    public const int CatchUpTicks = 24;

    /// <summary>Time one frame may spend replaying ticks while catching up, so the window stays responsive.</summary>
    public const int CatchUpBudgetMs = 25;
}

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

/// <summary>Window, render-surface and GPU post-processing / lighting tuning for the desktop client.</summary>
public static class DesktopScreenTweaks
{
    public static readonly Size WindowSize = new(1920, 1080);
    public static readonly Size RenderSurfaceSize = new(320, 200);
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

    // GPU post-processing / lighting tuning
    public const float PostVignetteStrength = 0.18f;
    public const float PostVignetteInnerRadius = 0.35f;
    public const float PostVignetteOuterRadius = 0.95f;
    public const float PostTerrainHeatThreshold = 0.10f;
    public const float PostTerrainMaskCaveDarken = 0.12f;
    public const float PostTerrainMaskSolidLift = 0.04f;
    public const float PostTerrainMaskRimLift = 0.07f;
    public const float PostMaterialEmissiveGoldR = 0.95f;
    public const float PostMaterialEmissiveGoldG = 0.75f;
    public const float PostMaterialEmissiveGoldB = 0.20f;
    public const float PostMaterialEmissiveScorchedR = 0.7059f;
    public const float PostMaterialEmissiveScorchedG = 0.3137f;
    public const float PostMaterialEmissiveScorchedB = 0.1176f;
    public const float PostMaterialEmissiveGoldStrength = 0.95f;
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
