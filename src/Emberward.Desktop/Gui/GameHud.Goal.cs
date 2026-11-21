namespace Emberward.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Emberward.Core;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Entities.Bases;
using static Emberward.Desktop.Gui.HudDraw;

/// <summary>
/// The base upgrades and the sector goal (DESIGN.md §4.8): the upgrade panel shown while docked and the banner when
/// the core is tapped. The goal lines in the status panel are <see cref="GoalLines"/>.
/// </summary>
public partial class GameHud
{
    private float _sectorCompleteAt = float.NaN;

    /// <summary>
    /// The top edge of the upgrade panel as last drawn, or +infinity while it is not shown: the toasts above it stop
    /// short of it rather than cover its title.
    /// </summary>
    private float _upgradePanelTop = float.PositiveInfinity;

    // The upgrade panel's margin from the view's left edge and the HUD below it, its share of the view's width and
    // the limits on that. The widest still ends left of a base whose far wall the tank is touching.
    private const float UpgradePanelMargin = 14f, UpgradePanelShare = 0.3f, UpgradePanelMinW = 300f, UpgradePanelMaxW = 400f;

    /// <summary>
    /// The upgrade list while the tank is docked at its own base, the base track (keys 1-5) above the tank track
    /// (6-7), on the left of the view just above the HUD. The base's entrances are at its top and bottom, so the panel
    /// keeps clear of the ground above and below the base as well as of the tank in the middle of the view.
    /// </summary>
    private void DrawUpgradePanel(Tank player)
    {
        _upgradePanelTop = float.PositiveInfinity;
        if (!_guidance.ShowUpgradePanel || !player.IsDockedAtOwnBase || player.Base is not { } home)
            return;

        var vp = ViewportRect;
        float width = Math.Clamp(vp.w * UpgradePanelShare, UpgradePanelMinW, UpgradePanelMaxW);
        var anchor = new Vector2(vp.x + UpgradePanelMargin, MathF.Floor(ViewBottom(vp) - UpgradePanelMargin));
        if (HudTheme.BeginWindow("##Upgrades", anchor, new Vector2(0f, 1f), width))
        {
            _upgradePanelTop = ImGui.GetWindowPos().Y;
            HudTheme.TitleRow($"UPGRADES    base ore store {home.Store.Ore}", 19f);
            var dl = ImGui.GetWindowDrawList();
            float rowW = ImGui.GetContentRegionAvail().X;
            UpgradeTrack? track = null;
            for (int i = 0; i < BaseUpgrades.Count; i++)
            {
                var upgrade = (BaseUpgrade)i;
                if (BaseUpgrades.TrackOf(upgrade) != track)
                {
                    // A small track label above its rows.
                    track = BaseUpgrades.TrackOf(upgrade);
                    var at = ImGui.GetCursorScreenPos();
                    HudTheme.TextAt(dl, 13f, at, track == UpgradeTrack.Base ? "BASE" : "TANK", HudTheme.TextDim);
                    ImGui.Dummy(new Vector2(rowW, 15f));
                }
                DrawUpgradeRow(dl, home, upgrade, i + 1, rowW);
            }
            if (player.UpgradeRefusedTicks > 0)
                ColoredText(HudTheme.Danger, player.LastUpgradeRefusal == UpgradeRefusal.Maxed
                    ? "That upgrade is already at its top level."
                    : "Not enough ore in the base store for that one.");
        }
        HudTheme.EndWindow();
    }

    /// <summary>
    /// One upgrade: key, name, level pips and price (right-aligned) on the first line, what the next level does on the
    /// second, in small type: six rows still fit above the HUD at 720 lines.
    /// </summary>
    private static void DrawUpgradeRow(ImDrawListPtr dl, TankBase home, BaseUpgrade upgrade, int key, float rowW)
    {
        var (name, effect) = BaseUpgrades.Describe(upgrade);
        int level = home.Upgrades.Level(upgrade);
        bool maxed = home.Upgrades.IsMaxed(upgrade);
        int cost = home.Upgrades.NextCost(upgrade);
        bool affordable = !maxed && home.Store.CanPay(cost);

        var pos = ImGui.GetCursorScreenPos();
        float capW = HudTheme.DrawKeyCap(dl, pos, key.ToString(System.Globalization.CultureInfo.InvariantCulture), 13f, affordable);
        float textX = pos.X + capW + 10f;
        HudTheme.TextAt(dl, 16f, new Vector2(textX, pos.Y + 1f), name, affordable ? HudTheme.Amber : HudTheme.TextDim);

        string price = maxed ? "maxed" : $"{cost} ore";
        float priceW = HudTheme.Measure(15f, price).X;
        float priceX = pos.X + rowW - priceW;
        HudTheme.TextAt(dl, 15f, new Vector2(priceX, pos.Y + 1f), price,
            maxed ? HudTheme.TextDim : affordable ? HudTheme.Good : HudTheme.Danger);
        // Level pips: lit segments for bought levels, just left of the price.
        float px = priceX - 14f - BaseUpgrades.MaxLevel * 16f;
        for (int l = 0; l < BaseUpgrades.MaxLevel; l++)
        {
            var pmin = new Vector2(px + l * 16f, pos.Y + 5f);
            dl.AddRectFilled(pmin, pmin + new Vector2(12f, 10f), l < level ? HudTheme.Amber : HudTheme.Track, 2f);
            dl.AddRect(pmin, pmin + new Vector2(12f, 10f), HudTheme.BronzeDark, 2f);
        }

        HudTheme.TextAt(dl, 13f, new Vector2(textX, pos.Y + 20f), effect, affordable ? HudTheme.Text : HudTheme.TextDim);
        ImGui.Dummy(new Vector2(rowW, 36f));
    }

    /// <summary>A banner across the view for a few seconds once the player's base taps the core.</summary>
    private void DrawSectorComplete(Tank player, World world)
    {
        if (world.CoreTapOf(player.Base) is not { HasTapped: true })
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
