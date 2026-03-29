namespace Tunnerer.Core.Terrain;

using Tunnerer.Core.Types;

/// <summary>
/// Builds a synthetic terrain grid with hand-crafted geometric patterns for visual
/// testing of the GPU rendering pipeline (SDF smoothing, material shaders, corner
/// rounding, boundary blending, decals, etc.).
/// </summary>
public static class TerrainTestPatternGenerator
{
    public static readonly Size DefaultSize = new(320, 200);

    public static TerrainGrid Generate(Size? sizeOverride = null)
    {
        var size = sizeOverride ?? DefaultSize;
        var grid = new TerrainGrid(size);
        grid.Fill(TerrainPixel.Blank);

        int y = 2;
        y = DrawMaterialStrips(grid, y);
        y += 3;
        y = DrawBoundaryShapes(grid, y);
        y += 3;
        y = DrawMaterialTransitions(grid, y);
        y += 3;
        y = DrawDecalShowcase(grid, y);
        y += 3;
        y = DrawCornerGallery(grid, y);
        y += 3;
        DrawStressPatterns(grid, y);

        return grid;
    }

    // ---- Material strips: horizontal bands of each material type ----

    static int DrawMaterialStrips(TerrainGrid grid, int startY)
    {
        const int stripH = 8;
        const int gutter = 3;
        int margin = 4;
        int w = grid.Width - margin * 2;
        int y = startY;

        FillRect(grid, margin, y, w, stripH, TerrainPixel.DirtHigh);
        y += stripH + gutter;

        FillRect(grid, margin, y, w, stripH, TerrainPixel.Rock);
        y += stripH + gutter;

        FillRect(grid, margin, y, w, stripH, TerrainPixel.GoldMedium);
        y += stripH + gutter;

        FillRect(grid, margin, y, w, stripH, TerrainPixel.ConcreteHigh);
        y += stripH + gutter;

        // Base strip: use BaseMin for interior, BaseBarrier for edges, BaseCore center
        int baseW = w;
        FillRect(grid, margin, y, baseW, stripH, TerrainPixel.BaseMin);
        FillRect(grid, margin, y, baseW, 1, TerrainPixel.BaseBarrier);
        FillRect(grid, margin, y + stripH - 1, baseW, 1, TerrainPixel.BaseBarrier);
        if (stripH > 4)
            FillRect(grid, margin + baseW / 2 - 2, y + stripH / 2 - 1, 4, 2, TerrainPixel.BaseCore);
        y += stripH;

        return y;
    }

    // ---- Cave/solid boundary shapes: circles, diagonals, corners ----

    static int DrawBoundaryShapes(TerrainGrid grid, int startY)
    {
        int y = startY;
        int x = 6;

        // Circles of increasing radii in dirt
        int[] radii = [2, 4, 7, 10, 15];
        foreach (int r in radii)
        {
            int cx = x + r;
            int cy = y + 16;
            FillCircle(grid, cx, cy, r, TerrainPixel.DirtHigh);
            x += r * 2 + 5;
        }

        // Circles of increasing radii in stone
        x = 6;
        foreach (int r in radii)
        {
            int cx = x + r;
            int cy = y + 50;
            FillCircle(grid, cx, cy, r, TerrainPixel.Rock);
            x += r * 2 + 5;
        }

        // Diagonal lines at various angles in stone (right side of the grid)
        int diagX = grid.Width / 2 + 10;
        int diagY = y + 4;
        double[] angles = [15, 30, 45, 60, 75];
        foreach (double angleDeg in angles)
        {
            DrawDiagonalStrip(grid, diagX, diagY, angleDeg, 30, 3, TerrainPixel.Rock);
            diagX += 12;
        }

        // Concave and convex corner shapes (stone)
        int shapeX = grid.Width / 2 + 10;
        int shapeY = y + 30;

        // L-shape (convex outer corner)
        FillRect(grid, shapeX, shapeY, 10, 5, TerrainPixel.Rock);
        FillRect(grid, shapeX, shapeY + 5, 5, 10, TerrainPixel.Rock);

        // Notched block (concave inner corner)
        shapeX += 18;
        FillRect(grid, shapeX, shapeY, 12, 12, TerrainPixel.DirtHigh);
        FillRect(grid, shapeX + 6, shapeY, 6, 6, TerrainPixel.Blank);

        // U-shape (two concave corners)
        shapeX += 18;
        FillRect(grid, shapeX, shapeY, 4, 12, TerrainPixel.Rock);
        FillRect(grid, shapeX + 8, shapeY, 4, 12, TerrainPixel.Rock);
        FillRect(grid, shapeX, shapeY + 8, 12, 4, TerrainPixel.Rock);

        return y + 62;
    }

