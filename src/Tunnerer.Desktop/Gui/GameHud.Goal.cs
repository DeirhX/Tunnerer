namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core.Entities;
using static Tunnerer.Desktop.Gui.HudDraw;

/// <summary>
/// The base upgrades and the sector goal (DESIGN.md §4.8): the upgrade panel shown while docked and the banner when
/// the core is tapped. The goal lines in the status panel are <see cref="GoalLines"/>.
/// </summary>
public partial class GameHud
{
    private float _sectorCompleteAt = float.NaN;

    // The upgrade panel's margin from the HUD parts and the bottom edge, and its widest. The HUD always leaves a gap of
    // at least HudLayout.MinCenterGap, so the panel is at least that less the margins wide.
    private const float UpgradePanelMargin = 12f, UpgradePanelMaxW = 460f;

    /// <summary>
    /// The upgrade list while the tank is docked at its own base, at the bottom of the screen in the gap between the
    /// HUD's two parts, so it stays clear of the tank in the middle of the view.
    /// </summary>
    private void DrawUpgradePanel(Tank player)
    {
        if (!player.IsDockedAtOwnBase || player.Base is not { } home)
            return;

        var (gapMinX, gapMaxX) = _strip.Gap;
        float width = MathF.Min(gapMaxX - gapMinX - 2f * UpgradePanelMargin, UpgradePanelMaxW);
        var anchor = new Vector2(MathF.Floor((gapMinX + gapMaxX) * 0.5f), ImGui.GetIO().DisplaySize.Y - UpgradePanelMargin);
        if (HudTheme.BeginWindow("##Upgrades", anchor, new Vector2(0.5f, 1f), width))
        {
            HudTheme.TitleRow($"BASE UPGRADES    dirt store {home.Materials.Dirt}", 19f);
            var dl = ImGui.GetWindowDrawList();
            float rowW = ImGui.GetContentRegionAvail().X;
            for (int i = 0; i < BaseUpgrades.Count; i++)
            {
                var upgrade = (BaseUpgrade)i;
                var (name, effect) = BaseUpgrades.Describe(upgrade);
                int level = home.Upgrades.Level(upgrade);
                bool maxed = home.Upgrades.IsMaxed(upgrade);
                int cost = maxed ? 0 : home.Upgrades.NextCost(upgrade);
                bool affordable = !maxed && home.Materials.Dirt >= cost;

                // First line: key, name, level pips and price (right-aligned). Second line: what it does.
                var pos = ImGui.GetCursorScreenPos();
                float capW = HudTheme.DrawKeyCap(dl, pos, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), 15f, affordable);
                float textX = pos.X + capW + 10f;
                HudTheme.TextAt(dl, 17f, new Vector2(textX, pos.Y + 3f), name, affordable ? HudTheme.Amber : HudTheme.TextDim);

                string price = maxed ? "maxed" : $"{cost} dirt";
                float priceW = HudTheme.Measure(16f, price).X;
                float priceX = pos.X + rowW - priceW;
                HudTheme.TextAt(dl, 16f, new Vector2(priceX, pos.Y + 3f), price,
                    maxed ? HudTheme.TextDim : affordable ? HudTheme.Good : HudTheme.Danger);
                // Level pips: lit segments for bought levels, just left of the price.
                float px = priceX - 14f - BaseUpgrades.MaxLevel * 16f;
                for (int l = 0; l < BaseUpgrades.MaxLevel; l++)
                {
                    var pmin = new Vector2(px + l * 16f, pos.Y + 7f);
                    dl.AddRectFilled(pmin, pmin + new Vector2(12f, 10f), l < level ? HudTheme.Amber : HudTheme.Track, 2f);
                    dl.AddRect(pmin, pmin + new Vector2(12f, 10f), HudTheme.BronzeDark, 2f);
                }

                HudTheme.TextAt(dl, 15f, new Vector2(textX, pos.Y + 25f), effect, affordable ? HudTheme.Text : HudTheme.TextDim);
                ImGui.Dummy(new Vector2(rowW, 46f));
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
