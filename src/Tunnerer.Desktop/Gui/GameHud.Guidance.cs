namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Gui.Guidance;
using Tunnerer.Desktop.Rendering;

/// <summary>
/// The tutorial card (top left), the game-event toasts under it, and a pointer to whatever the current tutorial step
/// is about. The logic lives in <see cref="TutorialDirector"/> and <see cref="GameEventWatcher"/>; this only draws.
/// </summary>
public partial class GameHud
{
    private readonly NotificationFeed _feed = new();
    private readonly GameEventWatcher _events = new();
    private readonly TutorialDirector _tutorial = new();
    private World? _watchedWorld;
    private bool _tutorialHidden;
    private float _tutorialDoneAt = float.NaN;

    /// <summary>The camera of the game view: top-left in screen pixels and pixels per cell. Set before <see cref="Draw"/>.</summary>
    public (int X, int Y, int Scale) Camera { get; set; } = (0, 0, 1);

    private const float CardWidth = 470f;

    private void DrawGuidance(Tank player, World world)
    {
        if (!ReferenceEquals(world, _watchedWorld))
        {
            _watchedWorld = world;
            _events.Reset();
            _feed.Clear();
        }
        if (ImGui.IsKeyPressed(ImGuiKey.F2, false))
            _tutorialHidden = !_tutorialHidden;

        _events.Update(world, player, _feed, _time);
        _feed.Update(_time);
        var prompt = player.IsDead ? null : _tutorial.Update(world, player, _time);
        if (prompt == null && _tutorial.IsComplete && float.IsNaN(_tutorialDoneAt))
        {
            _tutorialDoneAt = _time;
            _feed.Post(_time, "tutorial-done", "Tutorial complete",
                "Push your network toward hot rock, keep harvesters cool and charged, and mind your temperature.",
                NotificationKind.Good, duration: 12f, cooldown: 1e9f);
        }

        var vp = ViewportRect;
        float y = vp.y + 14f;
        if (prompt is { } p && !_tutorialHidden)
        {
            y = DrawTutorialCard(p, vp.x + 14f, y) + 10f;
            if (p.Target is { } target)
                DrawPointer(target, vp);
        }
        else if (prompt != null)
        {
            var dlh = ImGui.GetForegroundDrawList();
            float capW = HudTheme.DrawKeyCap(dlh, new Vector2(vp.x + 16f, y), "F2", 13f, lit: false);
            HudTheme.TextAt(dlh, 15f, new Vector2(vp.x + 22f + capW, y + 2f), "show tutorial", HudTheme.TextDim);
            y += 28f;
        }
        DrawToasts(vp.x + 14f, y);
    }

    /// <summary>Draws the tutorial card and returns its bottom edge.</summary>
    private static float DrawTutorialCard(in TutorialPrompt prompt, float x, float y)
    {
        float bottom = y;
        if (HudTheme.BeginWindow("##Tutorial", new Vector2(x, y), Vector2.Zero, CardWidth))
        {
            var dl = ImGui.GetWindowDrawList();
            var head = ImGui.GetCursorScreenPos();
            float w = ImGui.GetContentRegionAvail().X;
            HudTheme.TextAt(dl, 14f, head, $"NEXT STEP  {prompt.Number} / {prompt.Count}", HudTheme.TextDim);
            float capW = HudTheme.Measure(13f, "F2").X + 12f;
            HudTheme.TextAt(dl, 14f, head + new Vector2(w - capW - 44f, 0f), "hide", HudTheme.TextDim);
            HudTheme.DrawKeyCap(dl, head + new Vector2(w - capW, -4f), "F2", 13f, lit: false);
            ImGui.Dummy(new Vector2(w, 18f));
            HudTheme.TitleRow(prompt.Title, 24f);
            HudTheme.WrappedRow(prompt.Body, HudTheme.Text, 17f);
            if (prompt.Progress != null)
            {
                ImGui.Dummy(new Vector2(1f, 4f));
                HudTheme.BarRow(prompt.ProgressFraction, prompt.ProgressFraction >= 1f ? HudTheme.Good : HudTheme.Amber,
                    prompt.Progress, 22f);
            }
            bottom = ImGui.GetWindowPos().Y + ImGui.GetWindowSize().Y;
        }
        HudTheme.EndWindow();
        return bottom;
    }

