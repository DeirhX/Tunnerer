using Emberward.Core.Terrain;
using Emberward.Core.Types;

namespace Emberward.Tests;

public class TerrainPixelTests
{
    [Theory]
    [InlineData(TerrainPixel.LevelGenDirt, 0)]
    [InlineData(TerrainPixel.LevelGenRock, 1)]
    [InlineData(TerrainPixel.LevelGenMark, 2)]
    [InlineData(TerrainPixel.Blank, (byte)' ')]
    [InlineData(TerrainPixel.Dirt, (byte)'D')]
    [InlineData(TerrainPixel.DirtGrow, (byte)'g')]
    [InlineData(TerrainPixel.Rock, (byte)'r')]
    [InlineData(TerrainPixel.Decal, (byte)'.')]
    [InlineData(TerrainPixel.BaseMin, (byte)'0')]
    [InlineData(TerrainPixel.BaseMax, (byte)'7')]
    [InlineData(TerrainPixel.BaseBarrier, (byte)'8')]
    [InlineData(TerrainPixel.BaseCore, (byte)'9')]
    [InlineData(TerrainPixel.Concrete, (byte)'C')]
    [InlineData(TerrainPixel.Emberite, (byte)'o')]
    [InlineData(TerrainPixel.EmberiteCore, (byte)'O')]
    public void EnumValues_MatchCpp(TerrainPixel pixel, byte expected)
    {
        Assert.Equal(expected, (byte)pixel);
    }

    [Theory]
    [InlineData(TerrainPixel.Dirt, true)]
    [InlineData(TerrainPixel.DirtGrow, false)]
    [InlineData(TerrainPixel.Rock, false)]
    [InlineData(TerrainPixel.Blank, false)]
    public void IsDirt_MatchesCpp(TerrainPixel pixel, bool expected)
    {
        Assert.Equal(expected, Pixel.IsDirt(pixel));
    }

    [Theory]
    [InlineData(TerrainPixel.Dirt, true)]
    [InlineData(TerrainPixel.DirtGrow, true)]
    [InlineData(TerrainPixel.Rock, false)]
    [InlineData(TerrainPixel.Blank, false)]
    public void IsDiggable_MatchesCpp(TerrainPixel pixel, bool expected)
    {
        Assert.Equal(expected, Pixel.IsDiggable(pixel));
    }

    [Theory]
    [InlineData(TerrainPixel.Decal, true)]
    [InlineData(TerrainPixel.Blank, false)]
    [InlineData(TerrainPixel.Dirt, false)]
    public void IsScorched_MatchesCpp(TerrainPixel pixel, bool expected)
    {
        Assert.Equal(expected, Pixel.IsScorched(pixel));
    }

    [Theory]
    [InlineData(TerrainPixel.Rock, true)]
    [InlineData(TerrainPixel.Concrete, true)]
    [InlineData(TerrainPixel.BaseMin, true)]
    [InlineData(TerrainPixel.Dirt, false)]
    [InlineData(TerrainPixel.Blank, false)]
    public void IsBlockingCollision_MatchesCpp(TerrainPixel pixel, bool expected)
    {
        Assert.Equal(expected, Pixel.IsBlockingCollision(pixel));
    }

    [Fact]
    public void GetColor_ProducesNonMagentaForAllKnownPixels()
    {
        var magenta = new Color(255, 0, 255);
        TerrainPixel[] knownPixels =
        [
            TerrainPixel.Blank, TerrainPixel.Dirt,
            TerrainPixel.DirtGrow, TerrainPixel.Rock, TerrainPixel.Decal,
            TerrainPixel.Concrete, TerrainPixel.Emberite, TerrainPixel.EmberiteCore,
            TerrainPixel.BaseMin, TerrainPixel.BaseBarrier, TerrainPixel.BaseCore
        ];

        foreach (var pix in knownPixels)
            Assert.NotEqual(magenta, Pixel.GetColor(pix));
    }
}
