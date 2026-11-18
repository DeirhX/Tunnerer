namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Desktop.Net;

/// <summary>
/// Draws the network menu (<see cref="NetMenuModel"/>) over the paused game, in the style of the pause menu, and turns
/// mouse clicks into the model's calls. Keys and typed text reach the model through the game, not through here.
/// </summary>
public sealed class NetMenuView
{
    private const float PanelW = 580f, RowH = 42f, LabelW = 116f, Pad = 30f, ButtonH = 46f;
    private const float StepperW = 252f, ArrowW = 34f, StepGap = 6f;

    // The same rule as the pause menu's buttons: a click counts only when the press began after this screen opened.
    private NetMenuModel? _model;
    private NetMenuScreen _screen;
    private bool _wasOpen;
    private bool _pressArmed;

    /// <summary>Draws the open screen and returns what a click on it asked for.</summary>
    public NetMenuResult Draw((float x, float y, float w, float h) viewport, NetMenuModel model)
    {
        if (viewport.w <= 0 || viewport.h <= 0)
            return NetMenuResult.Nothing;

        if (!ReferenceEquals(model, _model) || model.Screen != _screen)
            _wasOpen = false;
        _model = model;
        _screen = model.Screen;
        if (!_wasOpen)
            _pressArmed = false;
        else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            _pressArmed = true;

        var dl = ImGui.GetForegroundDrawList();
        dl.AddRectFilled(Vector2.Zero, ImGui.GetIO().DisplaySize, 0xA0000000u);

        float height = model.Screen == NetMenuScreen.Join ? JoinHeight(model) : CreateHeight(model);
        Vector2 min = PanelOrigin(viewport, PanelW, height);
        HudTheme.DrawPanel(dl, min, min + new Vector2(PanelW, height));
        float cx = min.X + PanelW * 0.5f;
        string title = model.Screen == NetMenuScreen.Join ? "NETWORK MATCH" : "CREATE ROOM";
        Centered(dl, 34f, cx, min.Y + 20f, title, HudTheme.Amber);
        HudTheme.DrawTitle(dl, new Vector2(min.X + Pad, min.Y + 62f), PanelW - 2 * Pad, "", 1f);

        float y = min.Y + 80f;
        y = DrawNotice(dl, model, min.X + Pad, y);
        var result = model.Screen == NetMenuScreen.Join
            ? DrawJoin(dl, model, min.X + Pad, y)
            : DrawCreate(dl, model, min.X + Pad, y);
        _wasOpen = true;
        return result;
    }

    // ---- Screens -----------------------------------------------------------------------------------------------------

    private static float NoticeHeight(NetMenuModel m) =>
        m.Notice is { } n ? 30f + HudTheme.Measure(16f, n.Text, PanelW - 2 * Pad - 24f).Y + 18f : 0f;

    private static float JoinHeight(NetMenuModel m) =>
        80f + NoticeHeight(m) + RowH * (m.SecretRequired ? 3 : 2) + 20f + 40f + 16f + ButtonH + 50f;

    private static float CreateHeight(NetMenuModel m) =>
        80f + NoticeHeight(m) + RowH * 5 + (MapPresets.Warning(m.Map) != null ? 24f : 0f) + 16f + 28f + ButtonH + 30f;

    private NetMenuResult DrawJoin(ImDrawListPtr dl, NetMenuModel m, float x, float y)
    {
        DrawTextRow(dl, m, NetMenuField.Name, "You", x, ref y);
        DrawTextRow(dl, m, NetMenuField.Room, "Room", x, ref y);
        if (m.SecretRequired)
            DrawTextRow(dl, m, NetMenuField.Secret, "Secret", x, ref y);

        y += 6f;
        Text(dl, 14f, new Vector2(x, y), $"Relay  {m.Relay.Host}", HudTheme.TextDim);
        y += 22f;
        Text(dl, 14f, new Vector2(x, y), "A room that does not exist yet is opened with your last room settings.", HudTheme.TextDim);
        y += 34f;

        var result = NetMenuResult.Nothing;
        float buttonW = (PanelW - 2 * Pad - 14f) * 0.5f;
        if (Button(dl, new Vector2(x, y), new Vector2(buttonW, ButtonH), "Join", "Enter"))
            result = m.Submit();
        if (Button(dl, new Vector2(x + buttonW + 14f, y), new Vector2(buttonW, ButtonH), "Create a room", "F2"))
            m.OpenCreate();
        y += ButtonH + 16f;
        Centered(dl, 14f, x + (PanelW - 2 * Pad) * 0.5f, y, "Tab next field    Esc back", HudTheme.TextDim);
        return result;
    }

