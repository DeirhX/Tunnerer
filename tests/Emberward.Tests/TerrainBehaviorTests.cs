using Emberward.Core.Terrain;
using Emberward.Core.Types;

namespace Emberward.Tests;

public class TerrainBehaviorTests
{
    [Theory]
    [InlineData(TerrainPixel.Rock, true, false)]
    [InlineData(TerrainPixel.Concrete, true, false)]
    [InlineData(TerrainPixel.Dirt, false, true)]
    [InlineData(TerrainPixel.Blank, false, false)]
    [InlineData(TerrainPixel.Decal, false, false)]
    public void BehaviorTable_MatchesLegacyMethods(TerrainPixel pix, bool blocksMovement, bool softCollision)
    {
        var b = Pixel.GetBehavior(pix);
        Assert.Equal(blocksMovement, b.BlocksMovement);
        Assert.Equal(softCollision, b.SoftCollision);
        Assert.Equal(blocksMovement || softCollision, b.IsAnyCollision);
    }

    [Fact]
    public void BaseRange_AllBlockMovement()
    {
        for (byte b = (byte)TerrainPixel.BaseMin; b <= (byte)TerrainPixel.BaseMax; b++)
        {
            var pix = (TerrainPixel)b;
            Assert.True(Pixel.IsBlockingCollision(pix), $"Base pixel {pix} should block movement");
            Assert.True(Pixel.IsBase(pix), $"Base pixel {pix} should be recognized as base");
        }
    }

    [Fact]
    public void BaseBarrier_DoesNotBlockMovement()
    {
        Assert.False(Pixel.IsBlockingCollision(TerrainPixel.BaseBarrier));
        Assert.False(Pixel.IsBase(TerrainPixel.BaseBarrier));
    }

    [Theory]
    [InlineData(TerrainPixel.Dirt)]
    [InlineData(TerrainPixel.DirtGrow)]
    public void Diggable_IncludesAllDirtVariants(TerrainPixel pix)
    {
        Assert.True(Pixel.IsDiggable(pix));
    }

    [Fact]
    public void Torchable_IncludesDiggableAndMineral()
    {
        Assert.True(Pixel.IsTorchable(TerrainPixel.Dirt));
        Assert.True(Pixel.IsTorchable(TerrainPixel.Rock));
        Assert.True(Pixel.IsTorchable(TerrainPixel.Concrete));
        Assert.False(Pixel.IsTorchable(TerrainPixel.Blank));
    }

    [Fact]
    public void GetColor_AllDefinedPixels_NotMagenta()
    {
        var magenta = new Color(0xff, 0x00, 0xff);
        TerrainPixel[] defined = [
            TerrainPixel.Blank, TerrainPixel.Dirt,
            TerrainPixel.DirtGrow, TerrainPixel.Rock, TerrainPixel.Decal,
            TerrainPixel.Concrete, TerrainPixel.Emberite, TerrainPixel.EmberiteCore,
            TerrainPixel.BaseBarrier, TerrainPixel.BaseFloor, TerrainPixel.BaseMin, TerrainPixel.BaseMax,
        ];

        foreach (var pix in defined)
            Assert.NotEqual(magenta, Pixel.GetColor(pix));
    }

    [Fact]
    public void GetColor_UndefinedPixel_ReturnsMagenta()
    {
        var magenta = new Color(0xff, 0x00, 0xff);
        var color = Pixel.GetColor((TerrainPixel)255);
        Assert.Equal(magenta, color);
    }

    [Fact]
    public void Rock_IsMineral_NotConcrete()
    {
        Assert.True(Pixel.IsMineral(TerrainPixel.Rock));
        Assert.True(Pixel.IsRock(TerrainPixel.Rock));
        Assert.False(Pixel.IsConcrete(TerrainPixel.Rock));
    }

    [Fact]
    public void Concrete_IsMineral_AndConcrete()
    {
        Assert.True(Pixel.IsMineral(TerrainPixel.Concrete));
        Assert.True(Pixel.IsConcrete(TerrainPixel.Concrete));
        Assert.False(Pixel.IsRock(TerrainPixel.Concrete));
    }

    [Fact]
    public void Scorched_IsRecognized()
    {
        Assert.True(Pixel.IsScorched(TerrainPixel.Decal));
        Assert.False(Pixel.IsBlockingCollision(TerrainPixel.Decal));
        Assert.False(Pixel.IsDiggable(TerrainPixel.Decal));
    }

    [Fact]
    public void DirtGrow_IsDiggable_ButNotSoftCollision()
    {
        Assert.True(Pixel.IsDiggable(TerrainPixel.DirtGrow));
        Assert.False(Pixel.IsSoftCollision(TerrainPixel.DirtGrow));
        Assert.False(Pixel.IsDirt(TerrainPixel.DirtGrow));
    }

    [Fact]
    public void GetBehavior_ReturnsRefToSameInstance()
    {
        ref readonly var b1 = ref Pixel.GetBehavior(TerrainPixel.Rock);
        ref readonly var b2 = ref Pixel.GetBehavior(TerrainPixel.Rock);
        Assert.Equal(b1, b2);
    }

    [Fact]
    public void GroundKinds_SortPixels()
    {
        Assert.True(Pixel.IsOpenGround(TerrainPixel.Blank));
        Assert.False(Pixel.IsOpenGround(TerrainPixel.Rock));
        Assert.False(Pixel.IsOpenGround(TerrainPixel.Dirt));
        Assert.True(Pixel.IsSoil(TerrainPixel.Dirt));
        Assert.True(Pixel.IsSoil(TerrainPixel.DirtGrow));
        Assert.False(Pixel.IsSoil(TerrainPixel.Rock));
        Assert.True(Pixel.IsSolidGround(TerrainPixel.Rock));
        Assert.False(Pixel.IsSolidGround(TerrainPixel.Blank));
        Assert.True(Pixel.IsAnyBaseCell(TerrainPixel.BaseFloor));
        Assert.True(Pixel.IsAnyBaseCell(TerrainPixel.BaseBarrier));
        Assert.False(Pixel.IsAnyBaseCell(TerrainPixel.Dirt));
    }

    [Fact]
    public void Blank_HasBlackColor()
    {
        var black = new Color(0, 0, 0);
        Assert.Equal(black, Pixel.GetColor(TerrainPixel.Blank));
    }

    [Theory]
    [InlineData(TerrainPixel.Rock)]
    [InlineData(TerrainPixel.Concrete)]
    [InlineData(TerrainPixel.Dirt)]
    [InlineData(TerrainPixel.DirtGrow)]
    public void Torchable_MatchesDerivedProperty(TerrainPixel pix)
    {
        var b = Pixel.GetBehavior(pix);
        Assert.Equal(b.Diggable || b.Mineral, b.Torchable);
        Assert.True(Pixel.IsTorchable(pix));
    }

    [Theory]
    [InlineData(TerrainPixel.Blank)]
    [InlineData(TerrainPixel.Decal)]
    [InlineData(TerrainPixel.BaseBarrier)]
    public void NotTorchable_PixelTypes(TerrainPixel pix)
    {
        Assert.False(Pixel.IsTorchable(pix));
    }
}
