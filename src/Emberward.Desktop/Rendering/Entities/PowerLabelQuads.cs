namespace Emberward.Desktop.Rendering.Entities;

using Emberward.Core;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Sensing;
using Emberward.Core.Types;
using Emberward.Desktop.Gui;

/// <summary>Emits the in-world charge and flow labels after the other entities, beneath the HUD.</summary>
internal sealed class PowerLabelQuads
{
    private const float BarWidth = 40f;
    private const float BarHeight = 3f;
    private const float LineGap = 2f;
    private const float DotSize = 1.5f;
    private const int GlyphWidth = 5;
    private const int GlyphHeight = 7;

    private readonly List<MachinePowerLabel> _labels = new();
    private readonly List<Machine> _near = new();
    private World? _world;
    private Tank? _player;
    private VisionMap? _vision;
    private bool _labelsCurrent;

    public void CaptureStep(World world, int localSlot)
    {
        _world = world;
        _player = localSlot >= 0 && localSlot < world.TankList.Count ? world.TankList.Tanks[localSlot] : null;
        _labelsCurrent = false;
    }

    public void Emit(EntityQuads quads, VisionMap? vision, float cellSize)
    {
        float zoom = PowerLabels.ZoomFade(cellSize);
        if (zoom <= 0f || _world?.Energy == null || _player == null)
            return;
        if (!_labelsCurrent || _vision != vision)
        {
            PowerLabels.Collect(_world, _player, vision, _labels, _near);
            _vision = vision;
            _labelsCurrent = true;
        }

        // The view can be rendered at a different density from the UI. Keep each dot at least one render pixel wide.
        float scale = quads.View.PixelScale;
        float pixelsPerUiUnit = scale / cellSize;
        float dot = MathF.Max(1f, DotSize * pixelsPerUiUnit) / scale;
        float barHeight = BarHeight / cellSize;
        float gap = LineGap / cellSize;
        float halfBar = BarWidth * 0.5f / cellSize;
        float flowTop = -barHeight - gap - GlyphHeight * dot;
        float chargeTop = flowTop - gap - GlyphHeight * dot;
        foreach (var (machine, label, opacity) in _labels)
        {
            float x = quads.ScreenX(machine.Position.X);
            float y = quads.ScreenY(machine.Position.Y - PowerLabels.LiftCells - 0.5f);
            if (x < 0f || x > quads.View.ViewSize.X || y < -chargeTop * scale || y > quads.View.ViewSize.Y)
                continue;

            float alpha = opacity * zoom;
            AddPlate(quads, x, y, -halfBar, halfBar, barHeight, HudPalette.Track, alpha);
            if (label.Fill > 0f)
                AddPlate(quads, x, y, -halfBar, -halfBar + 2f * halfBar * label.Fill, barHeight, label.Color, alpha);
            AddLine(quads, x, y, chargeTop, label.Charge, dot, HudPalette.Text, alpha);
            AddLine(quads, x, y, flowTop, label.Flow, dot, label.Color, alpha);
        }
    }

    private static void AddPlate(EntityQuads quads, float x, float y, float left, float right, float height, uint color, float alpha) =>
        quads.Add(x, y, 1f, 0f, left, right, -height, 0f, Rgb(color), EntityShape.KindPowerLabel, 0f, 0f, 0f,
            alpha * (color >> 24) / 255f);

    private static void AddLine(EntityQuads quads, float x, float y, float top, string text, float dot, uint color, float alpha)
    {
        float advance = (GlyphWidth + 1) * dot;
        float left = -(text.Length * advance - dot) * 0.5f;
        float scale = quads.View.PixelScale;
        var rgb = Rgb(color);
        foreach (char character in text)
        {
            ulong glyph = Glyph(character);
            if (glyph != 0UL)
            {
                // Both pieces fit exactly in floats. The quad includes one dot of room for the shadow.
                quads.Add(x + left * scale, y + top * scale, 1f, 0f, 0f, advance, 0f, (GlyphHeight + 1) * dot,
                    rgb, EntityShape.KindPowerLabel, glyph & 0xFFFFFUL, glyph >> 20, 1f / dot, alpha);
            }
            left += advance;
        }
    }

    private static Color Rgb(uint abgr) => new((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));

    // The 5x7 bitmap alphabet used by PowerLabels. Each group of five bits is one row, top to bottom.
    private static ulong Glyph(char character) => character switch
    {
        '0' => 0b01110_10001_10011_10101_11001_10001_01110UL,
        '1' => 0b00100_01100_00100_00100_00100_00100_01110UL,
        '2' => 0b01110_10001_00001_00010_00100_01000_11111UL,
        '3' => 0b11110_00001_00001_01110_00001_00001_11110UL,
        '4' => 0b00010_00110_01010_10010_11111_00010_00010UL,
        '5' => 0b11111_10000_10000_11110_00001_00001_11110UL,
        '6' => 0b01110_10000_10000_11110_10001_10001_01110UL,
        '7' => 0b11111_00001_00010_00100_01000_01000_01000UL,
        '8' => 0b01110_10001_10001_01110_10001_10001_01110UL,
        '9' => 0b01110_10001_10001_01111_00001_00001_01110UL,
        '.' => 0b00000_00000_00000_00000_00000_01100_01100UL,
        '/' => 0b00001_00001_00010_00100_01000_10000_10000UL,
        '+' => 0b00000_00100_00100_11111_00100_00100_00000UL,
        '-' => 0b00000_00000_00000_11111_00000_00000_00000UL,
        's' => 0b00000_00000_01111_10000_01110_00001_11110UL,
        'k' => 0b10000_10000_10010_10100_11000_10100_10010UL,
        'M' => 0b10001_11011_10101_10101_10001_10001_10001UL,
        'g' => 0b00000_01111_10001_10001_01111_00001_01110UL,
        'e' => 0b00000_00000_01110_10001_11111_10000_01111UL,
        'n' => 0b00000_00000_11110_10001_10001_10001_10001UL,
        ' ' => 0UL,
        _ => 0b01110_10001_00010_00100_00100_00000_00100UL,
    };
}
