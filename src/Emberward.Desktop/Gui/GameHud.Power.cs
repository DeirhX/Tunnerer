namespace Emberward.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Emberward.Core;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Sensing;

/// <summary>
/// The charge and flow labels over machines while the energy network runs (DESIGN.md §4.9): charge over capacity,
/// charge a second in and out, and a thin fill bar, in the fed or starved colour. <see cref="PowerLabels"/> (shared
/// with the browser) chooses the machines and the words; this only draws them, under every HUD panel.
/// </summary>
public partial class GameHud
{
    private readonly List<MachinePowerLabel> _powerLabels = new();
    private readonly List<Machine> _powerScratch = new();
    private ImDrawListPtr _viewportDrawList;

    /// <summary>The player's fog of war, or null without it: labels follow the machine sprites' sight rule.</summary>
    public VisionMap? Vision { get; set; }

    /// <summary>The labels are on (G). They show only while the match runs the energy network.</summary>
    public bool ShowPowerLabels { get; set; } = true;

    private const float LabelChargeSize = 13f;
    private const float LabelFlowSize = 12f;
    private const float LabelBarWidth = 40f;
    private const float LabelBarHeight = 3f;

    private void DrawPowerLabels(Tank player, World world)
    {
        if (!ShowPowerLabels || world.Energy == null)
            return;
        float zoom = PowerLabels.ZoomFade(ToUi(Camera.Scale));
        if (zoom <= 0f)
            return;
        PowerLabels.Collect(world, player, Vision, _powerLabels, _powerScratch);
        var play = PlayRect;
        var dl = _viewportDrawList;
        foreach (var (machine, label, opacity) in _powerLabels)
        {
            Vector2 at = CellToScreen(machine.Position.X + 0.5f, machine.Position.Y - PowerLabels.LiftCells);
            if (at.X < play.x || at.X > play.x + play.w || at.Y < play.y + 30f || at.Y > play.y + play.h)
                continue;
            float alpha = opacity * zoom;
            uint color = HudTheme.WithAlpha(label.Color, alpha);
            float barTop = at.Y - LabelBarHeight;
            float flowTop = barTop - 2f - LabelFlowSize;
            float chargeTop = flowTop - LabelChargeSize;
            Centred(dl, LabelChargeSize, at.X, chargeTop, label.Charge, HudTheme.WithAlpha(HudTheme.Text, alpha));
            Centred(dl, LabelFlowSize, at.X, flowTop, label.Flow, color);
            var min = new Vector2(MathF.Floor(at.X - LabelBarWidth * 0.5f), barTop);
            var max = min + new Vector2(LabelBarWidth, LabelBarHeight);
            dl.AddRectFilled(min, max, HudTheme.WithAlpha(HudTheme.Track, alpha));
            if (label.Fill > 0f)
                dl.AddRectFilled(min, new Vector2(min.X + LabelBarWidth * label.Fill, max.Y), color);
        }
    }

    private static void Centred(ImDrawListPtr dl, float size, float centerX, float y, string text, uint color) =>
        HudTheme.TextAt(dl, size, new Vector2(MathF.Floor(centerX - HudTheme.Measure(size, text).X * 0.5f), MathF.Floor(y)),
            text, color);
}
