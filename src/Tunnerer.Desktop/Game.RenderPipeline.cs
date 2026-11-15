namespace Tunnerer.Desktop;

using Tunnerer.Core.Config;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Gui;
using Tunnerer.Desktop.Input;
using Tunnerer.Desktop.Rendering;
using System.Diagnostics;

public partial class Game
{
    /// <param name="stepProgress">
    /// Where this frame lies between the previous simulation step (0) and the latest (1); moving entities and the
    /// camera are drawn at that point.
    /// </param>
    private void RenderImGuiFrame(
        IReadOnlyList<Core.Entities.Tank> tanks, in FrameInputSnapshot frameInput, float stepProgress)
    {
        var (winW, winH) = _renderer.GetWindowSize();
        float dt = _interpolateFrames
            ? Math.Clamp((float)_renderClock.Elapsed.TotalSeconds, 0.001f, 0.1f)
            : 1f / Tweaks.Perf.TargetFps;
        _renderClock.Restart();
        int scale = DesktopScreenTweaks.PixelScale;
        bool uiViewport = _renderBackend.SupportsUi && !_terrainTestMode;
        var viewSize = _renderViewState.ComputeViewSize(winW, winH, uiViewport);
        _renderViewState.EnsureHiResBuffer(viewSize);

        var player = tanks.Count > 0 ? tanks[0] : null;
        ReadOnlySpan<PositionF> drawnTankPositions = _entityLayer.InterpolateTankPositions(stepProgress);
        _renderViewState.UpdateCamera(
            drawnTankPositions.Length > 0 ? drawnTankPositions[0] : null, _terrainSize, viewSize.X, viewSize.Y, scale);
        var renderView = new RenderView(
            ViewSize: _renderViewState.HiResSize,
            WorldSize: _terrainSize,
            CameraPixels: new Position(_renderViewState.CamPixelX, _renderViewState.CamPixelY),
            PixelScale: scale);
        _entityLayer.Build(
            renderView, stepProgress, turnSeconds: _paused ? 0f : dt, snapHeadings: !_interpolateFrames);
        bool steppedSinceRender = _steppedSinceRender;
        _steppedSinceRender = false;

        ProfileSection(ref _drawProfile.ScreenDraw, () =>
        {
            var hiResWatch = Stopwatch.StartNew();

            var visibleRect = _renderViewState.ComputeVisibleWorldRect(_terrainSize, scale);
            float visualTime = VisualTime;
            _terrainAuxBuilder.GrowthFade.CollectExpired(visualTime, _terrainDirtyCells);

            Rect? auxDirtyRect;
            Rect? coverageDirtyRect;
            bool consumeHeatDirty;
            if (_gpuAuxFullUploadPending)
            {
                ProfileSection(ref _drawProfile.ScreenAuxBuild, () =>
                    _terrainAuxBuilder.BuildGpuTerrainAuxData(_world.Terrain, _gpuTerrainAux, _gpuMaterialCoverage));
                auxDirtyRect = new Rect(0, 0, _terrainSize.X, _terrainSize.Y);
                coverageDirtyRect = auxDirtyRect;
                consumeHeatDirty = true;
            }
            else
            {
                Rect? terrainDirtyRect = RenderViewState.ClampToViewport(
                    RenderViewState.TryGetDirtyCellBounds(_terrainDirtyCells), visibleRect);
                // Heat only changes on simulation steps; in-between frames have nothing new to pack.
                bool includeHeatAux = steppedSinceRender &&
                    (_heatAuxFrameCounter++ % DesktopScreenTweaks.HeatAuxUpdateIntervalFrames) == 0;
                Rect? heatDirtyRect = includeHeatAux && _world.Terrain.TryGetHeatDirtyRect(out Rect dirtyRect)
                    ? RenderViewState.ClampToViewport(dirtyRect, visibleRect)
                    : null;

                Rect? cameraRevealRect = _renderViewState.ComputeCameraRevealRect(visibleRect);

                // Terrain-change and camera-reveal need full 4-channel aux rebuild
                // (R=heat, G=SDF, B=material class, A=scorch level).
                Rect? fullAuxRect = RenderViewState.MergeDirtyRects(terrainDirtyRect, cameraRevealRect);

                // The build pads the rect by the blur margin; upload exactly what it wrote.
                if (fullAuxRect is Rect fullRect)
                {
                    Rect written = fullRect;
                    ProfileSection(ref _drawProfile.AuxTerrainPack, () =>
                        written = _terrainAuxBuilder.BuildGpuTerrainAuxRect(
                            _world.Terrain, _gpuTerrainAux, _gpuMaterialCoverage, fullRect));
                    fullAuxRect = written;
                }
                coverageDirtyRect = fullAuxRect;

                // Heat-only R-channel fast path for the remaining visible heat area
                Rect? heatOnlyRect = heatDirtyRect;
                if (heatOnlyRect is not null && fullAuxRect is not null)
                    heatOnlyRect = RenderViewState.SubtractCoveredHeatRect(heatOnlyRect, fullAuxRect);

                if (heatOnlyRect is Rect heatRect)
                    ProfileSection(ref _drawProfile.AuxHeatPack, () =>
                        TerrainAuxBuilder.UpdateGpuTerrainAuxHeatOnly(_world.Terrain, _gpuTerrainAux, heatRect));

                auxDirtyRect = RenderViewState.MergeDirtyRects(fullAuxRect, heatOnlyRect);
                consumeHeatDirty = includeHeatAux;
            }
            _renderViewState.UpdateLastAuxViewport(visibleRect);
            _terrainDirtyCells.Clear();

            _drawProfile.ScreenHiResTerrain += hiResWatch.Elapsed;
            double terrainMs = hiResWatch.Elapsed.TotalMilliseconds;

            hiResWatch.Restart();
            int tankHeatGlowCount = 0;
            ProfileSection(ref _drawProfile.ScreenTankGlowBuild, () =>
                tankHeatGlowCount = _terrainAuxBuilder.BuildGpuTankHeatGlowData(
                    _world.TankList.Tanks, _entityLayer.InterpolateTankPositions(stepProgress), renderView, _gpuTankHeatGlow));

            _drawProfile.ScreenHiResEntities += hiResWatch.Elapsed;

            hiResWatch.Restart();
            var upload = new GamePixelsUpload(
                Pixels: Array.Empty<uint>(),
                View: renderView,
                PostProcess: new PostProcessUploadOptions(
                    Quality: HiResRenderQuality.High,
                    HeatDebugOverlayEnabled: _commandController.ShowHeatDebugOverlay,
                    PassFlags: _commandController.EnabledPostPasses,
                    ThermalRegions: BuildThermalRegionOverlayUpload(),
                    TimeSeconds: visualTime),
                TankGlow: new TankGlowUpload(
                    Data: _gpuTankHeatGlow,
                    Count: tankHeatGlowCount),
                TerrainAux: new TerrainAuxUpload(
                    Data: _gpuTerrainAux,
                    DirtyRect: auxDirtyRect,
                    MaterialCoverage: _gpuMaterialCoverage,
                    MaterialCoverageDirtyRect: coverageDirtyRect),
                NativeContinuous: new NativeContinuousUpload(
                    Enabled: true,
                    SourcePixels: _compositePixels,
                    SourceChanged: _compositeChanged,
                    SampleCount: _renderViewState.NativeContinuousSampleCount),
                Entities: new EntityLayerUpload(
                    Vertices: _entityLayer.Vertices,
                    VertexCount: _entityLayer.VertexCount));
            ProfileSection(ref _drawProfile.ScreenBackendUpload, () =>
                _renderBackend.UploadGamePixels(upload));
            _compositeChanged = false;
            if (consumeHeatDirty)
                _world.Terrain.ClearHeatDirtyRect();
            _gpuAuxFullUploadPending = false;
            _drawProfile.ScreenUpload += hiResWatch.Elapsed;
            double uploadMs = hiResWatch.Elapsed.TotalMilliseconds;

            ProfileSection(ref _drawProfile.ScreenQualityAdjust, () =>
            {
                _renderViewState.UpdateNativeContinuousQuality(terrainMs + uploadMs);
            });
        });

        ProfileSection(ref _drawProfile.ScreenClearFrame, () =>
            _renderBackend.ClearFrame(new Size(winW, winH), new Tunnerer.Core.Types.Color(26, 26, 26, 255)));

        var imguiWatch = Stopwatch.StartNew();
        if (_renderBackend.SupportsUi)
        {
            ProfileSection(ref _drawProfile.ScreenNewFrame, () =>
                _renderBackend.NewFrame(winW, winH, dt));

            if (_terrainTestMode)
            {
                var gameTextureSize = _renderBackend.GameTextureSize;
                ProfileSection(ref _drawProfile.ScreenHudDraw, () =>
                    _hud.DrawTerrainTest(
                        _renderBackend.GameTextureId,
                        gameTextureSize.X,
                        gameTextureSize.Y,
                        _commandController.ShowHeatDebugOverlay,
                        _commandController.EnabledPostPasses,
                        requestScreenshot: label => _renderBackend.RequestScreenshot(label),
                        executeCommand: (cmd) => ExecuteGameCommand(cmd, InputCommandSources.Input)));
            }
            else if (player != null)
            {
                float vpW = _renderViewState.HiResSize.X;
                float vpH = _renderViewState.HiResSize.Y;
                if (vpW > 0 && vpH > 0 &&
                    frameInput.MouseX >= 0 && frameInput.MouseY >= 0 &&
                    frameInput.MouseX < vpW && frameInput.MouseY < vpH)
                {
                    _hud.CrosshairScreenPos = (frameInput.MouseX, frameInput.MouseY);
                }
                else
                    _hud.CrosshairScreenPos = null;

                _hud.Camera = (_renderViewState.CamPixelX, _renderViewState.CamPixelY, DesktopScreenTweaks.PixelScale);
                var gameTextureSize = _renderBackend.GameTextureSize;
                ProfileSection(ref _drawProfile.ScreenHudDraw, () =>
                    _hud.Draw(
                        _renderBackend.GameTextureId,
                        gameTextureSize.X,
                        gameTextureSize.Y,
                        player,
                        _world,
                        dt,
                        _commandController.ShowHeatDebugOverlay,
                        _commandController.EnabledPostPasses,
                        _commandController.ShowPostPassOverlay));
                _renderBackend.SetHeatOrb(_hud.HeatOrb);
                DrawSessionOverlay(dt);
            }

        }

        _drawProfile.ScreenUi += imguiWatch.Elapsed;
        imguiWatch.Restart();
        _renderBackend.Render();
        _drawProfile.ScreenImGuiRender += imguiWatch.Elapsed;
    }

    private ThermalRegionOverlayUpload BuildThermalRegionOverlayUpload()
    {
        if (!_world.Terrain.TryGetThermalTileInfo(out int tileSize, out _, out _))
            return default;

        float threshold01 = _world.Settings.ThermalActiveTemperatureThreshold / (TerrainGrid.HeatByteScale * 255f);
        return new ThermalRegionOverlayUpload(
            TileSizeCells: Math.Max(1, tileSize),
            ActiveThresholdHeat01: Math.Clamp(threshold01, 0f, 1f));
    }
}
