namespace Tunnerer.Desktop.Rendering;

using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Config;

/// <summary>What one frame uploads of the terrain aux and material coverage textures.</summary>
/// <param name="ConsumesHeatDirty">The frame packed the terrain's heat dirty rect, so it can be cleared after upload.</param>
public readonly record struct TerrainAuxFrame(TerrainAuxUpload Upload, bool ConsumesHeatDirty);

/// <summary>
/// Keeps the CPU copies of the terrain aux texture (R=heat, G=SDF, B=material class, A=scorch level) and the
/// material coverage texture, and decides each frame which part of them to repack and upload: everything on the
/// first frame, then only cells the simulation changed, the strip the camera revealed and, every few steps, the
/// heat that changed.
/// </summary>
public sealed class TerrainAuxUploader
{
    private readonly TerrainAuxBuilder _builder;
    private readonly DrawProfile _profile;
    private readonly List<Position> _dirtyCells = new();
    private readonly byte[] _aux;
    private readonly byte[] _coverage;
    private bool _fullUploadPending = true;
    private int _heatFrameCounter;

    public TerrainAuxUploader(Size terrainSize, TerrainAuxBuilder builder, DrawProfile profile)
    {
        _builder = builder;
        _profile = profile;
        _aux = new byte[terrainSize.Area * 4];
        _coverage = new byte[terrainSize.Area * 4];
    }

    /// <summary>Forgets changed cells not yet packed; called at the start of each simulation batch.</summary>
    public void ClearDirtyCells() => _dirtyCells.Clear();

    /// <summary>Cells a simulation step changed; they are repacked on the next frame (and start a growth fade).</summary>
    public void ObserveTerrainChanges(TerrainGrid terrain, IReadOnlyList<Position> changedCells, float visualTime)
    {
        if (changedCells.Count == 0)
            return;
        _dirtyCells.AddRange(changedCells);
        _builder.GrowthFade.ObserveChanges(terrain, changedCells, visualTime);
    }

    /// <summary>
    /// Repacks what changed since the last frame into the CPU copies and says which rects to upload. Call
    /// <see cref="Commit"/> once the backend has taken the upload.
    /// </summary>
    /// <param name="steppedSinceRender">A simulation step ran since the last frame; heat only changes on steps.</param>
    public TerrainAuxFrame Prepare(
        TerrainGrid terrain, RenderViewState view, in Rect visibleRect, bool steppedSinceRender, float visualTime)
    {
        _builder.GrowthFade.CollectExpired(visualTime, _dirtyCells);

        Rect? auxDirtyRect;
        Rect? coverageDirtyRect;
        bool consumeHeatDirty;
        if (_fullUploadPending)
        {
            DrawProfile.Measure(ref _profile.ScreenAuxBuild, () =>
                _builder.BuildGpuTerrainAuxData(terrain, _aux, _coverage));
            auxDirtyRect = new Rect(0, 0, terrain.Size.X, terrain.Size.Y);
            coverageDirtyRect = auxDirtyRect;
            consumeHeatDirty = true;
        }
        else
        {
            Rect? terrainDirtyRect = RenderViewState.ClampToViewport(
                RenderViewState.TryGetDirtyCellBounds(_dirtyCells), visibleRect);
            // Heat only changes on simulation steps; in-between frames have nothing new to pack.
            bool includeHeatAux = steppedSinceRender &&
                (_heatFrameCounter++ % DesktopScreenTweaks.HeatAuxUpdateIntervalFrames) == 0;
            Rect? heatDirtyRect = includeHeatAux && terrain.TryGetHeatDirtyRect(out Rect dirtyRect)
                ? RenderViewState.ClampToViewport(dirtyRect, visibleRect)
                : null;

            Rect? cameraRevealRect = view.ComputeCameraRevealRect(visibleRect);

            // Terrain changes and camera reveal need all four channels rebuilt.
            Rect? fullAuxRect = RenderViewState.MergeDirtyRects(terrainDirtyRect, cameraRevealRect);

            // The build pads the rect by the blur margin; upload exactly what it wrote.
            if (fullAuxRect is Rect fullRect)
            {
                Rect written = fullRect;
                DrawProfile.Measure(ref _profile.AuxTerrainPack, () =>
                    written = _builder.BuildGpuTerrainAuxRect(terrain, _aux, _coverage, fullRect));
                fullAuxRect = written;
            }
            coverageDirtyRect = fullAuxRect;

            // Heat-only R-channel fast path for the remaining visible heat area.
            Rect? heatOnlyRect = heatDirtyRect;
            if (heatOnlyRect is not null && fullAuxRect is not null)
                heatOnlyRect = RenderViewState.SubtractCoveredHeatRect(heatOnlyRect, fullAuxRect);

            if (heatOnlyRect is Rect heatRect)
                DrawProfile.Measure(ref _profile.AuxHeatPack, () =>
                    TerrainAuxBuilder.UpdateGpuTerrainAuxHeatOnly(terrain, _aux, heatRect));

            auxDirtyRect = RenderViewState.MergeDirtyRects(fullAuxRect, heatOnlyRect);
            consumeHeatDirty = includeHeatAux;
        }
        view.UpdateLastAuxViewport(visibleRect);
        _dirtyCells.Clear();

        return new TerrainAuxFrame(
            new TerrainAuxUpload(
                Data: _aux,
                DirtyRect: auxDirtyRect,
                MaterialCoverage: _coverage,
                MaterialCoverageDirtyRect: coverageDirtyRect),
            consumeHeatDirty);
    }

    /// <summary>The backend has taken <paramref name="frame"/>'s upload.</summary>
    public void Commit(TerrainGrid terrain, in TerrainAuxFrame frame)
    {
        if (frame.ConsumesHeatDirty)
            terrain.ClearHeatDirtyRect();
        _fullUploadPending = false;
    }
}
