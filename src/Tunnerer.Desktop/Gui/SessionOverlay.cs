namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core.Config;

public enum SessionOverlayAction
{
    None,
    Resume,
    ShowControls,
    Back,
    Quit,
}

/// <summary>
/// The pause menu and the controls screen, plus a short reminder of the two keys that open them at the start of a
/// match. Drawn on the foreground draw list so it sits above every other HUD element.
/// </summary>
public sealed class SessionOverlay
{
    private const float KeyReminderSeconds = 10f;

    private const uint Dim = 0xA0000000u;
    private const uint PanelFill = 0xF0201A16u;
    private const uint PanelBorder = 0xFF6A8CA8u;
    private const uint TitleColor = 0xFFFFFFFFu;
    private const uint TextColor = 0xFFE0E0E0u;
    private const uint KeyColor = 0xFF5AE6FFu;
    private const uint DimTextColor = 0xFFA0A0A0u;
    private const uint ButtonFill = 0xFF3A3028u;
    private const uint ButtonHover = 0xFF7A5A30u;

    private static readonly (string Keys, string Action)[] Controls =
    [
        ("W A S D", "Drive and dig"),
        ("Mouse", "Aim the turret"),
        ("Left click / Space", "Fire"),
        ("E", $"Build a relay ({Tweaks.Machine.RelayDirtCost} dirt); press again to plant it"),
        ("F9", "Thermal view"),
        ("Esc / P", "Pause"),
        ("F1", "This screen"),
        ("F10", "Screenshot"),
        ("F11", "Renderer debug overlay"),
    ];

    private static readonly string[] HowToPlay =
    [
        "Digging and firing heat your tank. Past the limit the heat burns your shield.",
        "Park in your base to cool down and repair.",
        "A relay planted in sight of your base network pumps nearby heat back to the base.",
        $"Each tank has {Tweaks.Tank.MaxLives} lives. Destroy the enemy tank until it has none left.",
    ];

    private float _playTime;

    // A button only reacts to a click that began after its screen opened. Left click is also "fire", so without
    // this, pausing while shooting and then letting go over a button would press it.
    private bool _wasOpen;
    private bool _wasShowingControls;
    private bool _pressArmed;

    public SessionOverlayAction Draw(
        (float x, float y, float w, float h) viewport,
        bool paused,
        bool showControls,
        int seed,
        float deltaTime,
        bool showKeyReminder = true)
    {
        if (viewport.w <= 0 || viewport.h <= 0)
            return SessionOverlayAction.None;

        var dl = ImGui.GetForegroundDrawList();
        if (!paused || !_wasOpen || showControls != _wasShowingControls)
            _pressArmed = false;
        else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            _pressArmed = true;
        _wasOpen = paused;
        _wasShowingControls = showControls;

        if (!paused)
        {
            _playTime += MathF.Max(0f, deltaTime);
            if (showKeyReminder && _playTime < KeyReminderSeconds)
                DrawText(dl, 16f, new Vector2(viewport.x + 12f, viewport.y + viewport.h - 26f),
                    "F1 controls    Esc pause", TextColor);
            return SessionOverlayAction.None;
        }

        var displaySize = ImGui.GetIO().DisplaySize;
        dl.AddRectFilled(Vector2.Zero, displaySize, Dim);
        return showControls ? DrawControls(dl, viewport) : DrawPauseMenu(dl, viewport, seed);
    }

    private SessionOverlayAction DrawPauseMenu(ImDrawListPtr dl, (float x, float y, float w, float h) vp, int seed)
    {
        const float panelW = 380f, panelH = 318f, buttonW = 300f, buttonH = 46f, gap = 12f;
        Vector2 min = PanelOrigin(vp, panelW, panelH);
        DrawPanel(dl, min, new Vector2(panelW, panelH));

        float cx = min.X + panelW * 0.5f;
        DrawCentered(dl, 38f, cx, min.Y + 22f, "PAUSED", TitleColor);

        var action = SessionOverlayAction.None;
        float bx = cx - buttonW * 0.5f;
        float by = min.Y + 84f;
        if (Button(dl, new Vector2(bx, by), new Vector2(buttonW, buttonH), "Resume", "Esc"))
            action = SessionOverlayAction.Resume;
        by += buttonH + gap;
        if (Button(dl, new Vector2(bx, by), new Vector2(buttonW, buttonH), "Controls", "F1"))
            action = SessionOverlayAction.ShowControls;
        by += buttonH + gap;
        if (Button(dl, new Vector2(bx, by), new Vector2(buttonW, buttonH), "Quit game", "Q"))
            action = SessionOverlayAction.Quit;

        DrawCentered(dl, 14f, cx, min.Y + panelH - 30f, $"Map seed {seed}  -  replay it with --seed={seed}", DimTextColor);
        return action;
    }

