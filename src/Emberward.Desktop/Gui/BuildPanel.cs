namespace Emberward.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Emberward.Core.Entities.Tanks;
using Emberward.Desktop.Presentation;
using static Emberward.Desktop.Gui.HudDraw;

/// <summary>
/// The build slots of the BUILD STRUCTURES panel: one per machine, filling up toward its ore cost from the stock that
/// would pay (<see cref="Tank.QuoteBuild"/>: the base store in the own network, else the hold), which each slot names.
/// </summary>
public sealed class BuildPanel(HudLayout layout)
{
    // Build slots inside build_panel.png (offsets from the panel's top-left), 3 columns x 2 rows.
    private static readonly float[] SlotX = [25f, 117f, 209f];
    private static readonly float[] SlotY = [44f, 105f];
    private const float SlotW = 80f, SlotH = 56f;

    private Vector2 At(float x, float y) => layout.At(x, y);
    private float S(float v) => layout.S(v);

    public void Draw(ImDrawListPtr dl, Tank player, float panelX, float time)
    {
        // Each machine has a slot in the top row (MachinePresentation.BuildSlot). While a machine is in hand only its
        // own slot is lit: pressing the key again plants it.
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

        foreach (var machine in MachinePresentations.InSlotOrder)
            DrawSlot(dl, panelX, BuildSlotView.Of(machine, player.QuoteBuild(machine.Type), carried), time);
    }

    private void DrawSlot(ImDrawListPtr dl, float panelX, BuildSlotView slot, float time)
    {
        int col = slot.Machine.BuildSlot;
        var sMin = At(panelX + SlotX[col], SlotY[0]);
        var sMax = At(panelX + SlotX[col] + SlotW, SlotY[0] + SlotH);
        if (slot.State == BuildSlotState.Blocked)
            dl.AddRectFilled(sMin, sMax, 0xA8000000u, S(6f));
        else if (slot.State == BuildSlotState.Filling)
        {
            float fillTop = sMax.Y - S(SlotH) * slot.Progress;
            dl.AddRectFilled(sMin, new Vector2(sMax.X, fillTop), 0x80000000u, S(6f));
        }

        uint border = slot.State switch
        {
            BuildSlotState.Carrying => (int)(time * 3f) % 2 == 0 ? 0xFF40FFFFu : 0xFF20A0A0u,
            BuildSlotState.Ready => 0xFF40FF60u,
            _ => 0xFF707070u,
        };
        dl.AddRect(sMin, sMax, border, S(6f), ImDrawFlags.None, S(2.5f));

        DrawOutlinedText(dl, S(15f), new Vector2(sMin.X + S(4f), sMax.Y - S(18f)), slot.Label,
            slot.State == BuildSlotState.Blocked ? 0xFF909090u : 0xFFFFFFFFu, S(1f));
        if (slot.Caption is { } caption)
        {
            uint color = slot.CaptionKind switch
            {
                BuildSlotCaption.FromBase => 0xFFF0D080u,
                BuildSlotCaption.FromHold => 0xFFD8E860u,
                _ => 0xFFB0B0B0u,
            };
            DrawOutlinedText(dl, S(13f), new Vector2(sMin.X + S(4f), sMin.Y + S(3f)), caption, color, S(1f));
        }
    }
}