    // ---- Material-to-material transitions ----

    static int DrawMaterialTransitions(TerrainGrid grid, int startY)
    {
        int y = startY;
        int blockW = 20;
        int blockH = 14;
        int gutter = 4;
        int x = 6;

        // Vertical split: dirt | stone
        FillRect(grid, x, y, blockW / 2, blockH, TerrainPixel.DirtHigh);
        FillRect(grid, x + blockW / 2, y, blockW / 2, blockH, TerrainPixel.Rock);
        x += blockW + gutter;

        // Vertical split: dirt | energy
        FillRect(grid, x, y, blockW / 2, blockH, TerrainPixel.DirtHigh);
        FillRect(grid, x + blockW / 2, y, blockW / 2, blockH, TerrainPixel.GoldMedium);
        x += blockW + gutter;

        // Vertical split: stone | energy
        FillRect(grid, x, y, blockW / 2, blockH, TerrainPixel.Rock);
        FillRect(grid, x + blockW / 2, y, blockW / 2, blockH, TerrainPixel.GoldMedium);
        x += blockW + gutter;

        // Vertical split: concrete | dirt
        FillRect(grid, x, y, blockW / 2, blockH, TerrainPixel.ConcreteHigh);
        FillRect(grid, x + blockW / 2, y, blockW / 2, blockH, TerrainPixel.DirtHigh);
        x += blockW + gutter;

        // Diagonal splits (second row)
        x = 6;
        int y2 = y + blockH + gutter;

        // Diagonal dirt/stone
        DrawDiagonalSplit(grid, x, y2, blockW, blockH, TerrainPixel.DirtHigh, TerrainPixel.Rock);
        x += blockW + gutter;

        // Diagonal stone/energy
        DrawDiagonalSplit(grid, x, y2, blockW, blockH, TerrainPixel.Rock, TerrainPixel.GoldMedium);
        x += blockW + gutter;

        // Diagonal dirt/energy
        DrawDiagonalSplit(grid, x, y2, blockW, blockH, TerrainPixel.DirtHigh, TerrainPixel.GoldMedium);
        x += blockW + gutter;

        // Horizontal split: dirt over stone
        FillRect(grid, x, y2, blockW, blockH / 2, TerrainPixel.DirtHigh);
        FillRect(grid, x, y2 + blockH / 2, blockW, blockH / 2, TerrainPixel.Rock);
        x += blockW + gutter;

        // Checkerboard material transition: dirt/stone 4x4 cells
        int cbX = x;
        for (int cy = 0; cy < blockH; cy++)
            for (int cx = 0; cx < blockW; cx++)
            {
                var mat = ((cx / 4) + (cy / 4)) % 2 == 0 ? TerrainPixel.DirtHigh : TerrainPixel.Rock;
                SetSafe(grid, cbX + cx, y2 + cy, mat);
            }

        return y2 + blockH;
    }

    // ---- Decal showcase ----

