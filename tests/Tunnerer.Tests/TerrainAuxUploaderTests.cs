using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;
using Tunnerer.Desktop;
using Tunnerer.Desktop.Rendering;

namespace Tunnerer.Tests;

public class TerrainAuxUploaderTests
{
    private static readonly Size TerrainSize = new(128, 96);
    private static readonly Rect Viewport = new(0, 0, 128, 96);

    private readonly TerrainGrid _terrain = new(TerrainSize);
    private readonly RenderViewState _view = new();
    private readonly TerrainAuxUploader _uploader = new(TerrainSize, new TerrainAuxBuilder(), new DrawProfile());

    private TerrainAuxFrame Frame(bool stepped = false)
    {
        var frame = _uploader.Prepare(TerrainView.Live(_terrain), _view, Viewport, stepped, visualTime: 0f);
        _uploader.Commit(_terrain, frame);
        return frame;
    }

    private static bool Contains(Rect? rect, Position p) =>
        rect is Rect r && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    [Fact]
    public void FirstFrame_UploadsEverything()
    {
        var frame = Frame();

        var full = new Rect(0, 0, TerrainSize.X, TerrainSize.Y);
        Assert.Equal(full, frame.Upload.DirtyRect);
        Assert.Equal(full, frame.Upload.MaterialCoverageDirtyRect);
        Assert.True(frame.ConsumesHeatDirty);
        Assert.Equal(TerrainSize.Area * 4, frame.Upload.Data!.Length);
        Assert.Equal(TerrainSize.Area * 4, frame.Upload.MaterialCoverage!.Length);
    }

    [Fact]
    public void NothingChanged_UploadsNothing()
    {
        Frame();
        var frame = Frame();

        Assert.Null(frame.Upload.DirtyRect);
        Assert.Null(frame.Upload.MaterialCoverageDirtyRect);
        Assert.False(frame.ConsumesHeatDirty);
    }

    [Fact]
    public void ChangedCell_IsRepackedInAllChannels()
    {
        Frame();
        var cell = new Position(40, 30);
        _uploader.ObserveTerrainChanges(_terrain, [cell], visualTime: 0f);
        var frame = Frame();

        Assert.True(Contains(frame.Upload.DirtyRect, cell));
        Assert.Equal(frame.Upload.DirtyRect, frame.Upload.MaterialCoverageDirtyRect);
    }

    [Fact]
    public void ClearedDirtyCells_AreNotRepacked()
    {
        Frame();
        _uploader.ObserveTerrainChanges(_terrain, [new Position(40, 30)], visualTime: 0f);
        _uploader.ClearDirtyCells();
        var frame = Frame();

        Assert.Null(frame.Upload.DirtyRect);
    }

    [Fact]
    public void HeatChange_IsPackedOnlyAfterAStep()
    {
        Frame();
        var cell = new Position(60, 50);
        _terrain.AddHeat(cell, 200);

        var between = Frame(stepped: false);
        Assert.Null(between.Upload.DirtyRect);

        var afterStep = Frame(stepped: true);
        Assert.True(Contains(afterStep.Upload.DirtyRect, cell));
        Assert.Null(afterStep.Upload.MaterialCoverageDirtyRect);
        Assert.True(afterStep.ConsumesHeatDirty);
        Assert.False(_terrain.TryGetHeatDirtyRect(out _));
    }
}
