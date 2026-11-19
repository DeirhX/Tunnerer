namespace Emberward.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Emberward.Core;

/// <summary>
/// One look for every piece of UI drawn over the game: the tutorial card, toasts, the status and upgrade panels, the
/// pause and controls menus and small labels. It follows the bottom HUD art: dark gunmetal plates with chamfered
/// corners, a bronze frame with an amber inner line, amber corner brackets and rivets, amber titles, and segmented
/// glowing bars. Colours are ImGui ABGR.
/// </summary>
public static class HudTheme
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

    // ---- Palette ---------------------------------------------------------------
    public static readonly uint PlateTop = Rgba(30, 32, 36, 238);
    public static readonly uint PlateBottom = Rgba(13, 14, 16, 242);
    public static readonly uint Bronze = Rgba(122, 86, 44);
    public static readonly uint BronzeDark = Rgba(58, 40, 22);
    public static readonly uint Amber = Rgba(255, 178, 64);
    public static readonly uint AmberDim = Rgba(176, 112, 40);
    public static readonly uint Teal = Rgba(96, 214, 255);
    public static readonly uint Text = Rgba(234, 228, 216);
    public static readonly uint TextDim = Rgba(158, 150, 138);
    public static readonly uint Good = Rgba(118, 230, 128);
    public static readonly uint Warn = Rgba(255, 198, 64);
    public static readonly uint Danger = Rgba(255, 86, 66);
    public static readonly uint Shadow = Rgba(0, 0, 0, 150);
    public static readonly uint Track = Rgba(8, 8, 10, 230);

    private const float Chamfer = 9f;

    private static Vector2[] ChamferedRect(Vector2 min, Vector2 max, float c)
    {
        c = MathF.Min(c, MathF.Min(max.X - min.X, max.Y - min.Y) * 0.5f);
        return
        [
            new(min.X + c, min.Y), new(max.X - c, min.Y), new(max.X, min.Y + c), new(max.X, max.Y - c),
            new(max.X - c, max.Y), new(min.X + c, max.Y), new(min.X, max.Y - c), new(min.X, min.Y + c),
        ];
    }

    /// <summary>
    /// A plate: drop shadow, gunmetal fill with a lighter top band, bronze frame, amber inner line, amber corner
    /// brackets and rivets. <paramref name="accent"/> colours the brackets (amber by default; a toast uses its kind).
    /// </summary>
    public static void DrawPanel(ImDrawListPtr dl, Vector2 min, Vector2 max, uint? accent = null, float alpha = 1f)
    {
        uint a = accent ?? Amber;
        var shadow = ChamferedRect(min + new Vector2(4f, 5f), max + new Vector2(4f, 5f), Chamfer);
        dl.AddConvexPolyFilled(ref shadow[0], shadow.Length, WithAlpha(Shadow, alpha));

        var outer = ChamferedRect(min, max, Chamfer);
        dl.AddConvexPolyFilled(ref outer[0], outer.Length, WithAlpha(PlateBottom, alpha));
        // Lighter top half, faded into the plate.
        float midY = min.Y + (max.Y - min.Y) * 0.45f;
        dl.AddRectFilledMultiColor(new Vector2(min.X + Chamfer * 0.6f, min.Y + 2f), new Vector2(max.X - Chamfer * 0.6f, midY),
            WithAlpha(PlateTop, alpha), WithAlpha(PlateTop, alpha), WithAlpha(PlateTop, 0f), WithAlpha(PlateTop, 0f));

        dl.AddPolyline(ref outer[0], outer.Length, WithAlpha(Bronze, alpha), ImDrawFlags.Closed, 2.2f);
        var inner = ChamferedRect(min + new Vector2(3.5f, 3.5f), max - new Vector2(3.5f, 3.5f), Chamfer - 2f);
        dl.AddPolyline(ref inner[0], inner.Length, WithAlpha(a, alpha * 0.28f), ImDrawFlags.Closed, 1f);

        // Corner brackets over the chamfers, and a rivet beside each.
        float len = 14f;
        uint bracket = WithAlpha(a, alpha);
        void Corner(Vector2 p, Vector2 dx, Vector2 dy)
        {
            dl.AddLine(p + dy * Chamfer, p + dx * Chamfer, bracket, 2.6f);
            dl.AddLine(p + dx * Chamfer, p + dx * (Chamfer + len), bracket, 2.6f);
            dl.AddLine(p + dy * Chamfer, p + dy * (Chamfer + len * 0.7f), bracket, 2.6f);
            dl.AddCircleFilled(p + dx * (Chamfer + len + 7f) + dy * 4f, 1.8f, WithAlpha(BronzeDark, alpha));
        }
        Corner(min, new Vector2(1, 0), new Vector2(0, 1));
        Corner(new Vector2(max.X, min.Y), new Vector2(-1, 0), new Vector2(0, 1));
        Corner(new Vector2(min.X, max.Y), new Vector2(1, 0), new Vector2(0, -1));
        Corner(max, new Vector2(-1, 0), new Vector2(0, -1));
    }

    /// <summary>Outlined text in the HUD's style.</summary>
    public static void TextAt(ImDrawListPtr dl, float size, Vector2 pos, string text, uint color, float wrap = 0f)
    {
        var font = ImGui.GetFont();
        uint outline = WithAlpha(0xE0000000u, (color >> 24) / 255f);
        dl.AddText(font, size, pos + new Vector2(1f, 1.5f), outline, text, wrap);
        dl.AddText(font, size, pos, color, text, wrap);
    }

    public static Vector2 Measure(float size, string text, float wrap = 0f) =>
        ImGui.GetFont().CalcTextSizeA(size, float.MaxValue, wrap, text);

    /// <summary>A section title: amber text with a thin amber rule fading out to the right.</summary>
    public static float DrawTitle(ImDrawListPtr dl, Vector2 pos, float width, string title, float size = 18f, uint? color = null)
    {
        uint c = color ?? Amber;
        TextAt(dl, size, pos, title, c);
        float y = pos.Y + size + 3f;
        dl.AddRectFilledMultiColor(new Vector2(pos.X, y), new Vector2(pos.X + width, y + 1.5f),
            WithAlpha(c, 0.9f), WithAlpha(c, 0f), WithAlpha(c, 0f), WithAlpha(c, 0.9f));
        return size + 8f;
    }

    /// <summary>A recessed track with a segmented glowing fill and an optional centred label.</summary>
    public static void DrawBar(ImDrawListPtr dl, Vector2 min, Vector2 max, float fraction, uint? fill = null, string? label = null)
    {
        uint f = fill ?? Amber;
        fraction = Math.Clamp(fraction, 0f, 1f);
        var track = ChamferedRect(min, max, 4f);
        dl.AddConvexPolyFilled(ref track[0], track.Length, Track);
        dl.AddPolyline(ref track[0], track.Length, BronzeDark, ImDrawFlags.Closed, 1.2f);

        float innerMinX = min.X + 3f, innerMaxX = max.X - 3f;
        float right = innerMinX + (innerMaxX - innerMinX) * fraction;
        const float segment = 9f, gap = 2f;
        for (float x = innerMinX; x < right; x += segment + gap)
        {
            float x1 = MathF.Min(x + segment, right);
            dl.AddRectFilledMultiColor(new Vector2(x, min.Y + 3f), new Vector2(x1, max.Y - 3f),
                WithAlpha(f, 1f), WithAlpha(f, 1f), WithAlpha(f, 0.65f), WithAlpha(f, 0.65f));
        }
        if (fraction > 0f)
            dl.AddRectFilled(new Vector2(innerMinX, min.Y - 1f), new Vector2(right, max.Y + 1f), WithAlpha(f, 0.10f), 3f);

        if (!string.IsNullOrEmpty(label))
        {
            float size = MathF.Min(16f, (max.Y - min.Y) - 2f);
            var sz = Measure(size, label);
            TextAt(dl, size, new Vector2(MathF.Floor((min.X + max.X - sz.X) * 0.5f), MathF.Floor((min.Y + max.Y - sz.Y) * 0.5f)), label, Text);
        }
    }

    /// <summary>A key cap ("E", "F2", "1"): a small plate with an amber edge. Returns its width.</summary>
    public static float DrawKeyCap(ImDrawListPtr dl, Vector2 pos, string key, float size = 16f, bool lit = true)
    {
        var sz = Measure(size, key);
        float w = MathF.Max(sz.X + 12f, size + 8f), h = size + 8f;
        var min = pos;
        var max = pos + new Vector2(w, h);
        var cap = ChamferedRect(min, max, 3.5f);
        dl.AddConvexPolyFilled(ref cap[0], cap.Length, Rgba(40, 36, 32, 245));
        dl.AddPolyline(ref cap[0], cap.Length, lit ? Amber : AmberDim, ImDrawFlags.Closed, 1.4f);
        dl.AddLine(new Vector2(min.X + 3f, max.Y - 2f), new Vector2(max.X - 3f, max.Y - 2f), Rgba(0, 0, 0, 160), 1.5f);
        TextAt(dl, size, new Vector2(MathF.Floor(min.X + (w - sz.X) * 0.5f), min.Y + 3f), key, lit ? Text : TextDim);
        return w;
    }

    /// <summary>A menu button plate: the label on the left, its key cap on the right; amber glow when hovered.</summary>
    public static void DrawButton(ImDrawListPtr dl, Vector2 min, Vector2 max, string label, string key, bool hovered, bool enabled)
    {
        var plate = ChamferedRect(min, max, 6f);
        dl.AddConvexPolyFilled(ref plate[0], plate.Length, hovered ? Rgba(70, 50, 28, 245) : Rgba(34, 32, 30, 240));
        dl.AddPolyline(ref plate[0], plate.Length, hovered ? Amber : Bronze, ImDrawFlags.Closed, hovered ? 2f : 1.4f);
        if (hovered)
            dl.AddRectFilled(min + new Vector2(6f, 3f), new Vector2(max.X - 6f, min.Y + 6f), WithAlpha(Amber, 0.25f), 2f);

        const float textSize = 20f;
        float textY = MathF.Floor(min.Y + (max.Y - min.Y - textSize) * 0.5f);
        TextAt(dl, textSize, new Vector2(min.X + 18f, textY), label, enabled ? Text : TextDim);
        if (!string.IsNullOrEmpty(key))
        {
            float capW = Measure(15f, key).X + 12f;
            DrawKeyCap(dl, new Vector2(max.X - 14f - MathF.Max(capW, 23f), MathF.Floor(min.Y + (max.Y - min.Y - 23f) * 0.5f)), key, 15f, enabled);
        }
    }

    /// <summary>A small label plate (pointer distances, short tags).</summary>
    public static void DrawTag(ImDrawListPtr dl, Vector2 center, string text, uint color, float size = 16f)
    {
        var sz = Measure(size, text);
        var min = new Vector2(MathF.Floor(center.X - sz.X * 0.5f - 9f), MathF.Floor(center.Y - sz.Y * 0.5f - 5f));
        var max = min + new Vector2(sz.X + 18f, sz.Y + 10f);
        var plate = ChamferedRect(min, max, 5f);
        dl.AddConvexPolyFilled(ref plate[0], plate.Length, Rgba(14, 14, 16, 225));
        dl.AddPolyline(ref plate[0], plate.Length, WithAlpha(color, 0.8f), ImDrawFlags.Closed, 1.3f);
        TextAt(dl, size, min + new Vector2(9f, 5f), text, color);
    }

    /// <summary>
    /// Styles an ImGui window as a HUD plate: its own background is hidden and <see cref="DrawPanel"/> is drawn behind
    /// its contents (from last frame's size, so auto-resizing windows settle after one frame). Pair with
    /// <see cref="EndWindow"/>.
    /// </summary>
    public static bool BeginWindow(string id, Vector2 pos, Vector2 pivot, float width, uint? accent = null)
    {
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always, pivot);
        ImGui.SetNextWindowSize(new Vector2(width, 0f), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16f, 12f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6f, 4f));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.Separator, WithAlpha(Bronze, 0.8f));
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBackground;
        bool open = ImGui.Begin(id, flags);
        if (open)
        {
            var p = ImGui.GetWindowPos();
            var s = ImGui.GetWindowSize();
            if (s.Y > 4f)
                DrawPanel(ImGui.GetWindowDrawList(), p, p + s, accent);
        }
        return open;
    }

    public static void EndWindow()
    {
        ImGui.End();
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar(3);
    }

    /// <summary>Inside a themed window: a bar on its own row, full width.</summary>
    public static void BarRow(float fraction, uint? fill, string? label, float height = 18f)
    {
        var pos = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        DrawBar(ImGui.GetWindowDrawList(), pos, pos + new Vector2(w, height), fraction, fill, label);
        ImGui.Dummy(new Vector2(w, height));
    }

    /// <summary>Inside a themed window: a title on its own row.</summary>
    public static void TitleRow(string title, float size = 18f, uint? color = null)
    {
        var pos = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        float h = DrawTitle(ImGui.GetWindowDrawList(), pos, w, title, size, color);
        ImGui.Dummy(new Vector2(w, h));
    }

    /// <summary>Inside a themed window: a key cap followed by a line of text.</summary>
    public static void KeyRow(string key, string text, uint color, float size = 17f)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        float capW = DrawKeyCap(dl, pos, key, size - 2f, color != TextDim);
        TextAt(dl, size, pos + new Vector2(capW + 8f, 3f), text, color);
        ImGui.Dummy(new Vector2(capW + 8f + Measure(size, text).X, size + 8f));
    }

    /// <summary>Inside a themed window: wrapped body text.</summary>
    public static void WrappedRow(string text, uint color, float size = 17f)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        var sz = Measure(size, text, w);
        TextAt(dl, size, pos, text, color, w);
        ImGui.Dummy(new Vector2(w, sz.Y));
    }

    /// <summary>Inside a themed window: one line of text.</summary>
    public static void LineRow(string text, uint color, float size = 17f)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        TextAt(dl, size, pos, text, color);
        ImGui.Dummy(new Vector2(Measure(size, text).X, size + 1f));
    }
}