    static int DrawDecalShowcase(TerrainGrid grid, int startY)
    {
        int y = startY;
        int x = 6;

        // Crater on dirt: circle of DecalHigh surrounded by DecalLow ring
        FillRect(grid, x, y, 30, 20, TerrainPixel.DirtHigh);
        FillCircle(grid, x + 15, y + 10, 6, TerrainPixel.DecalLow);
        FillCircle(grid, x + 15, y + 10, 3, TerrainPixel.DecalHigh);
        // Set scorch intensity for decal pixels
        SetScorchInCircle(grid, x + 15, y + 10, 6, 40);
        SetScorchInCircle(grid, x + 15, y + 10, 3, 64);
        x += 36;

        // Crater on stone
        FillRect(grid, x, y, 30, 20, TerrainPixel.Rock);
        FillCircle(grid, x + 15, y + 10, 6, TerrainPixel.DecalLow);
        FillCircle(grid, x + 15, y + 10, 3, TerrainPixel.DecalHigh);
        SetScorchInCircle(grid, x + 15, y + 10, 6, 40);
        SetScorchInCircle(grid, x + 15, y + 10, 3, 64);
        x += 36;

        // Scattered small decal hits on dirt
        FillRect(grid, x, y, 30, 20, TerrainPixel.DirtHigh);
        int[] hitOffsetsX = [3, 8, 14, 20, 25, 6, 18, 12];
        int[] hitOffsetsY = [3, 7, 4, 9, 5, 14, 15, 11];
        for (int i = 0; i < hitOffsetsX.Length; i++)
        {
            int hx = x + hitOffsetsX[i];
            int hy = y + hitOffsetsY[i];
            SetSafe(grid, hx, hy, TerrainPixel.DecalHigh);
            SetScorchSafe(grid, hx, hy, 64);
            // Small ring of DecalLow around each hit
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (dx != 0 || dy != 0)
                    {
                        if (grid.IsInside(new Position(hx + dx, hy + dy)) &&
                            grid.GetPixelRaw(new Position(hx + dx, hy + dy)) == TerrainPixel.DirtHigh)
                        {
                            SetSafe(grid, hx + dx, hy + dy, TerrainPixel.DecalLow);
                            SetScorchSafe(grid, hx + dx, hy + dy, 30);
                        }
                    }
        }
        x += 36;

        // DecalHigh/DecalLow strip next to cave boundary (test how decals render at edges)
        FillRect(grid, x, y, 20, 20, TerrainPixel.DirtHigh);
        FillRect(grid, x, y + 8, 20, 4, TerrainPixel.DecalHigh);
        for (int px = x; px < x + 20; px++)
            for (int py = y + 8; py < y + 12; py++)
                SetScorchSafe(grid, px, py, 64);
        // Carve cave into the bottom half to see decal-at-edge behavior
        FillRect(grid, x + 5, y + 12, 10, 8, TerrainPixel.Blank);
        x += 26;

        // Gradient of scorch intensities on dirt (low to max)
        FillRect(grid, x, y, 40, 10, TerrainPixel.DirtHigh);
        for (int col = 0; col < 40; col++)
        {
            byte intensity = (byte)Math.Clamp(col * 64 / 40, 0, 64);
            for (int row = 0; row < 10; row++)
            {
                if (intensity > 10)
                {
                    SetSafe(grid, x + col, y + row, TerrainPixel.DecalHigh);
                    SetScorchSafe(grid, x + col, y + row, intensity);
                }
            }
        }

        return y + 20;
    }

    // ---- Corner gallery ----

