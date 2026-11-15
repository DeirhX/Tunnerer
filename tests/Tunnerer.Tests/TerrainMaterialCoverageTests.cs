namespace Tunnerer.Tests;

using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Rendering;

public class TerrainMaterialCoverageTests
{
    [Fact]
    public void SeparablePasses_MatchTheFullKernel()
    {
        var terrain = TerrainTestPatternGenerator.Generate(new Size(200, 150));
        var actual = new byte[terrain.Width * terrain.Height * 4];
        TerrainMaterialCoverage.WriteRect(terrain, 0, 0, terrain.Width - 1, terrain.Height - 1, actual);

        for (int y = 0; y < terrain.Height; y++)
        {
            for (int x = 0; x < terrain.Width; x++)
            {
                var (s0, s1, s2, denom) = BruteForce(terrain, x, y);
                int i = (y * terrain.Width + x) * 4;
                if (denom <= 0f)
                {
                    Assert.Equal(0, actual[i] + actual[i + 1] + actual[i + 2]);
                    continue;
                }
                // Before the thin-feature lift the largest channel is unchanged; compare the dominant material.
                int expectedDominant = s0 >= s1 && s0 >= s2 ? 0 : s1 >= s2 ? 1 : 2;
                int actualDominant = actual[i] >= actual[i + 1] && actual[i] >= actual[i + 2] ? 0
                    : actual[i + 1] >= actual[i + 2] ? 1 : 2;
                float top = MathF.Max(s0, MathF.Max(s1, s2)) / denom;
                float second = (s0 + s1 + s2 - MathF.Max(s0, MathF.Max(s1, s2)) - MathF.Min(s0, MathF.Min(s1, s2))) / denom;
                if (top - second > 0.02f && top > 0.56f)
                    Assert.Equal(expectedDominant, actualDominant);
                Assert.InRange(actual[i] + actual[i + 1] + actual[i + 2], 252, 258);
            }
        }
    }

    [Fact]
    public void RectUpdate_MatchesFullWrite()
    {
        var terrain = TerrainTestPatternGenerator.Generate(new Size(120, 90));
        var full = new byte[terrain.Width * terrain.Height * 4];
        TerrainMaterialCoverage.WriteRect(terrain, 0, 0, terrain.Width - 1, terrain.Height - 1, full);
        var partial = (byte[])full.Clone();
        Array.Clear(partial, (40 * terrain.Width + 0) * 4, 20 * terrain.Width * 4);
        TerrainMaterialCoverage.WriteRect(terrain, 0, 40, terrain.Width - 1, 59, partial);
        Assert.Equal(full, partial);
    }

    private static (float, float, float, float) BruteForce(TerrainGrid terrain, int cx, int cy)
    {
        const int r = TerrainMaterialCoverage.KernelRadius;
        const float sigma = 1.5f;
        float s0 = 0, s1 = 0, s2 = 0;
        for (int ky = -r; ky <= r; ky++)
            for (int kx = -r; kx <= r; kx++)
            {
                int x = cx + kx, y = cy + ky;
                if ((uint)x >= (uint)terrain.Width || (uint)y >= (uint)terrain.Height) continue;
                float wgt = MathF.Exp(-(kx * kx + ky * ky) / (2f * sigma * sigma));
                switch (terrain.GetPixelRaw(y * terrain.Width + x))
                {
                    case TerrainPixel.Rock or TerrainPixel.HotRock or TerrainPixel.Gold: s0 += wgt; break;
                    case TerrainPixel.Concrete: s1 += wgt; break;
                    case TerrainPixel.Dirt or TerrainPixel.DirtGrow: s2 += wgt; break;
                }
            }
        return (s0, s1, s2, s0 + s1 + s2);
    }
}
