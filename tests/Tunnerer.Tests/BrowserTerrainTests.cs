namespace Tunnerer.Tests;

using Tunnerer.Browser.Client;
using Tunnerer.Core.Sensing;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

public class BrowserTerrainTests
{
    [Fact]
    public void UnexploredGround_DoesNotRevealMaterialOrHeat()
    {
        var terrain = new TerrainGrid(new Size(32, 32));
        var vision = new VisionMap(terrain);
        var renderer = new BrowserTerrain(terrain, vision);
        var view = new Rect(0, 0, 32, 32);
        renderer.Prepare(view, 0);
        byte[][] hidden = Enumerable.Range(0, 4).Select(i => renderer.Texture(i).ToArray()).ToArray();
        Assert.Equal(TerrainGrid.HeatToByte(0), hidden[1][0]);

        terrain.SetPixel(new Position(16, 16), TerrainPixel.Emberite);
        terrain.AddHeat(new Position(16, 16), 255);
        vision.Update(terrain, [], [new Position(16, 16)]);
        renderer.ObserveStep(1);
        renderer.Prepare(view, 1);
        for (int i = 0; i < 4; i++)
            Assert.Equal(hidden[i], renderer.Texture(i));
        Assert.All(renderer.Texture(3), value => Assert.Equal(0, value));
    }

    [Fact]
    public void RememberedGround_DoesNotRevealChangesOutsideVision()
    {
        var terrain = new TerrainGrid(new Size(32, 32));
        var cell = new Position(16, 16);
        terrain.SetPixel(cell, TerrainPixel.Emberite);
        var vision = new VisionMap(terrain);
        vision.Update(terrain, [new VisionSource(cell, 4, 4)], []);
        vision.Update(terrain, [], []);
        var renderer = new BrowserTerrain(terrain, vision);
        var view = new Rect(12, 12, 8, 8);
        renderer.Prepare(view, 0);
        byte[][] remembered = Enumerable.Range(0, 4).Select(i => renderer.Texture(i).ToArray()).ToArray();

        terrain.SetPixel(cell, TerrainPixel.Blank);
        terrain.AddHeat(cell, 255);
        vision.Update(terrain, [], [cell]);
        renderer.ObserveStep(1);
        renderer.Prepare(view, 1);

        Assert.True(vision.IsExplored(cell));
        Assert.False(vision.IsVisible(cell));
        for (int i = 0; i < 4; i++)
            Assert.Equal(remembered[i], renderer.Texture(i));
    }

    [Fact]
    public void Uploads_AreClippedToTheWorld_AndUnchangedFramesUploadNothing()
    {
        var terrain = new TerrainGrid(new Size(16, 16));
        var renderer = new BrowserTerrain(terrain, new VisionMap(terrain));

        renderer.Prepare(new Rect(-32, -32, 64, 64), 0);
        Assert.Equal(new Rect(0, 0, 16, 16), renderer.UploadRect);
        Assert.Equal(16 * 16 * 4, renderer.Texture(0).Length);
        renderer.Prepare(new Rect(-32, -32, 64, 64), 0);
        Assert.Equal(default, renderer.UploadRect);
    }
}
