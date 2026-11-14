namespace Tunnerer.Desktop.Rendering;

using Tunnerer.Core.Terrain;

/// <summary>
/// Per-cell Gaussian-blurred material coverage for the GPU "material coverage" texture.
/// Channels: R = stone (Rock, HotRock, and Gold, which is a vein drawn over the rock it was cut from),
/// G = concrete, B = dirt (Dirt, DirtGrow), A = unused.
/// Each channel is sum(w * [cell is that material]) / sum(w * [cell is any of the three]) over non-base solid
/// cells only, so the channels sum to 1 and caves, scorch and the base never bend a material boundary.
/// The shader picks materials from these smooth fields instead of interpolating material ids.
/// </summary>
public static class TerrainMaterialCoverage
{
    public const int KernelRadius = 4;
    private const int KernelSize = KernelRadius * 2 + 1;
    private const float GaussianSigma = 1.5f;

    // Thin features (1-cell veins) would otherwise be out-voted by their surroundings and vanish.
    // A cell whose own material covers less than ThinFeatureStart is lifted toward ThinFeatureTarget.
    private const float ThinFeatureStart = 0.42f;
    private const float ThinFeatureRange = 0.12f;
    private const float ThinFeatureTarget = 0.56f;

    private const int GroupNone = -1;
    private const int GroupCount = 3;

    private static readonly float[] GaussKernel = BuildKernel();
    private static readonly sbyte[] GroupOf = BuildGroupTable();

    private static float[] BuildKernel()
    {
        var kernel = new float[KernelSize * KernelSize];
        for (int ky = -KernelRadius; ky <= KernelRadius; ky++)
            for (int kx = -KernelRadius; kx <= KernelRadius; kx++)
                kernel[(ky + KernelRadius) * KernelSize + (kx + KernelRadius)] =
                    MathF.Exp(-(kx * kx + ky * ky) / (2f * GaussianSigma * GaussianSigma));
        return kernel;
    }

    private static sbyte[] BuildGroupTable()
    {
        var table = new sbyte[256];
        Array.Fill(table, (sbyte)GroupNone);
        table[(int)TerrainPixel.Rock] = 0;
        table[(int)TerrainPixel.HotRock] = 0;
        table[(int)TerrainPixel.Concrete] = 1;
        table[(int)TerrainPixel.Gold] = 0;
        table[(int)TerrainPixel.Dirt] = 2;
        table[(int)TerrainPixel.DirtGrow] = 2;
        return table;
    }

    /// <summary>Writes the coverage bytes for cell (cx, cy) at <paramref name="writeIndex"/>.</summary>
    public static void WriteCell(ITerrainPixelReadOnly terrain, int cx, int cy, byte[] target, int writeIndex)
    {
        int w = terrain.Width, h = terrain.Height;
        Span<float> sums = stackalloc float[GroupCount];
        sums.Clear();
        float denom = 0f;

        for (int ky = -KernelRadius; ky <= KernelRadius; ky++)
        {
            int ny = cy + ky;
            if ((uint)ny >= (uint)h) continue;
            int rowOff = ny * w;
            int kernelRow = (ky + KernelRadius) * KernelSize + KernelRadius;
            for (int kx = -KernelRadius; kx <= KernelRadius; kx++)
            {
                int nx = cx + kx;
                if ((uint)nx >= (uint)w) continue;
                int group = GroupOf[(int)terrain.GetPixelRaw(rowOff + nx)];
                if (group < 0) continue;
                float gw = GaussKernel[kernelRow + kx];
                sums[group] += gw;
                denom += gw;
            }
        }

        if (denom <= 0f)
        {
            target[writeIndex] = target[writeIndex + 1] = target[writeIndex + 2] = target[writeIndex + 3] = 0;
            return;
        }

        float inv = 1f / denom;
        float c0 = sums[0] * inv, c1 = sums[1] * inv, c2 = sums[2] * inv;

        int ownGroup = GroupOf[(int)terrain.GetPixelRaw(cx + cy * w)];
        if (ownGroup >= 0)
        {
            float own = ownGroup switch { 0 => c0, 1 => c1, _ => c2 };
            float lift = Math.Clamp((ThinFeatureStart - own) / ThinFeatureRange, 0f, 1f);
            if (lift > 0f)
            {
                float lifted = own + (ThinFeatureTarget - own) * lift;
                float scale = (1f - lifted) / (1f - own);
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
