namespace Emberward.Desktop.Gui;

using System.Globalization;
using Emberward.Core;

/// <summary>
/// The HUD's colours and colour arithmetic, with no ImGui in it, so that the browser client (which compiles this file
/// through <c>Emberward.Rendering</c>) colours what it shows as the desktop does. Desktop code reads them through
/// <see cref="HudTheme"/>. Colours are ImGui ABGR.
/// </summary>
public static class HudPalette
{
    public static uint Rgba(byte r, byte g, byte b, byte a = 255) => (uint)(a << 24 | b << 16 | g << 8 | r);

    /// <summary><paramref name="color"/> with its alpha scaled by <paramref name="alpha"/> (0 to 1).</summary>
    public static uint WithAlpha(uint color, float alpha) =>
        (color & 0x00FFFFFFu) | (uint)(Math.Clamp(alpha, 0f, 1f) * (color >> 24)) << 24;

    /// <summary><paramref name="color"/> with its alpha replaced by <paramref name="alpha"/> (0 to 255).</summary>
    public static uint WithAlphaByte(uint color, uint alpha) => (color & 0x00FFFFFFu) | alpha << 24;

    /// <summary>The colour a status gauge shows at <paramref name="level"/>, with <paramref name="ok"/> for a fine one.</summary>
    public static uint LevelColor(StatusLevel level, uint ok) => level switch
    {
        StatusLevel.Ok => ok,
        StatusLevel.Warn => Warn,
        _ => Danger,
    };

    /// <summary>An ABGR colour as CSS <c>#rrggbb</c> (its alpha left out).</summary>
    public static string Hex(uint color) =>
        string.Create(CultureInfo.InvariantCulture, $"#{color & 0xFF:x2}{(color >> 8) & 0xFF:x2}{(color >> 16) & 0xFF:x2}");

    public static readonly uint PlateTop = Rgba(30, 32, 36, 238);
    public static readonly uint PlateBottom = Rgba(13, 14, 16, 242);
    public static readonly uint Bronze = Rgba(122, 86, 44);
    public static readonly uint BronzeDark = Rgba(58, 40, 22);
    public static readonly uint Amber = Rgba(255, 178, 64);
    public static readonly uint AmberDim = Rgba(176, 112, 40);
    public static readonly uint Teal = Rgba(96, 214, 255);
    public static readonly uint Text = Rgba(234, 228, 216);
    public static readonly uint TextDim = Rgba(158, 150, 138);
    public static readonly uint Producer = Rgba(194, 156, 255);
    public static readonly uint Good = Rgba(118, 230, 128);
    public static readonly uint Warn = Rgba(255, 198, 64);
    public static readonly uint Danger = Rgba(255, 86, 66);
    public static readonly uint Shadow = Rgba(0, 0, 0, 150);
    public static readonly uint Track = Rgba(8, 8, 10, 230);
}