    private NetMenuResult DrawCreate(ImDrawListPtr dl, NetMenuModel m, float x, float y)
    {
        DrawTextRow(dl, m, NetMenuField.Name, "You", x, ref y);
        DrawTextRow(dl, m, NetMenuField.CreateName, "Room name", x, ref y);

        // Tanks: the slot count is the bot setting, because every tank nobody takes is a bot.
        DrawChoiceRow(dl, m, NetMenuField.Tanks, "Tanks", m.Players.ToString(System.Globalization.CultureInfo.InvariantCulture), x, y);
        Text(dl, 14f, new Vector2(x + LabelW + StepperW + 14f, y + 13f), "the rest are bots", HudTheme.TextDim);
        y += RowH;

        DrawChoiceRow(dl, m, NetMenuField.Map, "Map", MapPresets.Label(m.Map), x, y);
        y += RowH;
        if (MapPresets.Warning(m.Map) is { } warning)
        {
            Text(dl, 14f, new Vector2(x + LabelW, y - 8f), warning, HudTheme.Warn);
            y += 24f;
        }

        DrawSeedRow(dl, m, x, y);
        y += RowH + 16f;
        Text(dl, 14f, new Vector2(x, y), "Every machine must simulate the whole map in real time.", HudTheme.TextDim);
        y += 28f;

        var result = NetMenuResult.Nothing;
        float buttonW = (PanelW - 2 * Pad - 14f) * 0.5f;
        if (Button(dl, new Vector2(x, y), new Vector2(buttonW, ButtonH), "Create and join", "Enter"))
            result = m.Submit();
        if (Button(dl, new Vector2(x + buttonW + 14f, y), new Vector2(buttonW, ButtonH), "Back", "Esc"))
            result = m.Back();
        return result;
    }

    private static float DrawNotice(ImDrawListPtr dl, NetMenuModel m, float x, float y)
    {
        if (m.Notice is not { } notice)
            return y;
        float width = PanelW - 2 * Pad;
        float height = NoticeHeight(m) - 8f;
        uint accent = notice.IsError ? HudTheme.Danger : HudTheme.Amber;
        HudTheme.DrawPanel(dl, new Vector2(x, y), new Vector2(x + width, y + height), accent);
        Text(dl, 15f, new Vector2(x + 12f, y + 8f), notice.Title, accent);
        HudTheme.TextAt(dl, 16f, new Vector2(x + 12f, y + 30f), notice.Text, HudTheme.Text, width - 24f);
        return y + height + 14f;
    }

    // ---- Rows ---------------------------------------------------------------------------------------------------------

