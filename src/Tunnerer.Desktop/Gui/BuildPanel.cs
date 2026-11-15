namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Machines;
using static Tunnerer.Desktop.Gui.HudDraw;

/// <summary>The build slots of the BUILD STRUCTURES panel: one per machine, filling up as dirt is dug.</summary>
public sealed class BuildPanel(HudLayout layout)
{
    // Build slots inside build_panel.png (offsets from the panel's top-left), 3 columns x 2 rows.
    private static readonly float[] SlotX = [25f, 117f, 209f];
    private static readonly float[] SlotY = [44f, 105f];
    private const float SlotW = 80f, SlotH = 56f;

    private Vector2 At(float x, float y) => layout.At(x, y);
    private float S(float v) => layout.S(v);

    public void Draw(ImDrawListPtr dl, Tank player, int dirt, float panelX, float time)
    {
        // Slot 0 builds a relay (E), slot 1 a harvester (R), slot 2 a pylon (T). While a machine is in hand only its own slot is lit:
        // pressing the key again plants it.
        var carried = player.CarriedMachineType;

        for (int row = 0; row < SlotY.Length; row++)
        {
            for (int col = 0; col < SlotX.Length; col++)
            {
                if (row == 0)
                    continue;
                float x = panelX + SlotX[col], y = SlotY[row];
                dl.AddRectFilled(At(x, y), At(x + SlotW, y + SlotH), 0xA8000000u, S(6f));
            }
        }

        DrawSlot(dl, panelX, col: 0, MachineType.Relay, "RELAY", "E", dirt, carried, time);
        DrawSlot(dl, panelX, col: 1, MachineType.Harvester, "HARVEST", "R", dirt, carried, time);
        DrawSlot(dl, panelX, col: 2, MachineType.Pylon, "PYLON", "T", dirt, carried, time);
    }

    private void DrawSlot(ImDrawListPtr dl, float panelX, int col, MachineType type,
        string name, string key, int dirt, MachineType? carried, float time)
    {
        int cost = MachineBehaviors.Get(type).BuildCost.Dirt;
        float progress = Math.Clamp(dirt / (float)cost, 0f, 1f);
        bool ready = progress >= 1f;
        bool carryingThis = carried == type;
        bool blocked = carried != null && !carryingThis; // the other machine is in hand

        var sMin = At(panelX + SlotX[col], SlotY[0]);
        var sMax = At(panelX + SlotX[col] + SlotW, SlotY[0] + SlotH);
        if (blocked)
            dl.AddRectFilled(sMin, sMax, 0xA8000000u, S(6f));
        else if (!ready && !carryingThis)
        {
            float fillTop = sMax.Y - S(SlotH) * progress;
            dl.AddRectFilled(sMin, new Vector2(sMax.X, fillTop), 0x80000000u, S(6f));
        }

        uint border = carryingThis
            ? ((int)(time * 3f) % 2 == 0 ? 0xFF40FFFFu : 0xFF20A0A0u)
            : ready && !blocked ? 0xFF40FF60u : 0xFF707070u;
        dl.AddRect(sMin, sMax, border, S(6f), ImDrawFlags.None, S(2.5f));

        string label = carryingThis ? $"PLANT [{key}]" : ready ? $"{name} [{key}]" : $"{dirt}/{cost}";
        DrawOutlinedText(dl, S(15f), new Vector2(sMin.X + S(4f), sMax.Y - S(18f)), label, blocked ? 0xFF909090u : 0xFFFFFFFFu, S(1f));
        // The key is always visible, even before the slot is affordable.
        if (!ready && !carryingThis)
            DrawOutlinedText(dl, S(13f), new Vector2(sMin.X + S(4f), sMin.Y + S(3f)), $"[{key}] {name}", 0xFFB0B0B0u, S(1f));
    }
}
