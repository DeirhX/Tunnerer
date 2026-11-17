namespace Tunnerer.Desktop.Net;

using Tunnerer.Core.Types;
using Tunnerer.Desktop.Config;

/// <summary>The map sizes the network menu offers when a room is created (docs/multiplayer-lobby-ux.md section 3.3).</summary>
public enum MapPreset
{
    Small,
    Medium,
    Large,
}

public static class MapPresets
{
    /// <summary>The size of <c>--large</c> and of the Large preset.</summary>
    public static readonly Size LargeSize = new(1500, 750);

    public static Size SizeOf(MapPreset preset) => preset switch
    {
        MapPreset.Small => DesktopScreenTweaks.RenderSurfaceSize,
        MapPreset.Medium => NetworkTweaks.LargeMatchMapSize,
        _ => LargeSize,
    };

    /// <summary>The preset with exactly this size, or null.</summary>
    public static MapPreset? PresetOf(Size size) =>
        Enum.GetValues<MapPreset>().Cast<MapPreset?>().FirstOrDefault(preset => SizeOf(preset!.Value) == size);

    /// <summary>The preset a match of <paramref name="players"/> tanks gets when nobody chose one: the rule the command line uses.</summary>
    public static MapPreset DefaultFor(int players) => players > 2 ? MapPreset.Medium : MapPreset.Small;

    public static string Label(MapPreset preset)
    {
        var size = SizeOf(preset);
        return $"{preset} {size.X}x{size.Y}";
    }

    /// <summary>Every machine simulates the whole map in real time; the biggest one is more than a slow machine can do.</summary>
    public static string? Warning(MapPreset preset) => preset == MapPreset.Large
        ? "Large maps need a fast computer: a slow one falls behind the match."
        : null;
}
