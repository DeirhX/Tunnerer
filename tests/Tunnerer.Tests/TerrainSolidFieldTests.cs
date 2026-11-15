namespace Tunnerer.Tests;

using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Rendering;

public class TerrainSolidFieldTests
{
    [Fact]
    public void EveryCell_IsOnItsOwnSideOfTheEdge()
    {
        var terrain = TerrainTestPatternGenerator.Generate(new Size(320, 240));
        var field = new TerrainSolidField();
        field.Rebuild(terrain);

        for (int y = 0; y < terrain.Height; y++)
        {
            for (int x = 0; x < terrain.Width; x++)
            {
                byte v = field.SampleAsByte(x, y);
                bool solid = TerrainSolidField.IsSolidTerrain(terrain.GetPixelRaw(y * terrain.Width + x));
                Assert.True(solid ? v >= 128 : v <= 127, $"cell ({x},{y}) solid={solid} field={v}");
            }
        }
    }

    [Fact]
    public void ThinFeatures_KeepTheMargin()
    {
        var terrain = new TerrainGrid(new Size(32, 32));
        terrain.Fill(TerrainPixel.Dirt);
        for (int y = 4; y < 28; y++)
            terrain.SetPixel(new Position(16, y), TerrainPixel.Blank);  // 1-cell tunnel
        terrain.SetPixel(new Position(6, 6), TerrainPixel.Blank);       // single dug cell

        var field = new TerrainSolidField();
        field.Rebuild(terrain);

        int margin = (int)MathF.Floor(TerrainSolidField.MinMargin * 255f);
        Assert.True(field.SampleAsByte(16, 16) <= 127 - margin + 1);
        Assert.True(field.SampleAsByte(6, 6) <= 127 - margin + 1);
        Assert.True(field.SampleAsByte(15, 16) >= 128);
    }

    [Fact]
    public void IncrementalUpdate_MatchesFullRebuild()
    {
        var terrain = TerrainTestPatternGenerator.Generate(new Size(160, 120));
        var incremental = new TerrainSolidField();
        incremental.Rebuild(terrain);

        var rng = new Random(1234);
        for (int dig = 0; dig < 40; dig++)
        {
            int cx = rng.Next(terrain.Width), cy = rng.Next(terrain.Height), r = rng.Next(1, 4);
            int minX = Math.Max(0, cx - r), maxX = Math.Min(terrain.Width - 1, cx + r);
            int minY = Math.Max(0, cy - r), maxY = Math.Min(terrain.Height - 1, cy + r);
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                    terrain.SetPixel(new Position(x, y), dig % 3 == 0 ? TerrainPixel.Dirt : TerrainPixel.Blank);

            int pad = TerrainSolidField.KernelRadius;
            incremental.UpdateRect(terrain,
                Math.Max(0, minX - pad), Math.Max(0, minY - pad),
                Math.Min(terrain.Width - 1, maxX + pad), Math.Min(terrain.Height - 1, maxY + pad));
        }

        var full = new TerrainSolidField();
        full.Rebuild(terrain);
        for (int y = 0; y < terrain.Height; y++)
            for (int x = 0; x < terrain.Width; x++)
                Assert.Equal(full.SampleAsByte(x, y), incremental.SampleAsByte(x, y));
    }
}