    static int DrawCornerGallery(TerrainGrid grid, int startY)
    {
        int y = startY;
        int x = 6;
        const int cellSize = 14;
        const int gap = 4;

        // 90-degree sharp corners (all 4 orientations)
        for (int orient = 0; orient < 4; orient++)
        {
            int ox = x + orient * (cellSize + gap);
            DrawLCorner(grid, ox, y, cellSize, orient, TerrainPixel.Rock);
        }
        x += 4 * (cellSize + gap) + gap;

        // Single-step staircases (2 orientations)
        DrawStaircase(grid, x, y, 12, 12, true, TerrainPixel.DirtHigh);
        x += 16;
        DrawStaircase(grid, x, y, 12, 12, false, TerrainPixel.DirtHigh);
        x += 16;

        // Quarter-circle arcs at different radii
        int[] arcRadii = [3, 6, 10];
        foreach (int r in arcRadii)
        {
            FillQuarterCircle(grid, x, y + cellSize, r, TerrainPixel.Rock);
            x += r + gap;
        }

        // Thin diagonal bridges (1px, 2px, 3px wide)
        x = 6;
        int y2 = y + cellSize + gap;
        for (int thickness = 1; thickness <= 3; thickness++)
        {
            DrawDiagonalStrip(grid, x, y2, 45.0, 16, thickness, TerrainPixel.DirtHigh);
            x += 14;
        }

        // Thin horizontal and vertical walls
        FillRect(grid, x, y2, 20, 1, TerrainPixel.Rock);
        FillRect(grid, x, y2 + 3, 20, 2, TerrainPixel.Rock);
        FillRect(grid, x, y2 + 7, 20, 3, TerrainPixel.Rock);
        x += 24;

        FillRect(grid, x, y2, 1, 14, TerrainPixel.Rock);
        FillRect(grid, x + 3, y2, 2, 14, TerrainPixel.Rock);
        FillRect(grid, x + 7, y2, 3, 14, TerrainPixel.Rock);

        return y2 + 16;
    }

    // ---- Stress patterns ----

    static void DrawStressPatterns(TerrainGrid grid, int startY)
    {
        int y = startY;
        if (y + 20 > grid.Height) return;

        int x = 6;

        // Isolated single pixels (various materials)
        TerrainPixel[] singles = [TerrainPixel.DirtHigh, TerrainPixel.Rock, TerrainPixel.GoldMedium, TerrainPixel.ConcreteHigh];
        foreach (var mat in singles)
        {
            SetSafe(grid, x, y + 2, mat);
            x += 4;
        }
        x += 4;

        // 2x2 blocks
        foreach (var mat in singles)
        {
            FillRect(grid, x, y + 1, 2, 2, mat);
            x += 5;
        }
        x += 4;

        // Checkerboard (dirt/cave)
        for (int cy = 0; cy < 10; cy++)
            for (int cx = 0; cx < 10; cx++)
                if ((cx + cy) % 2 == 0)
                    SetSafe(grid, x + cx, y + cy, TerrainPixel.DirtHigh);
        x += 14;

        // Thin cave tunnel through dirt (1px, 2px, 3px wide)
        FillRect(grid, x, y, 20, 14, TerrainPixel.DirtHigh);
        FillRect(grid, x, y + 3, 20, 1, TerrainPixel.Blank);  // 1px tunnel
        FillRect(grid, x, y + 6, 20, 2, TerrainPixel.Blank);  // 2px tunnel
        FillRect(grid, x, y + 10, 20, 3, TerrainPixel.Blank); // 3px tunnel
        x += 24;

        // Small cave openings in a stone wall
        FillRect(grid, x, y, 30, 14, TerrainPixel.Rock);
        // 1px opening
        SetSafe(grid, x + 3, y + 7, TerrainPixel.Blank);
        // 2px opening
        FillRect(grid, x + 8, y + 7, 2, 1, TerrainPixel.Blank);
        // 3px opening
        FillRect(grid, x + 13, y + 6, 3, 2, TerrainPixel.Blank);
        // 5px circle opening
        FillCircle(grid, x + 23, y + 7, 2, TerrainPixel.Blank);

        // Dithered edge (alternating solid/cave)
        x = 6;
        int y3 = y + 16;
        if (y3 + 10 > grid.Height) return;
        FillRect(grid, x, y3, 30, 5, TerrainPixel.DirtHigh);
        for (int col = 0; col < 30; col++)
            if (col % 2 == 0)
                SetSafe(grid, x + col, y3 + 5, TerrainPixel.DirtHigh);
    }

    // ---- Drawing primitives ----

    static void FillRect(TerrainGrid grid, int left, int top, int w, int h, TerrainPixel value)
    {
        int maxX = Math.Min(left + w, grid.Width);
        int maxY = Math.Min(top + h, grid.Height);
        for (int y = Math.Max(0, top); y < maxY; y++)
            for (int x = Math.Max(0, left); x < maxX; x++)
                grid.SetPixelRaw(new Position(x, y), value);
    }

