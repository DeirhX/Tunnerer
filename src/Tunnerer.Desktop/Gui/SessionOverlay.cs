namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core.Config;
using Tunnerer.Desktop.Presentation;

public enum SessionOverlayAction
{
    None,
    Resume,
    ShowControls,
    Back,
    Quit,
}

/// <summary>
/// Sound settings as the pause menu shows them. The menu changes <see cref="Volume"/> and <see cref="Muted"/>
/// when the player uses its controls; the caller applies the change.
/// </summary>
public record struct SoundMenuState(bool Available, float Volume, bool Muted);

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
        .. MachinePresentations.InSlotOrder.Select(m =>
            (m.KeyLabel, $"Build a {m.Name} ({m.DirtCost} dirt); press again to plant it")),
        ("H / F9", "Thermal view (enemy tunnels glow)"),
        ("Esc / P", "Pause"),
        ("F1", "This screen"),
        ("M, - / =", "Mute sound, volume down / up"),
        ("F10", "Screenshot"),
        ("F11", "Renderer debug overlay (then 1-7 toggle passes)"),
        ("Enter", "New match, once a match is over"),
    ];

    private static readonly string[] HowToPlay =
    [
        "Digging and firing heat your tank. Past the limit the heat burns your shield.",
        "Park in your base to cool down and repair.",
        "A relay planted in sight of your base network pumps nearby heat back to the base.",
        "Dirt you bring home goes into the base store; a docked tank can build relays from it.",
        $"Each tank has {Tweaks.Tank.MaxLives} lives. Destroy the enemy tank until it has none left.",
    ];

    private float _playTime;

    // A button only reacts to a click that began after its screen opened. Left click is also "fire", so without
    // this, pausing while shooting and then letting go over a button would press it.
    private bool _wasOpen;
    private bool _wasShowingControls;
    private bool _pressArmed;
    private bool _draggingVolume;

    public SessionOverlayAction Draw(
        (float x, float y, float w, float h) viewport,
        bool paused,
        bool showControls,
        int seed,
        string replayArguments,
        float deltaTime,
        ref SoundMenuState sound,
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

        if (!paused || showControls)
            _draggingVolume = false;

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
        return showControls ? DrawControls(dl, viewport) : DrawPauseMenu(dl, viewport, seed, replayArguments, ref sound);
    }

    private SessionOverlayAction DrawPauseMenu(
        ImDrawListPtr dl, (float x, float y, float w, float h) vp, int seed, string replayArguments, ref SoundMenuState sound)
    {
        const float panelW = 380f, panelH = 434f, buttonW = 300f, buttonH = 46f, gap = 12f;
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
        string soundLabel = !sound.Available ? "Sound: not available" : sound.Muted ? "Sound: off" : "Sound: on";
        if (Button(dl, new Vector2(bx, by), new Vector2(buttonW, buttonH), soundLabel, sound.Available ? "M" : "",
                enabled: sound.Available))
            sound.Muted = !sound.Muted;
        by += buttonH + gap;
        VolumeSlider(dl, new Vector2(bx, by), new Vector2(buttonW, buttonH), ref sound);
        by += buttonH + gap;
        if (Button(dl, new Vector2(bx, by), new Vector2(buttonW, buttonH), "Quit game", "Q"))
            action = SessionOverlayAction.Quit;

        DrawCentered(dl, 14f, cx, min.Y + panelH - 30f, $"Map seed {seed}  -  replay it with {replayArguments}", DimTextColor);
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
    private bool Button(ImDrawListPtr dl, Vector2 min, Vector2 size, string label, string key, bool enabled = true)
    {
        Vector2 max = min + size;
        Vector2 mouse = ImGui.GetIO().MousePos;
        // While the volume knob is being dragged the pointer may wander over a button; it must not react.
        bool hovered = enabled && !_draggingVolume &&
            mouse.X >= min.X && mouse.Y >= min.Y && mouse.X < max.X && mouse.Y < max.Y;

        dl.AddRectFilled(min, max, hovered ? ButtonHover : ButtonFill, 6f);
        dl.AddRect(min, max, PanelBorder, 6f, ImDrawFlags.None, 1.5f);

        const float textSize = 20f;
        float textY = min.Y + (size.Y - textSize) * 0.5f;
        DrawText(dl, textSize, new Vector2(min.X + 18f, textY), label, enabled ? TitleColor : DimTextColor);
        float keyW = Measure(textSize, key);
        DrawText(dl, textSize, new Vector2(max.X - 18f - keyW, textY), key, KeyColor);

        if (!hovered || !_pressArmed || !ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            return false;
        Vector2 pressedAt = ImGui.GetIO().MouseClickedPos[0];
        return pressedAt.X >= min.X && pressedAt.Y >= min.Y && pressedAt.X < max.X && pressedAt.Y < max.Y;
    }

    /// <summary>
    /// The volume row: a track with a knob, the level in percent beside it. Clicking anywhere on the row moves
    /// the knob there and it then follows the pointer until the button is let go, even off the row. Levels are
    /// whole steps of 5%, the same as the - and = keys. Moving the knob turns the sound back on if it was muted.
    /// </summary>
    private void VolumeSlider(ImDrawListPtr dl, Vector2 min, Vector2 size, ref SoundMenuState sound)
    {
        Vector2 max = min + size;
        Vector2 mouse = ImGui.GetIO().MousePos;
        bool active = sound.Available;
        bool hovered = active && mouse.X >= min.X && mouse.Y >= min.Y && mouse.X < max.X && mouse.Y < max.Y;

        const float textSize = 20f, knobRadius = 9f;
        float trackLeft = min.X + 104f, trackRight = max.X - 74f;
        float trackY = MathF.Floor(min.Y + size.Y * 0.5f);

        // Like a button, the row only takes a press that began on it after the menu opened.
        if (hovered && _wasOpen && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            _draggingVolume = true;
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) || !active)
            _draggingVolume = false;
        if (_draggingVolume)
        {
            float fraction = Math.Clamp((mouse.X - trackLeft) / (trackRight - trackLeft), 0f, 1f);
            float volume = MathF.Round(fraction * 20f) / 20f;
            if (volume != sound.Volume || sound.Muted)
            {
                sound.Volume = volume;
                sound.Muted = false;
            }
        }

        dl.AddRectFilled(min, max, hovered || _draggingVolume ? ButtonHover : ButtonFill, 6f);
        dl.AddRect(min, max, PanelBorder, 6f, ImDrawFlags.None, 1.5f);

        float textY = min.Y + (size.Y - textSize) * 0.5f;
        DrawText(dl, textSize, new Vector2(min.X + 18f, textY), "Volume", active ? TitleColor : DimTextColor);

        bool audible = active && !sound.Muted;
        float knobX = MathF.Floor(trackLeft + (trackRight - trackLeft) * Math.Clamp(sound.Volume, 0f, 1f));
        dl.AddRectFilled(new Vector2(trackLeft, trackY - 3f), new Vector2(trackRight, trackY + 3f), 0xFF18120Eu, 3f);
        if (knobX > trackLeft)
            dl.AddRectFilled(new Vector2(trackLeft, trackY - 3f), new Vector2(knobX, trackY + 3f),
                audible ? KeyColor : DimTextColor, 3f);
        dl.AddCircleFilled(new Vector2(knobX, trackY), knobRadius, audible ? TitleColor : DimTextColor);
        dl.AddCircle(new Vector2(knobX, trackY), knobRadius, 0xFF000000u, 0, 1.5f);

        string level = !active ? "-" : sound.Muted ? "off" : $"{(int)MathF.Round(sound.Volume * 100f)}%";
        float levelW = Measure(textSize, level);
        DrawText(dl, textSize, new Vector2(max.X - 18f - levelW, textY), level, audible ? KeyColor : DimTextColor);
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