    private SessionOverlayAction DrawControls(ImDrawListPtr dl, (float x, float y, float w, float h) vp)
    {
        const float panelW = 720f, rowH = 26f, buttonW = 220f, buttonH = 42f;
        float panelH = 78f + Controls.Length * rowH + 20f + HowToPlay.Length * 22f + 24f + buttonH + 20f;
        Vector2 min = PanelOrigin(vp, panelW, panelH);
        DrawPanel(dl, min, new Vector2(panelW, panelH));

        float cx = min.X + panelW * 0.5f;
        DrawCentered(dl, 30f, cx, min.Y + 20f, "CONTROLS", TitleColor);

        float y = min.Y + 78f;
        foreach (var (keys, actionText) in Controls)
        {
            DrawText(dl, 18f, new Vector2(min.X + 34f, y), keys, KeyColor);
            DrawText(dl, 18f, new Vector2(min.X + 230f, y), actionText, TextColor);
            y += rowH;
        }

        y += 20f;
        foreach (string line in HowToPlay)
        {
            DrawText(dl, 15f, new Vector2(min.X + 34f, y), line, DimTextColor);
            y += 22f;
        }

        y += 24f;
        bool back = Button(dl, new Vector2(cx - buttonW * 0.5f, y), new Vector2(buttonW, buttonH), "Back", "Esc");
        return back ? SessionOverlayAction.Back : SessionOverlayAction.None;
    }

    /// <summary>Centres a panel in the game viewport, keeping it on screen when the viewport is small.</summary>
    private static Vector2 PanelOrigin((float x, float y, float w, float h) vp, float panelW, float panelH)
    {
        var displaySize = ImGui.GetIO().DisplaySize;
        float x = vp.x + (vp.w - panelW) * 0.5f;
        float y = vp.y + (vp.h - panelH) * 0.5f;
        x = MathF.Max(0f, MathF.Min(x, displaySize.X - panelW));
        y = MathF.Max(0f, MathF.Min(y, displaySize.Y - panelH));
        return new Vector2(MathF.Floor(x), MathF.Floor(y));
    }

    private static void DrawPanel(ImDrawListPtr dl, Vector2 min, Vector2 size)
    {
        dl.AddRectFilled(min, min + size, PanelFill, 10f);
        dl.AddRect(min, min + size, PanelBorder, 10f, ImDrawFlags.None, 2f);
    }

    /// <summary>
    /// A button that reports a click when the left mouse button is released over it, provided the press also
    /// started on it while this screen was open.
    /// </summary>
    private bool Button(ImDrawListPtr dl, Vector2 min, Vector2 size, string label, string key)
    {
        Vector2 max = min + size;
        Vector2 mouse = ImGui.GetIO().MousePos;
        bool hovered = mouse.X >= min.X && mouse.Y >= min.Y && mouse.X < max.X && mouse.Y < max.Y;

        dl.AddRectFilled(min, max, hovered ? ButtonHover : ButtonFill, 6f);
        dl.AddRect(min, max, PanelBorder, 6f, ImDrawFlags.None, 1.5f);

        const float textSize = 20f;
        float textY = min.Y + (size.Y - textSize) * 0.5f;
        DrawText(dl, textSize, new Vector2(min.X + 18f, textY), label, TitleColor);
        float keyW = Measure(textSize, key);
        DrawText(dl, textSize, new Vector2(max.X - 18f - keyW, textY), key, KeyColor);

        if (!hovered || !_pressArmed || !ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            return false;
        Vector2 pressedAt = ImGui.GetIO().MouseClickedPos[0];
        return pressedAt.X >= min.X && pressedAt.Y >= min.Y && pressedAt.X < max.X && pressedAt.Y < max.Y;
    }

    private static float Measure(float size, string text) =>
        ImGui.GetFont().CalcTextSizeA(size, float.MaxValue, 0f, text).X;

    private static void DrawCentered(ImDrawListPtr dl, float size, float centerX, float y, string text, uint color) =>
        DrawText(dl, size, new Vector2(MathF.Floor(centerX - Measure(size, text) * 0.5f), y), text, color);

    private static void DrawText(ImDrawListPtr dl, float size, Vector2 pos, string text, uint color)
    {
        var font = ImGui.GetFont();
        dl.AddText(font, size, pos + new Vector2(1f, 1f), 0xE0000000u, text);
        dl.AddText(font, size, pos, color, text);
    }
}
