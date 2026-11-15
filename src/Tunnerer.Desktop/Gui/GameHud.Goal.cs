namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;

/// <summary>
/// The sector goal and the base upgrades (DESIGN.md §4.8): the goal lines in the status overlay, the upgrade panel
/// shown while docked, and the banner when the core is tapped.
/// </summary>
public partial class GameHud
{
    private float _sectorCompleteAt = float.NaN;

    /// <summary>The goal lines at the top of the status overlay.</summary>
    private void DrawGoalLines(World world, TankBase tankBase)
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

    /// <summary>The upgrade list above the bottom panel while the tank is docked at its own base.</summary>
    private void DrawUpgradePanel(Tank player)
    {
        if (!_dockedAtOwnBase || player.IsDead || player.Base is not { } home)
            return;

        var vp = ViewportRect;
        float bottom = _hudOrigin.Y > vp.y ? _hudOrigin.Y : vp.y + vp.h;
        if (HudTheme.BeginWindow("##Upgrades", new Vector2(vp.x + vp.w * 0.5f, bottom - 14f), new Vector2(0.5f, 1f), 760f))
        {
            HudTheme.TitleRow($"BASE UPGRADES        dirt store {home.Materials.Dirt}", 20f);
            var dl = ImGui.GetWindowDrawList();
            for (int i = 0; i < BaseUpgrades.Count; i++)
            {
                var upgrade = (BaseUpgrade)i;
                var (name, effect) = BaseUpgrades.Describe(upgrade);
                int level = home.Upgrades.Level(upgrade);
                bool maxed = home.Upgrades.IsMaxed(upgrade);
                int cost = maxed ? 0 : home.Upgrades.NextCost(upgrade);
                bool affordable = !maxed && home.Materials.Dirt >= cost;
                uint color = affordable ? HudTheme.Text : HudTheme.TextDim;

                var pos = ImGui.GetCursorScreenPos();
                float capW = HudTheme.DrawKeyCap(dl, pos, (i + 1).ToString(), 15f, affordable);
                HudTheme.TextAt(dl, 17f, pos + new Vector2(capW + 10f, 3f), name, affordable ? HudTheme.Amber : HudTheme.TextDim);
                // Level pips: lit segments for bought levels.
                float px = pos.X + capW + 160f;
                for (int l = 0; l < BaseUpgrades.MaxLevel; l++)
                {
                    var pmin = new Vector2(px + l * 16f, pos.Y + 7f);
                    dl.AddRectFilled(pmin, pmin + new Vector2(12f, 10f), l < level ? HudTheme.Amber : HudTheme.Track, 2f);
                    dl.AddRect(pmin, pmin + new Vector2(12f, 10f), HudTheme.BronzeDark, 2f);
                }
                string price = maxed ? "maxed" : $"{cost} dirt";
                HudTheme.TextAt(dl, 16f, pos + new Vector2(capW + 220f, 3f), price,
                    maxed ? HudTheme.TextDim : affordable ? HudTheme.Good : HudTheme.Danger);
                HudTheme.TextAt(dl, 15f, pos + new Vector2(capW + 320f, 4f), effect, color);
                ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, 25f));
            }
            if (player.UpgradeRefusedTicks > 0)
                ColoredText(TextBad, player.LastUpgradeRefusal == UpgradeRefusal.Maxed
                    ? "That upgrade is already at its top level."
                    : "Not enough dirt in the base store for that one.");
        }
        HudTheme.EndWindow();
    }

    /// <summary>A banner across the view for a few seconds once the player's base taps the core.</summary>
    private void DrawSectorComplete(Tank player)
    {
        if (player.Base is not { HasTappedCore: true })
            return;
        if (float.IsNaN(_sectorCompleteAt))
            _sectorCompleteAt = _time;
        float age = _time - _sectorCompleteAt;
        if (age > 12f)
            return;
        var vp = ViewportRect;
        var dl = ImGui.GetForegroundDrawList();
        float alpha = Math.Clamp(MathF.Min(age * 2f, (12f - age) * 0.7f), 0f, 1f);
        float cx = vp.x + vp.w * 0.5f, cy = vp.y + vp.h * 0.32f;
        dl.AddRectFilled(new Vector2(vp.x, cy - 46f), new Vector2(vp.x + vp.w, cy + 52f), (uint)(alpha * 0xB0) << 24);
        uint a = (uint)(alpha * 255f) << 24;
        DrawCenteredText(dl, 44f, cx, cy - 36f, "SECTOR COMPLETE", a | 0x0060F0A0u);
        DrawCenteredText(dl, 20f, cx, cy + 16f, "The core is tapped and the sector is yours. Keep building as long as you like.", a | 0x00FFFFFFu);
    }
}
