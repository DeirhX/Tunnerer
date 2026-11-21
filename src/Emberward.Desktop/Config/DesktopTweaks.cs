namespace Emberward.Desktop.Config;

using Emberward.Core.Config;
using Emberward.Core.Types;

/// <summary>Launch options for a match that come from the command line.</summary>
public static class GameLaunchOptions
{
    /// <summary>Map and simulation seed requested with --seed=N; null lets the game choose.</summary>
    public static int? Seed { get; set; }

    /// <summary>
    /// --energy-audit: add the <c>[ProfileE]</c> line to the simulation profile. It sums the heat of every cell of
    /// the map once per report, so it is off unless asked for.
    /// </summary>
    public static bool EnergyAudit { get; set; }
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

    /// <summary>Whether <paramref name="text"/> is a truthy word: <c>1</c>, <c>true</c>, <c>yes</c> or <c>on</c>, in any case.</summary>
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
    /// Parses <c>--windowed</c> / <c>EMBERWARD_WINDOWED</c> values: empty or a truthy word (<c>1</c>, <c>true</c>,
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

        return ParseFlag(text) ? DefaultWindowedSize : null;
    }

    /// <summary>
    /// Parses <c>--display=N</c> / <c>EMBERWARD_DISPLAY</c> values: a non-negative number, or <c>secondary</c> for the
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
/// (<c>EMBERWARD_VOLUME</c>, <c>EMBERWARD_MUTE</c>); M and - / = change them while playing.
/// </summary>
public static class AudioOptions
{
    public const float DefaultVolume = 0.7f;

    /// <summary>Master volume, 0 (silent) to 1.</summary>
    public static float Volume { get; set; } = DefaultVolume;

    public static bool Muted { get; set; }

    /// <summary>
    /// <c>EMBERWARD_AUDIO_CAPTURE</c>: write what would have been played to this WAV file instead of opening a sound
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
