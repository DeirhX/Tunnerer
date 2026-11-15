namespace Tunnerer.Desktop.Rendering;

using Tunnerer.Core.Terrain;

/// <summary>
/// Per-cell Gaussian-blurred material coverage for the GPU "material coverage" texture.
/// Channels: R = stone (Rock, HotRock, and Gold, which is a vein drawn over the rock it was cut from),
/// G = concrete, B = dirt (Dirt, DirtGrow). A is written by <see cref="TerrainAuxBuilder"/>: the regrow stamp of
/// <see cref="TerrainGrowthFade"/>.
/// Each channel is sum(w * [cell is that material]) / sum(w * [cell is any of the three]) over non-base solid
/// cells only, so the channels sum to 1 and caves, scorch and the base never bend a material boundary.
/// The shader picks materials from these smooth fields instead of interpolating material ids.
/// </summary>
/// <remarks>
/// The Gaussian is separable, so both sums are computed as a horizontal pass then a vertical pass
/// (2 × 9 taps per cell instead of 81), each spread over cores by <see cref="RowParallel"/>.
/// </remarks>
public static class TerrainMaterialCoverage
{
    public const int KernelRadius = 4;
    private const float GaussianSigma = 1.5f;

    // Thin features (1-cell veins) would otherwise be out-voted by their surroundings and vanish.
    // A cell whose own material covers less than ThinFeatureStart is lifted toward ThinFeatureTarget.
    private const float ThinFeatureStart = 0.42f;
    private const float ThinFeatureRange = 0.12f;
    private const float ThinFeatureTarget = 0.56f;

    private const int GroupNone = -1;
    private const int GroupCount = 3;
    // Per cell in the pass buffers: stone, concrete, dirt, any.
    private const int Lanes = GroupCount + 1;

    private static readonly float[] Kernel = BuildKernel();
    private static readonly sbyte[] GroupOf = BuildGroupTable();

    private static float[] BuildKernel()
    {
        var kernel = new float[KernelRadius * 2 + 1];
        for (int k = -KernelRadius; k <= KernelRadius; k++)
            kernel[k + KernelRadius] = MathF.Exp(-(k * k) / (2f * GaussianSigma * GaussianSigma));
        return kernel;
    }

    private static sbyte[] BuildGroupTable()
    {
        var table = new sbyte[256];
        Array.Fill(table, (sbyte)GroupNone);
        table[(int)TerrainPixel.Rock] = 0;
        table[(int)TerrainPixel.HotRock] = 0;
        table[(int)TerrainPixel.FrostRock] = 0;
        table[(int)TerrainPixel.Concrete] = 1;
        table[(int)TerrainPixel.Gold] = 0;
        table[(int)TerrainPixel.Dirt] = 2;
        table[(int)TerrainPixel.DirtGrow] = 2;
        return table;
    }

    /// <summary>
    /// Writes the coverage bytes (4 per cell, row-major over the whole grid) for the cells in the inclusive rect.
    /// </summary>
    public static void WriteRect(ITerrainPixelReadOnly terrain, int minX, int minY, int maxX, int maxY, byte[] target)
    {
        int w = terrain.Width, h = terrain.Height;
        int cols = maxX - minX + 1;
        if (cols <= 0 || maxY < minY) return;
        // Rows outside the grid contribute nothing, so the horizontal pass only covers rows inside it.
        int rowMin = Math.Max(0, minY - KernelRadius), rowMax = Math.Min(h - 1, maxY + KernelRadius);
        int stride = cols * Lanes;
        var horizontal = new float[(rowMax - rowMin + 1) * stride];

        RowParallel.For(rowMin, rowMax + 1, cols * (KernelRadius * 2 + 1), (from, to) =>
        {
            for (int y = from; y < to; y++)
            {
                int rowOff = y * w;
                int dst = (y - rowMin) * stride;
                for (int i = 0; i < cols; i++, dst += Lanes)
                {
                    int x = minX + i;
                    int k0 = Math.Max(-KernelRadius, -x), k1 = Math.Min(KernelRadius, w - 1 - x);
                    float s0 = 0f, s1 = 0f, s2 = 0f;
                    for (int k = k0; k <= k1; k++)
                    {
                        int group = GroupOf[(int)terrain.GetPixelRaw(rowOff + x + k)];
                        float kw = Kernel[k + KernelRadius];
                        if (group == 0) s0 += kw;
                        else if (group == 1) s1 += kw;
                        else if (group == 2) s2 += kw;
                    }
                    horizontal[dst] = s0;
                    horizontal[dst + 1] = s1;
                    horizontal[dst + 2] = s2;
                    horizontal[dst + 3] = s0 + s1 + s2;
                }
            }
        });

        RowParallel.For(minY, maxY + 1, cols * (KernelRadius * 2 + 1), (from, to) =>
        {
            for (int y = from; y < to; y++)
            {
                int k0 = Math.Max(-KernelRadius, rowMin - y), k1 = Math.Min(KernelRadius, rowMax - y);
                for (int i = 0; i < cols; i++)
                {
                    float s0 = 0f, s1 = 0f, s2 = 0f, denom = 0f;
                    int src = (y + k0 - rowMin) * stride + i * Lanes;
                    for (int k = k0; k <= k1; k++, src += stride)
                    {
                        float kw = Kernel[k + KernelRadius];
                        s0 += kw * horizontal[src];
                        s1 += kw * horizontal[src + 1];
                        s2 += kw * horizontal[src + 2];
                        denom += kw * horizontal[src + 3];
                    }
                    int x = minX + i;
                    WriteCell(terrain.GetPixelRaw(y * w + x), s0, s1, s2, denom, target, (y * w + x) * 4);
                }
            }
        });
    }

    private static void WriteCell(TerrainPixel own, float s0, float s1, float s2, float denom, byte[] target, int writeIndex)
    {
        if (denom <= 0f)
        {
            target[writeIndex] = target[writeIndex + 1] = target[writeIndex + 2] = target[writeIndex + 3] = 0;
            return;
        }

        float inv = 1f / denom;
        float c0 = s0 * inv, c1 = s1 * inv, c2 = s2 * inv;

        int ownGroup = GroupOf[(int)own];
        if (ownGroup >= 0)
        {
            float ownCover = ownGroup switch { 0 => c0, 1 => c1, _ => c2 };
            float lift = Math.Clamp((ThinFeatureStart - ownCover) / ThinFeatureRange, 0f, 1f);
            if (lift > 0f)
            {
                float lifted = ownCover + (ThinFeatureTarget - ownCover) * lift;
                float scale = (1f - lifted) / (1f - ownCover);
                c0 *= scale; c1 *= scale; c2 *= scale;
                switch (ownGroup)
                {
                    case 0: c0 = lifted; break;
                    case 1: c1 = lifted; break;
                    default: c2 = lifted; break;
                }
            }
        }

        target[writeIndex] = ToByte(c0);
        target[writeIndex + 1] = ToByte(c1);
        target[writeIndex + 2] = ToByte(c2);
        target[writeIndex + 3] = 0;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
}