    private void DrawTextRow(ImDrawListPtr dl, NetMenuModel m, NetMenuField field, string label, float x, ref float y)
    {
        var text = m.TextFieldOf(field)!;
        Text(dl, 18f, new Vector2(x, y + 10f), label, HudTheme.TextDim);
        var min = new Vector2(x + LabelW, y + 2f);
        var size = new Vector2(PanelW - 2 * Pad - LabelW, RowH - 8f);
        bool focused = m.Focus == field;
        if (_wasOpen && MenuInput.Hovered(min, size) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            m.Click(field);
        DrawTextBox(dl, min, size, text, focused);
        y += RowH;
    }

    private static void DrawTextBox(ImDrawListPtr dl, Vector2 min, Vector2 size, TextField field, bool focused)
    {
        var max = min + size;
        dl.AddRectFilled(min, max, HudTheme.Track, 4f);
        dl.AddRect(min, max, focused ? HudTheme.Amber : HudTheme.Bronze, 4f, ImDrawFlags.None, focused ? 2f : 1.2f);

        const float textSize = 20f;
        string shown = field.Masked ? new string('*', field.Text.Length) : field.Text;
        float caretX = HudTheme.Measure(textSize, shown[..field.Caret]).X;
        float room = size.X - 20f;
        float scroll = MathF.Max(0f, caretX - room);
        var origin = new Vector2(min.X + 10f - scroll, min.Y + (size.Y - textSize) * 0.5f);
        dl.PushClipRect(min + new Vector2(2f, 0f), max - new Vector2(2f, 0f), true);
        HudTheme.TextAt(dl, textSize, origin, shown, HudTheme.Text);
        if (focused && (int)(ImGui.GetTime() * 2.0) % 2 == 0)
            dl.AddLine(new Vector2(origin.X + caretX, origin.Y), new Vector2(origin.X + caretX, origin.Y + textSize), HudTheme.Amber, 2f);
        dl.PopClipRect();
    }

    /// <summary>A row of the form "&lt; value &gt;": the arrows, or the Left and Right keys, change it.</summary>
    private void DrawChoiceRow(ImDrawListPtr dl, NetMenuModel m, NetMenuField field, string label, string value, float x, float y)
    {
        Text(dl, 18f, new Vector2(x, y + 10f), label, HudTheme.TextDim);
        bool focused = m.Focus == field;
        float boxX = x + LabelW;
        float height = RowH - 8f;

        // [<] [ value ] [>]: the value sits in a track like the text fields, with a gap to each arrow, and the track
        // takes the focus outline so the row shows where the keyboard is.
        if (SmallButton(dl, new Vector2(boxX, y + 2f), "<", focused))
            m.Adjust(field, -1);
        var track = new Vector2(boxX + ArrowW + StepGap, y + 2f);
        var trackSize = new Vector2(StepperW - 2f * (ArrowW + StepGap), height);
        dl.AddRectFilled(track, track + trackSize, HudTheme.Track, 4f);
        dl.AddRect(track, track + trackSize, focused ? HudTheme.Amber : HudTheme.Bronze, 4f, ImDrawFlags.None, focused ? 2f : 1.2f);
        Centered(dl, 20f, track.X + trackSize.X * 0.5f, y + 10f, value, focused ? HudTheme.Amber : HudTheme.Text);
        if (SmallButton(dl, new Vector2(boxX + StepperW - ArrowW, y + 2f), ">", focused))
            m.Adjust(field, +1);
        if (_wasOpen && MenuInput.Hovered(track, trackSize) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            m.Click(field);
    }

    private void DrawSeedRow(ImDrawListPtr dl, NetMenuModel m, float x, float y)
    {
        Text(dl, 18f, new Vector2(x, y + 10f), "Seed", HudTheme.TextDim);
        bool focused = m.Focus == NetMenuField.SeedMode;
        float rx = x + LabelW;
        if (Radio(dl, new Vector2(rx, y + 8f), "new map each match", !m.FixedSeed, focused, out float w1))
        {
            m.Click(NetMenuField.SeedMode);
            if (m.FixedSeed)
                m.Adjust(NetMenuField.SeedMode, 0);
        }
        rx += w1 + 22f;
        if (Radio(dl, new Vector2(rx, y + 8f), "fixed", m.FixedSeed, focused, out float w2))
        {
            m.Click(NetMenuField.SeedMode);
            if (!m.FixedSeed)
                m.Adjust(NetMenuField.SeedMode, 0);
        }
        if (m.FixedSeed)
        {
            var min = new Vector2(rx + w2 + 14f, y + 2f);
            var size = new Vector2(x + PanelW - 2 * Pad - min.X, RowH - 8f);
            if (_wasOpen && MenuInput.Hovered(min, size) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                m.Click(NetMenuField.SeedValue);
            DrawTextBox(dl, min, size, m.SeedField, m.Focus == NetMenuField.SeedValue);
        }
    }

    // ---- Parts --------------------------------------------------------------------------------------------------------

    private bool Radio(ImDrawListPtr dl, Vector2 at, string label, bool on, bool focused, out float width)
    {
        const float size = 18f;
        float textW = HudTheme.Measure(size, label).X;
        width = 26f + textW;
        var box = new Vector2(width, 26f);
        bool hovered = MenuInput.Hovered(at, box);
        var center = at + new Vector2(10f, 13f);
        dl.AddCircle(center, 8f, focused ? HudTheme.Amber : HudTheme.Bronze, 0, 1.6f);
        if (on)
            dl.AddCircleFilled(center, 4.5f, HudTheme.Amber);
        Text(dl, size, at + new Vector2(26f, 2f), label, hovered || on ? HudTheme.Text : HudTheme.TextDim);
        return MenuInput.Clicked(at, box, hovered, _pressArmed);
    }

    private bool SmallButton(ImDrawListPtr dl, Vector2 min, string label, bool focused)
    {
        var size = new Vector2(34f, RowH - 8f);
        bool hovered = MenuInput.Hovered(min, size);
        HudTheme.DrawButton(dl, min, min + size, string.Empty, string.Empty, hovered, true);
        Centered(dl, 22f, min.X + size.X * 0.5f, min.Y + 5f, label, focused || hovered ? HudTheme.Amber : HudTheme.Text);
        return MenuInput.Clicked(min, size, hovered, _pressArmed);
    }

    private bool Button(ImDrawListPtr dl, Vector2 min, Vector2 size, string label, string key)
    {
        bool hovered = MenuInput.Hovered(min, size);
        HudTheme.DrawButton(dl, min, min + size, label, key, hovered, true);
        return MenuInput.Clicked(min, size, hovered, _pressArmed);
    }

    private static Vector2 PanelOrigin((float x, float y, float w, float h) vp, float panelW, float panelH)
    {
        var display = ImGui.GetIO().DisplaySize;
        float x = MathF.Max(0f, MathF.Min(vp.x + (vp.w - panelW) * 0.5f, display.X - panelW));
        float y = MathF.Max(0f, MathF.Min(vp.y + (vp.h - panelH) * 0.5f, display.Y - panelH));
        return new Vector2(MathF.Floor(x), MathF.Floor(y));
    }

    private static void Text(ImDrawListPtr dl, float size, Vector2 pos, string text, uint color) =>
        HudTheme.TextAt(dl, size, pos, text, color);

    private static void Centered(ImDrawListPtr dl, float size, float centerX, float y, string text, uint color) =>
        Text(dl, size, new Vector2(MathF.Floor(centerX - HudTheme.Measure(size, text).X * 0.5f), y), text, color);
}
