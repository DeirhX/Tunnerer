namespace Emberward.Desktop.Rendering;

using Emberward.Core.Terrain;

/// <summary>
/// The smooth solid/cave field the GPU draws every cave edge from (aux texture G channel, 0..255, edge at 0.5).
/// </summary>
/// <remarks>
/// A narrow Gaussian blur of solid occupancy, then "folded" so that every cell keeps at least
/// <see cref="MinMargin"/> on its own side of 0.5. Bilinear sampling returns exactly the stored value at a cell
/// centre, so the 0.5 contour never crosses the centre of a cell of the other kind: the drawn edge stays within
/// half a cell of the collision edge, and thin tunnels, walls and single cells can neither vanish nor close up.
/// Ordinary edges are untouched, since cells next to them are already further from 0.5 than the margin.
/// Out-of-bounds cells count as solid.
/// </remarks>
public sealed class TerrainSolidField
{
    public const int KernelRadius = 3;
    private const float GaussianSigma = 1.25f;
    /// <summary>Minimum distance of a cell's value from 0.5; larger widens thin features, smaller ripples less.</summary>
    public const float MinMargin = 0.10f;

    private static readonly float[] Kernel = BuildKernel();

    private byte[]? _field;
    private int _width;
    private int _height;

    public void Rebuild(ITerrainPixelReadOnly terrain)
    {
        _width = terrain.Width;
        _height = terrain.Height;
        if (_field == null || _field.Length != _width * _height)
            _field = new byte[_width * _height];
        Compute(terrain, 0, 0, _width - 1, _height - 1);
    }

    /// <summary>Recomputes exactly the given cells (inclusive). The caller pads its dirty rect by <see cref="KernelRadius"/>.</summary>
    public void UpdateRect(ITerrainPixelReadOnly terrain, int minX, int minY, int maxX, int maxY)
    {
        if (_field == null || _width != terrain.Width || _height != terrain.Height)
        {
            Rebuild(terrain);
            return;
        }
        Compute(terrain, minX, minY, maxX, maxY);
    }

    public byte SampleAsByte(int x, int y)
    {
        if (_field == null || (uint)x >= (uint)_width || (uint)y >= (uint)_height) return 255;
        return _field[y * _width + x];
    }

    public static bool IsSolidTerrain(TerrainPixel p) => p != TerrainPixel.Blank && !Pixel.IsScorched(p);

    private void Compute(ITerrainPixelReadOnly terrain, int minX, int minY, int maxX, int maxY)
    {
        int w = _width, h = _height;
        int cols = maxX - minX + 1;
        int rowMin = minY - KernelRadius, rowMax = maxY + KernelRadius;
        int rows = rowMax - rowMin + 1;
        if (cols <= 0 || maxY < minY) return;

        // Horizontal pass over the rows the vertical pass will read; rows outside the grid are all solid (1).
        var horizontal = new float[rows * cols];
        void HorizontalRow(int r)
        {
            int y = rowMin + r;
            int dst = r * cols;
            if ((uint)y >= (uint)h)
            {
                Array.Fill(horizontal, 1f, dst, cols);
                return;
            }
            int rowOff = y * w;
            for (int i = 0; i < cols; i++)
            {
                int x = minX + i;
                float sum = 0f;
                for (int k = -KernelRadius; k <= KernelRadius; k++)
                {
                    int nx = x + k;
                    bool solid = (uint)nx >= (uint)w || IsSolidTerrain(terrain.GetPixelRaw(rowOff + nx));
                    if (solid) sum += Kernel[k + KernelRadius];
                }
                horizontal[dst + i] = sum;
            }
        }

        // Vertical pass, then the fold.
        byte[] field = _field!;
        void VerticalRow(int y)
        {
            int rowOff = y * w;
            int r0 = y - rowMin;
            for (int i = 0; i < cols; i++)
            {
                float g = 0f;
                for (int k = -KernelRadius; k <= KernelRadius; k++)
                    g += Kernel[k + KernelRadius] * horizontal[(r0 + k) * cols + i];
                int x = minX + i;
                field[rowOff + x] = Fold(g, IsSolidTerrain(terrain.GetPixelRaw(rowOff + x)));
            }
        }

        int taps = cols * (KernelRadius * 2 + 1);
        RowParallel.For(0, rows, taps, (from, to) => { for (int r = from; r < to; r++) HorizontalRow(r); });
        RowParallel.For(minY, maxY + 1, taps, (from, to) => { for (int y = from; y < to; y++) VerticalRow(y); });
    }

    /// <summary>Blurred solid fraction <paramref name="g"/> to a byte kept on the cell's own side of 0.5.</summary>
    internal static byte Fold(float g, bool solid)
    {
        float a = MathF.Max(MathF.Abs(g - 0.5f), MinMargin);
        float v = 0.5f + (solid ? a : -a);
        int b = (int)MathF.Round(v * 255f);
        // 0.5 sits between bytes 127 and 128: solid cells must be >= 128, cave cells <= 127.
        return (byte)(solid ? Math.Clamp(b, 128, 255) : Math.Clamp(b, 0, 127));
    }

    private static float[] BuildKernel()
    {
        var k = new float[KernelRadius * 2 + 1];
        float sum = 0f;
        for (int i = -KernelRadius; i <= KernelRadius; i++)
            sum += k[i + KernelRadius] = MathF.Exp(-(i * i) / (2f * GaussianSigma * GaussianSigma));
        for (int i = 0; i < k.Length; i++)
            k[i] /= sum;
        return k;
    }
}
