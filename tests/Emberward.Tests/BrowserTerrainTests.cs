namespace Emberward.Tests;

using Emberward.Browser.Client;
using Emberward.Core.Sensing;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

public class BrowserTerrainTests
{
    private static byte[][] Snapshot(BrowserTerrain renderer) =>
        Enumerable.Range(0, BrowserTerrain.TextureCount).Select(i => renderer.Texture(i).Data.ToArray()).ToArray();

    /// <summary>A step and the frames after it; two, because heat is packed on every other frame that follows a step.</summary>
    private static void StepAndDraw(BrowserTerrain renderer, Rect view, float time)
    {
        for (int i = 0; i < 2; i++)
        {
            renderer.ObserveStep(time);
            renderer.Prepare(view, time);
        }
    }

    [Fact]
    public void UnexploredGround_DoesNotRevealMaterialOrHeat()
    {
        var terrain = new TerrainGrid(new Size(32, 32));
        var vision = new VisionMap(terrain);
        var renderer = new BrowserTerrain(terrain, vision);
        var view = new Rect(0, 0, 32, 32);
        renderer.Prepare(view, 0);
        byte[][] hidden = Snapshot(renderer);
        Assert.Equal(TerrainGrid.HeatToByte(0), hidden[BrowserTerrain.Aux][0]);

        terrain.SetPixel(new Position(16, 16), TerrainPixel.Pyrite);
        terrain.AddHeat(new Position(16, 16), 255);
        vision.Update(terrain, [], [new Position(16, 16)]);
        StepAndDraw(renderer, view, 1);

        for (int i = 0; i < BrowserTerrain.TextureCount; i++)
            Assert.Equal(hidden[i], renderer.Texture(i).Data);
        Assert.All(renderer.Texture(BrowserTerrain.Fog).Data, value => Assert.Equal(0, value));
    }

    [Fact]
    public void RememberedGround_DoesNotRevealChangesOutsideVision()
    {
        var terrain = new TerrainGrid(new Size(32, 32));
        var cell = new Position(16, 16);
        terrain.SetPixel(cell, TerrainPixel.Pyrite);
        var vision = new VisionMap(terrain);
        vision.Update(terrain, [new VisionSource(cell, 4, 4)], []);
        vision.Update(terrain, [], []);
        var renderer = new BrowserTerrain(terrain, vision);
        var view = new Rect(12, 12, 8, 8);
        renderer.Prepare(view, 0);
        byte[][] remembered = Snapshot(renderer);

        terrain.SetPixel(cell, TerrainPixel.Blank);
        terrain.AddHeat(cell, 255);
        vision.Update(terrain, [], [cell]);
        StepAndDraw(renderer, view, 1);

        Assert.True(vision.IsExplored(cell));
        Assert.False(vision.IsVisible(cell));
        for (int i = 0; i < BrowserTerrain.TextureCount; i++)
            Assert.Equal(remembered[i], renderer.Texture(i).Data);
    }

    [Fact]
    public void FirstFrame_UploadsTheWholeMap_AndUnchangedFramesUploadNothing()
    {
        var terrain = new TerrainGrid(new Size(16, 16));
        var renderer = new BrowserTerrain(terrain, new VisionMap(terrain));
        var view = new Rect(4, 4, 8, 8);

        renderer.Prepare(view, 0);
        for (int i = 0; i < BrowserTerrain.TextureCount; i++)
        {
            Assert.Equal(new Rect(0, 0, 16, 16), renderer.Texture(i).Dirty);
            Assert.Equal(16 * 16 * 4, renderer.Texture(i).Data.Length);
        }

        renderer.Prepare(view, 0);
        for (int i = 0; i < BrowserTerrain.TextureCount; i++)
            Assert.Null(renderer.Texture(i).Dirty);
    }

    [Fact]
    public void AStep_UploadsWhatChangedInSight_NotTheView()
    {
        var terrain = new TerrainGrid(new Size(128, 96));
        var eye = new Position(64, 48);
        var dug = new Position(66, 48);
        terrain.SetPixel(dug, TerrainPixel.Dirt);
        var vision = new VisionMap(terrain);
        VisionSource[] sources = [new VisionSource(eye, 12, 12)];
        vision.Update(terrain, sources, []);
        var renderer = new BrowserTerrain(terrain, vision);
        var view = new Rect(24, 8, 80, 80);
        renderer.Prepare(view, 0);

        // Nothing happened: a step alone uploads nothing.
        vision.Update(terrain, sources, []);
        renderer.ObserveStep(1);
        renderer.Prepare(view, 1);
        for (int i = 0; i < BrowserTerrain.TextureCount; i++)
            Assert.Null(renderer.Texture(i).Dirty);

        terrain.SetPixel(dug, TerrainPixel.Blank);
        vision.Update(terrain, sources, [dug]);
        renderer.ObserveStep(2);
        renderer.Prepare(view, 2);

        Assert.Equal(new Rect(dug.X, dug.Y, 1, 1), renderer.Texture(BrowserTerrain.Color).Dirty);
        foreach (int index in new[] { BrowserTerrain.Aux, BrowserTerrain.Coverage })
        {
            Rect dirty = Assert.NotNull(renderer.Texture(index).Dirty);
            Assert.True(dirty.Left <= dug.X && dug.X < dirty.Right && dirty.Top <= dug.Y && dug.Y < dirty.Bottom);
            Assert.True(dirty.Width * dirty.Height < view.Width * view.Height / 16, $"{dirty} is not a small part of the view");
        }
    }

    [Fact]
    public void MovingTheView_UploadsOnlyTheGroundThatCameIntoIt()
    {
        var terrain = new TerrainGrid(new Size(128, 96));
        var renderer = new BrowserTerrain(terrain, new VisionMap(terrain));
        renderer.Prepare(new Rect(20, 10, 60, 60), 0);

        renderer.Prepare(new Rect(22, 10, 60, 60), 0);

        Rect dirty = Assert.NotNull(renderer.Texture(BrowserTerrain.Aux).Dirty);
        Assert.True(dirty.Left >= 80 - 8 && dirty.Right <= 82 + 8, $"{dirty} is more than the strip that came into view");
        Assert.Null(renderer.Texture(BrowserTerrain.Color).Dirty);
        Assert.Null(renderer.Texture(BrowserTerrain.Fog).Dirty);
    }
}
