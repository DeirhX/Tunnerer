namespace Emberward.Desktop;

using Emberward.Core.Config;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using Emberward.Desktop.Config;
using Emberward.Desktop.Gui;
using Emberward.Desktop.Input;
using Emberward.Desktop.Rendering;
using Emberward.Core.Entities.Tanks;
using System.Diagnostics;

public partial class Game
{
    /// <param name="stepProgress">
    /// Where this frame lies between the previous simulation step (0) and the latest (1); moving entities and the
    /// camera are drawn at that point.
    /// </param>
    private void RenderImGuiFrame(
        IReadOnlyList<Tank> tanks, in FrameInputSnapshot frameInput, float stepProgress)
    {
        var (winW, winH) = _renderer.GetWindowSize();
        float dt = _interpolateFrames
            ? Math.Clamp((float)_renderClock.Elapsed.TotalSeconds, 0.001f, 0.1f)
            : 1f / Tweaks.Perf.TargetFps;
        _renderClock.Restart();
        var viewSize = EnsureViewSize();
        int scale = _renderViewState.CellPixels;

        var player = LocalTank;
        // A world that takes no step (paused, or waiting on the network) is drawn where it stands, not moving ahead.
        if (WorldStandsStill)
            _entityLayer.Hold();
        ReadOnlySpan<PositionF> drawnTankPositions = _entityLayer.TankPositions(stepProgress);
        // The camera follows the local tank the same way the shader draws it: from where it was last drawn, along its motion.
        if (_localSlot < drawnTankPositions.Length)
        {
            var (tankStart, tankMotion) = _entityLayer.TankMotion(_localSlot);
            _renderViewState.UpdateCamera(tankStart, tankMotion, stepProgress, _terrainSize, viewSize.X, viewSize.Y, scale);
        }
        else
        {
            _renderViewState.UpdateCamera(null, _terrainSize, viewSize.X, viewSize.Y, scale);
        }
        var renderView = new RenderView(
            ViewSize: _renderViewState.HiResSize,
            WorldSize: _terrainSize,
            CameraPixels: new Position(_renderViewState.CamPixelX, _renderViewState.CamPixelY),
            PixelScale: scale,
            CameraMotion: _renderViewState.CamMotion,
            StepAlpha: stepProgress);
        float visualTime = VisualTime;
        _entityLayer.Build(
            renderView, stepProgress, turnSeconds: WorldStandsStill ? 0f : dt, snapHeadings: !_interpolateFrames,
            visualSeconds: visualTime, vision: _vision, radar: _radar,
            powerLabelCellSize: _commandController.ShowPowerLabels && !InNetLobby ? scale / ImGuiPlatform.DeviceScale : 0f);
        ScreenEffectLevels screenEffects = _screenEffects.Update(_terrainTestMode ? null : player, WorldStandsStill ? 0f : dt);
        bool steppedSinceRender = _steppedSinceRender;
        _steppedSinceRender = false;

        DrawProfile.Measure(ref _drawProfile.ScreenDraw, () =>
        {
            var hiResWatch = Stopwatch.StartNew();

            Rect visibleRect = renderView.VisibleCells();
            Rect? sightChanged = null;
            FogUpload fog = _fogLayer != null && _vision != null ? _fogLayer.Prepare(_vision, out sightChanged) : default;
            TerrainAuxFrame terrainAux = _terrainAuxUploader.Prepare(
                _terrainView, visibleRect, steppedSinceRender, visualTime, sightChanged);

            _drawProfile.ScreenHiResTerrain += hiResWatch.Elapsed;
            double terrainMs = hiResWatch.Elapsed.TotalMilliseconds;

            hiResWatch.Restart();
            int tankHeatGlowCount = 0;
            DrawProfile.Measure(ref _drawProfile.ScreenTankGlowBuild, () =>
                tankHeatGlowCount = _terrainAuxBuilder.BuildGpuTankHeatGlowData(
                    _world.TankList.Tanks, _entityLayer.TankPositions(stepProgress), renderView, _gpuTankHeatGlow, _vision));

            _drawProfile.ScreenHiResEntities += hiResWatch.Elapsed;

            hiResWatch.Restart();
            var upload = new GamePixelsUpload(
                View: renderView,
                PostProcess: new PostProcessUploadOptions(
                    HeatDebugOverlayEnabled: _commandController.ShowHeatDebugOverlay,
                    PassFlags: _commandController.EnabledPostPasses,
                    ThermalRegions: BuildThermalRegionOverlayUpload(),
                    TimeSeconds: visualTime),
                TankGlow: new TankGlowUpload(
                    Data: _gpuTankHeatGlow,
                    Count: tankHeatGlowCount),
                TerrainAux: terrainAux.Upload,
                NativeContinuous: new NativeContinuousUpload(
                    SourcePixels: _worldPixels,
                    SourceChanged: _worldPixelsChanged,
                    SampleCount: _renderViewState.NativeContinuousSampleCount),
                Entities: new EntityLayerUpload(
                    Vertices: _entityLayer.Vertices,
                    VertexCount: _entityLayer.VertexCount),
                Fog: fog,
                ScreenEffects: screenEffects);
            DrawProfile.Measure(ref _drawProfile.ScreenBackendUpload, () =>
                _renderBackend.UploadGamePixels(upload));
            _worldPixelsChanged = false;
            _terrainAuxUploader.Commit(_world.Terrain, terrainAux);
            _drawProfile.ScreenUpload += hiResWatch.Elapsed;
            double uploadMs = hiResWatch.Elapsed.TotalMilliseconds;

            DrawProfile.Measure(ref _drawProfile.ScreenQualityAdjust, () =>
            {
                _renderViewState.UpdateNativeContinuousQuality(terrainMs + uploadMs);
            });
        });

        DrawProfile.Measure(ref _drawProfile.ScreenClearFrame, () =>
            _renderBackend.ClearFrame(new Size(winW, winH), new Emberward.Core.Types.Color(26, 26, 26, 255)));

        var imguiWatch = Stopwatch.StartNew();
        if (_renderBackend.SupportsUi)
        {
            DrawProfile.Measure(ref _drawProfile.ScreenNewFrame, () =>
                _renderBackend.NewFrame(winW, winH, dt));

            if (_terrainTestMode)
            {
                var gameTextureSize = _renderBackend.GameTextureSize;
                DrawProfile.Measure(ref _drawProfile.ScreenHudDraw, () =>
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
                _hud.CrosshairScreenPos = IsMouseInViewport(frameInput, out float mouseX, out float mouseY)
                    // In device pixels: during a hit's shake the crosshair moves with what the mouse aims at.
                    ? screenEffects.ShownInView(mouseX, mouseY, renderView.ViewSize.X, renderView.ViewSize.Y)
                    : null;

                _hud.Camera = (_renderViewState.CamPixelX, _renderViewState.CamPixelY, _renderViewState.CellPixels);
                var gameTextureSize = _renderBackend.GameTextureSize;
                _hud.WaitingForPlayers = InNetLobby;
                DrawProfile.Measure(ref _drawProfile.ScreenHudDraw, () =>
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
                // The panel closes by itself (out of reach, a level bought); the number keys follow it.
                _localController.MachineUpgradeMode = _hud.MachineUpgrades.IsOpen;
                _renderBackend.SetHeatOrb(_hud.HeatOrb);
                DrawSessionOverlay(dt);
                DrawNetOverlays(drawnTankPositions);
            }

        }

        _drawProfile.ScreenUi += imguiWatch.Elapsed;
        imguiWatch.Restart();
        _renderBackend.Render();
        _drawProfile.ScreenImGuiRender += imguiWatch.Elapsed;
    }

    /// <summary>
    /// The thermal debug overlay (Shift+2) draws the solver's tile grid and tints the tiles that hold warm ground.
    /// It works from the heat texture, so it shows where heat is, not which tiles the solver is stepping: a pocket
    /// that has settled is warm and drawn, yet costs nothing.
    /// </summary>
    private ThermalRegionOverlayUpload BuildThermalRegionOverlayUpload()
    {
        _world.Terrain.GetThermalTileInfo(out int tileSize, out _, out _);
        float threshold01 = DesktopScreenTweaks.ThermalOverlayWarmTemperature / (TerrainGrid.HeatByteScale * 255f);
        return new ThermalRegionOverlayUpload(
            TileSizeCells: Math.Max(1, tileSize),
            ActiveThresholdHeat01: Math.Clamp(threshold01, 0f, 1f));
    }
}