    static void FillCircle(TerrainGrid grid, int cx, int cy, int radius, TerrainPixel value)
    {
        int r2 = radius * radius;
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
                if (dx * dx + dy * dy <= r2)
                    SetSafe(grid, cx + dx, cy + dy, value);
    }

    static void FillQuarterCircle(TerrainGrid grid, int originX, int originY, int radius, TerrainPixel value)
    {
        int r2 = radius * radius;
        for (int dy = -radius; dy <= 0; dy++)
            for (int dx = 0; dx <= radius; dx++)
                if (dx * dx + dy * dy <= r2)
                    SetSafe(grid, originX + dx, originY + dy, value);
    }

    static void DrawDiagonalStrip(TerrainGrid grid, int startX, int startY, double angleDeg, int length, int thickness, TerrainPixel value)
    {
        double rad = angleDeg * Math.PI / 180.0;
        double cosA = Math.Cos(rad);
        double sinA = Math.Sin(rad);
        double perpX = -sinA;
        double perpY = cosA;

        for (int i = 0; i < length; i++)
        {
            double cx = startX + cosA * i;
            double cy = startY + sinA * i;
            for (int t = -(thickness / 2); t <= (thickness - 1) / 2; t++)
            {
                int px = (int)Math.Round(cx + perpX * t);
                int py = (int)Math.Round(cy + perpY * t);
                SetSafe(grid, px, py, value);
            }
        }
    }

    static void DrawDiagonalSplit(TerrainGrid grid, int left, int top, int w, int h, TerrainPixel matA, TerrainPixel matB)
    {
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool upperLeft = (x * h) < ((w - y) * w * h / (w + h));
                // Simple diagonal: above the line from top-right to bottom-left
                bool above = x * h + y * w < w * h;
                SetSafe(grid, left + x, top + y, above ? matA : matB);
            }
    }

    static void DrawLCorner(TerrainGrid grid, int left, int top, int size, int orientation, TerrainPixel value)
    {
        int half = size / 2;
        switch (orientation)
        {
            case 0: // top-left L
                FillRect(grid, left, top, size, half, value);
                FillRect(grid, left, top + half, half, half, value);
                break;
            case 1: // top-right L
                FillRect(grid, left, top, size, half, value);
                FillRect(grid, left + half, top + half, half, half, value);
                break;
            case 2: // bottom-left L
                FillRect(grid, left, top, half, half, value);
                FillRect(grid, left, top + half, size, half, value);
                break;
            case 3: // bottom-right L
                FillRect(grid, left + half, top, half, half, value);
                FillRect(grid, left, top + half, size, half, value);
                break;
        }
    }

    static void DrawStaircase(TerrainGrid grid, int left, int top, int w, int h, bool ascending, TerrainPixel value)
    {
        int steps = Math.Min(w, h);
        for (int i = 0; i < steps; i++)
        {
            int fillH = ascending ? (h - i * h / steps) : (i * h / steps + 1);
            int col = left + i;
            if (col >= grid.Width) break;
            for (int row = top + h - fillH; row < top + h; row++)
                SetSafe(grid, col, row, value);
        }
    }

    static void SetSafe(TerrainGrid grid, int x, int y, TerrainPixel value)
    {
        if (x >= 0 && x < grid.Width && y >= 0 && y < grid.Height)
            grid.SetPixelRaw(new Position(x, y), value);
    }

    static void SetScorchSafe(TerrainGrid grid, int x, int y, byte intensity)
    {
        if (x >= 0 && x < grid.Width && y >= 0 && y < grid.Height)
            grid.SetScorchIntensity(x + y * grid.Width, intensity);
    }

    static void SetScorchInCircle(TerrainGrid grid, int cx, int cy, int radius, byte intensity)
    {
        int r2 = radius * radius;
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
                if (dx * dx + dy * dy <= r2)
                    SetScorchSafe(grid, cx + dx, cy + dy, intensity);
    }
}
