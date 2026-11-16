namespace Tunnerer.Desktop.Gui;

using ImGuiNET;
using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using static Tunnerer.Desktop.Gui.HudDraw;

/// <summary>The sector goal lines at the top of the base status panel (DESIGN.md §4.8).</summary>
public static class GoalLines
{
    public static void Draw(World world, TankBase tankBase)
    {
        if (world.CorePocket == null)
            return;
        if (tankBase.HasTappedCore)
        {
            ColoredText(TextGood, "SECTOR COMPLETE - the core is tapped");
            return;
        }
        double rate = tankBase.CoreTapPerTick * Tweaks.Perf.TargetFps;
        bool meeting = rate >= Tweaks.Goal.GoalChargePerSecond;
        ColoredText(TextInfo, "GOAL  tap the hot core at the centre");
        ColoredText(meeting ? TextGood : TextDim,
            $"Core tap {rate / 1000.0:0.0}k / {Tweaks.Goal.GoalChargePerSecond / 1000.0:0.0}k charge/s");
        float hold = (float)(tankBase.CoreHoldSeconds / Tweaks.Goal.GoalHoldSeconds);
        HudTheme.BarRow(Math.Clamp(hold, 0f, 1f), meeting ? HudTheme.Good : HudTheme.Amber,
            $"held {tankBase.CoreHoldSeconds:0} / {Tweaks.Goal.GoalHoldSeconds:0} s", 20f);
        ImGui.Separator();
    }
}