    private void DrawToasts(float x, float y)
    {
        var dl = ImGui.GetForegroundDrawList();
        const float titleSize = 19f, detailSize = 15.5f, padX = 16f, padY = 11f;
        float wrap = CardWidth - padX * 2f;
        foreach (var n in _feed.Active.Reverse())
        {
            float age = _time - n.PostedAt;
            float fade = Math.Clamp(MathF.Min(age * 4f, (n.ExpiresAt - _time) * 1.5f), 0f, 1f);
            float slide = (1f - Math.Clamp(age * 5f, 0f, 1f)) * -30f;
            var detailSz = HudTheme.Measure(detailSize, n.Detail, wrap);
            float h = padY + titleSize + 5f + detailSz.Y + padY;
            var min = new Vector2(x + slide, y);
            var max = new Vector2(x + slide + CardWidth, y + h);
            uint accent = n.Kind switch
            {
                NotificationKind.Good => HudTheme.Good,
                NotificationKind.Warning => HudTheme.Warn,
                NotificationKind.Danger => HudTheme.Danger,
                _ => HudTheme.Teal,
            };
            HudTheme.DrawPanel(dl, min, max, accent, fade);
            uint a = (uint)(fade * 255f) << 24;
            HudTheme.TextAt(dl, titleSize, new Vector2(min.X + padX, min.Y + padY), n.Title, (accent & 0x00FFFFFFu) | a);
            HudTheme.TextAt(dl, detailSize, new Vector2(min.X + padX, min.Y + padY + titleSize + 5f), n.Detail,
                (HudTheme.Text & 0x00FFFFFFu) | a, wrap);
            y += h + 8f;
        }
    }

    /// <summary>
    /// Points at a world cell: a pulsing ring with a bouncing arrow when it is on screen, otherwise an arrow at the edge
    /// of the view showing which way to go and how far.
    /// </summary>
    private void DrawPointer(Position target, (float x, float y, float w, float h) vp)
    {
        var dl = ImGui.GetForegroundDrawList();
        var cam = Camera;
        // The camera is in window pixels; the view rectangle is in UI units.
        float sx = vp.x + ToUi((target.X + 0.5f) * cam.Scale - cam.X);
        float sy = vp.y + ToUi((target.Y + 0.5f) * cam.Scale - cam.Y);
        float viewBottom = ViewBottom(vp);
        const float margin = 40f;
        float pulse = 0.5f + 0.5f * MathF.Sin(_time * 5f);
        uint color = HudTheme.Amber;

        bool onScreen = sx >= vp.x + margin && sx <= vp.x + vp.w - margin && sy >= vp.y + margin && sy <= viewBottom - margin;
        if (onScreen)
        {
            float r = 26f + 6f * pulse;
            dl.AddCircle(new Vector2(sx, sy), r, (uint)(0x80 + 0x7F * pulse) << 24 | (HudTheme.Amber & 0x00FFFFFFu), 48, 3f);
            dl.AddCircle(new Vector2(sx, sy), r + 5f, (uint)(0x30 + 0x30 * pulse) << 24 | (HudTheme.Amber & 0x00FFFFFFu), 48, 1.5f);
            float bob = 8f * MathF.Sin(_time * 4f);
            var tip = new Vector2(sx, sy - r - 6f + bob);
            dl.AddTriangleFilled(tip, tip + new Vector2(-12f, -20f), tip + new Vector2(12f, -20f), color);
            return;
        }

        // Off screen: an arrow on the edge of the view, toward the target.
        var center = new Vector2(vp.x + vp.w * 0.5f, (vp.y + viewBottom) * 0.5f);
        var dir = new Vector2(sx, sy) - center;
        float len = dir.Length();
        if (len < 1f)
            return;
        dir /= len;
        float halfW = vp.w * 0.5f - margin, halfH = (viewBottom - vp.y) * 0.5f - margin;
        float t = MathF.Min(halfW / MathF.Max(MathF.Abs(dir.X), 1e-3f), halfH / MathF.Max(MathF.Abs(dir.Y), 1e-3f));
        var at = center + dir * t;
        var side = new Vector2(-dir.Y, dir.X);
        var head = at + dir * 18f;
        dl.AddTriangleFilled(head, at - dir * 6f + side * 14f, at - dir * 6f - side * 14f, (uint)(0xB0 + 0x4F * pulse) << 24 | (HudTheme.Amber & 0x00FFFFFFu));
        dl.AddTriangle(head, at - dir * 6f + side * 14f, at - dir * 6f - side * 14f, HudTheme.BronzeDark, 1.5f);
        int cells = (int)MathF.Round(len * ImGuiPlatform.UiScale / MathF.Max(1, cam.Scale));
        HudTheme.DrawTag(dl, at - dir * 40f, $"{cells} m", HudTheme.Amber);
    }
}
