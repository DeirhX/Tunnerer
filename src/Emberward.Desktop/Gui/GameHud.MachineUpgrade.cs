namespace Emberward.Desktop.Gui;

using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Emberward.Core;
using Emberward.Core.Entities.Tanks;
using static Emberward.Desktop.Gui.HudDraw;

/// <summary>
/// Per-machine upgrades (DESIGN.md §4.9): the machine next to the tank is ringed and shows its level pips, with the
/// key that opens its panel; the panel beside it lists the paths, their prices and effects. What it says comes from
/// <see cref="MachineUpgradeView"/>, which the browser shares.
/// </summary>
public partial class GameHud
{
    private const float MachinePanelWidth = 320f, MachinePanelGap = 14f;

    /// <summary>The key that opens and closes the machine upgrade panel, as written on screen.</summary>
    public const string MachineUpgradeKey = "F";

    /// <summary>Whether the machine upgrade panel is open (F); the game reads it to send the upgrade keys to the machine.</summary>
    public MachineUpgradePanel MachineUpgrades { get; } = new();

    private void DrawMachineUpgrade(Tank player, World world)
    {
        var view = MachineUpgrades.Update(world, player);
        if (view is null)
            return;
        var machine = view.Machine.Position;
        var center = CellToScreen(machine.X + 0.5f, machine.Y + 0.5f);
        if (!IsInView(center))
            return;

        var dl = ImGui.GetForegroundDrawList();
        float ring = ToUi(MachineUpgradeView.RingCells * Camera.Scale);
        if (view.HighlightColor >> 24 != 0)
            dl.AddCircle(center, ring, view.HighlightColor, 32, MachineUpgrades.IsOpen ? 3f : 2f);
        DrawMachinePips(dl, view, CellToScreen(machine.X + 0.5f, machine.Y + 0.5f + MachineUpgradeView.PipDropCells));

        if (!MachineUpgrades.IsOpen)
        {
            // The key that opens the panel, beside the ring.
            HudTheme.DrawKeyCap(dl, center + new Vector2(ring + 4f, -9f), MachineUpgradeKey, 13f,
                view.Highlight == MachineUpgradeHighlight.Affordable);
            return;
        }

        var anchor = new Vector2(center.X + ring + MachinePanelGap, center.Y);
        var vp = ViewportRect;
        // Flip to the left of the machine when the panel would run past the view's right edge.
        bool left = anchor.X + MachinePanelWidth > vp.x + vp.w;
        if (left)
            anchor.X = center.X - ring - MachinePanelGap;
        if (HudTheme.BeginWindow("##MachineUpgrade", anchor, new Vector2(left ? 1f : 0f, 0.5f), MachinePanelWidth))
        {
            HudTheme.TitleRow(string.Create(CultureInfo.InvariantCulture,
                $"{view.Title}    level {view.Level}/{view.MaxLevel}"), 18f);
            var rowDl = ImGui.GetWindowDrawList();
            float rowW = ImGui.GetContentRegionAvail().X;
            foreach (var choice in view.Choices)
                DrawMachineChoice(rowDl, choice, rowW);
            HudTheme.TextAt(rowDl, 13f, ImGui.GetCursorScreenPos(), string.Create(CultureInfo.InvariantCulture,
                $"base ore store {view.StoreOre}    {MachineUpgradeKey} closes"), HudTheme.TextDim);
            ImGui.Dummy(new Vector2(rowW, 16f));
            if (view.Refusal is { } refusal)
                ColoredText(HudTheme.Danger, refusal);
        }
        HudTheme.EndWindow();
    }

    private static void DrawMachinePips(ImDrawListPtr dl, MachineUpgradeView view, Vector2 at)
    {
        const float pip = 10f, step = 13f;
        float x = at.X - (view.MaxLevel * step - (step - pip)) * 0.5f;
        for (int l = 0; l < view.MaxLevel; l++)
        {
            var min = new Vector2(x + l * step, at.Y - pip * 0.5f);
            dl.AddRectFilled(min, min + new Vector2(pip, pip * 0.8f), l < view.Level ? HudTheme.Amber : HudTheme.Track, 2f);
            dl.AddRect(min, min + new Vector2(pip, pip * 0.8f), HudTheme.BronzeDark, 2f);
        }
    }

    /// <summary>One path: key, name and price on the first line, what a level does on the second.</summary>
    private static void DrawMachineChoice(ImDrawListPtr dl, MachineUpgradeChoice choice, float rowW)
    {
        var pos = ImGui.GetCursorScreenPos();
        float capW = HudTheme.DrawKeyCap(dl, pos, choice.Key.ToString(CultureInfo.InvariantCulture), 13f, choice.CanBuy);
        float textX = pos.X + capW + 10f;
        HudTheme.TextAt(dl, 16f, new Vector2(textX, pos.Y + 1f), choice.Name, choice.NameColor);
        float priceW = HudTheme.Measure(15f, choice.Price).X;
        HudTheme.TextAt(dl, 15f, new Vector2(pos.X + rowW - priceW, pos.Y + 1f), choice.Price, choice.PriceColor);
        HudTheme.TextAt(dl, 13f, new Vector2(textX, pos.Y + 20f), choice.Effect, choice.CanBuy ? HudTheme.Text : HudTheme.TextDim);
        ImGui.Dummy(new Vector2(rowW, 36f));
    }
}
