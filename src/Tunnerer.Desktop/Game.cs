namespace Tunnerer.Desktop;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using static SDL3.SDL;
using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Input;
using Tunnerer.Core.LevelGen;
using Tunnerer.Core.Sensing;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Audio;
using Tunnerer.Desktop.Gui;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Input;
using Tunnerer.Desktop.Rendering;
using Tunnerer.Desktop.Rendering.SdlGpu;
using System.Diagnostics;
using System.Runtime.InteropServices;

public partial class Game : IDisposable
{
    private readonly SdlRenderer _renderer;
    [SuppressMessage("Performance", "CA1859", Justification = "Game talks to the backend through its interface, not SDL_GPU types.")]
    private readonly IGameRenderBackend _renderBackend;
    [SuppressMessage("Performance", "CA1859", Justification = "Game talks to the backend through its interface, not SDL_GPU types.")]
    private readonly ITextureLoader _textures;
    private readonly GameHud _hud;
    private readonly WorldCompositeRenderer _compositeRenderer = new();
    private readonly EntityLayerBuilder _entityLayer = new();
    private readonly GameAudio _audio;

    private readonly Size _terrainSize;
    private readonly World _world;
    private readonly uint[] _worldPixels;
    private readonly uint[] _compositePixels;
    private readonly KeyboardController _p1Controller;
    private readonly BotTankAI _p2AI;
    private readonly ScriptedInputConfig _scriptedInput;
    private readonly InputRecorder _inputRecorder = new();
    private readonly GameSimulationStepper _simulationStepper;
    private readonly RenderViewState _renderViewState = new();
    private readonly TerrainAuxBuilder _terrainAuxBuilder = new();
    private readonly GameCommandController _commandController = new();
    private readonly float[] _gpuTankHeatGlow = new float[Tweaks.World.MaxPlayers * 4];
    private readonly TerrainAuxUploader _terrainAuxUploader;
    /// <summary>The player's vision and the fog texture built from it; both null when fog of war is off.</summary>
    private readonly VisionMap? _vision;
    private readonly FogLayer? _fogLayer;
    /// <summary>What the renderer draws the terrain from: the world, or with fog what the player last saw of it.</summary>
    private readonly TerrainView _terrainView;
    private bool _compositeChanged = true;
    private bool _steppedSinceRender = true;
    private readonly Stopwatch _renderClock = Stopwatch.StartNew();
    private readonly bool _interpolateFrames;
    private readonly VisualClock _visualClock;
    private float VisualTime => _visualClock.SecondsAt(_simFrameCounter);

    public const int DefaultSeed = 42;

    private readonly DrawProfile _drawProfile = new();
    private readonly PerfCaptureSession _perfSession;
    private readonly bool _terrainTestMode;
    private bool _isRunning = true;
    private int _simFrameCounter;
    private const int MaxCatchUpSimulationSteps = 4;

    public unsafe Game(
        Size? terrainSizeOverride = null,
        LevelGenMode genMode = LevelGenMode.Deterministic,
        PerfCaptureOptions? perfCapture = null,
        bool enableAiTanks = true,
        bool terrainTestMode = false,
        bool fogOfWar = true)
    {
        _terrainTestMode = terrainTestMode;
        _automatedRun = IsAutomatedRun(perfCapture);
        // Automated runs display exactly one frame per simulation step so their screenshots and timings are
        // reproducible; TUNNERER_INTERPOLATE=1 turns the in-between frames back on for them.
        _interpolateFrames = !_automatedRun ||
            string.Equals(Environment.GetEnvironmentVariable("TUNNERER_INTERPOLATE"), "1", StringComparison.Ordinal);
        _visualClock = _automatedRun ? VisualClock.StepLocked(Tweaks.World.AdvanceStep) : VisualClock.RealTime();
        Seed = ResolveSeed(IsInteractiveSession);
        var windowSize = DesktopScreenTweaks.WindowSize;
        _perfSession = PerfCaptureSession.Create(perfCapture);

        _renderer = new SdlRenderer(Tweaks.System.WindowTitle + (terrainTestMode ? " [Terrain Test]" : ""), windowSize);

        var sdlGpuBackend = new SdlGpuBackend(_renderer.NativeWindow);
        _renderBackend = sdlGpuBackend;
        _textures = new SdlGpuTextureLoader(sdlGpuBackend);
        _hud = new GameHud();
        LoadHudSprites();
        // Only a person playing a match hears sound.
        _audio = GameAudio.Create(!IsInteractiveSession);

        ReplayArguments = genMode == LevelGenMode.Optimized
            ? $"--fast --seed={Seed.ToString(CultureInfo.InvariantCulture)}"
            : $"--seed={Seed.ToString(CultureInfo.InvariantCulture)}";
        var built = WorldFactory.Build(new WorldLaunchOptions(terrainTestMode, terrainSizeOverride, genMode, Seed));
        _world = built.World;
        _terrainSize = built.TerrainSize;
        _p2AI = new BotTankAI(seed: built.BotSeed);
        _entityLayer.CaptureStep(_world);
        // The player is the first tank; the terrain test has none, and so no fog.
        if (fogOfWar && _world.TankList.Count > 0)
        {
            _vision = _world.TrackVision(_world.TankList.Tanks[0]);
            _fogLayer = new FogLayer(_terrainSize);
        }
        _terrainView = _vision != null ? TerrainView.Fogged(_world.Terrain, _vision) : TerrainView.Live(_world.Terrain);

        _worldPixels = new uint[_terrainSize.Area];
        _compositePixels = new uint[_terrainSize.Area];
        _terrainAuxUploader = new TerrainAuxUploader(_terrainSize, _terrainAuxBuilder, _drawProfile);
        if (built.Parallel)
            _world.Terrain.DrawAllToSurfaceParallel(_worldPixels);
        else
            _world.Terrain.DrawAllToSurface(_worldPixels);

        _p1Controller = new KeyboardController(new KeyBindings(
            Left: Scancode.A, Right: Scancode.D,
            Up: Scancode.W, Down: Scancode.S,
            Shoot: Scancode.Space));
        _scriptedInput = ScriptedInputConfig.FromEnvironment();
        // The terrain test has no match: no player input to script or record, and no enemy tank.
        bool aiTanks = !terrainTestMode && StartMatchInput(enableAiTanks);
        _simulationStepper = new GameSimulationStepper(
            world: _world,
            p1Controller: _p1Controller,
            p2AI: _p2AI,
            enableAiTanks: aiTanks,
            scriptedInput: _scriptedInput,
            inputRecorder: _inputRecorder,
            executeCommand: ExecuteGameCommand,
            requestScreenshot: label => _renderBackend.RequestScreenshot(label));
        Console.WriteLine("[Render] TerrainVisual=NativeContinuous");
    }

    public void Run()
    {
        try
        {
            var targetFrameTime = Tweaks.World.AdvanceStep;
            var frameCoordinator = new GameFrameCoordinator(
                targetFrameTime,
                MaxCatchUpSimulationSteps,
                renderBetweenSteps: _interpolateFrames,
                minRenderInterval: TimeSpan.FromSeconds(1.0 / DesktopScreenTweaks.MaxRenderFps));
            var tanks = _world.TankList.Tanks;
            // The terrain test runs no simulation: WASD pans the camera and only scripted commands advance.
            Action updateView = _terrainTestMode ? UpdateTerrainTestCamera : () => UpdateMatchCamera(tanks);
            Action<FrameInputSnapshot> advanceStep = _terrainTestMode
                ? _ => AdvanceScriptClock()
                : frameInput => AdvanceOneSimulationStep(tanks, frameInput);

            frameCoordinator.Run(
                isRunning: () => _isRunning,
                requestStop: () => _isRunning = false,
                pollEvents: () => _renderer.PollEvents(HandleEvent),
                isPaused: () => _paused,
                onPausedFrame: AdvancePausedFrame,
                onBeforeSimulationBatch: () =>
                {
                    _terrainAuxUploader.ClearDirtyCells();
                    _audio.SetPaused(_paused);
                    updateView();
                },
                captureFrameInput: CaptureFrameInput,
                advanceOneSimulationStep: advanceStep,
                composeFrame: () =>
                {
                    DrawProfile.Measure(ref _drawProfile.ObjectsDraw, () =>
                    {
                        _compositeRenderer.Compose(_world, _worldPixels, _compositePixels);
                        MarkEntityPixels(_worldPixels, _compositePixels);
                        _compositeChanged = true;
                    });
                },
                renderFrame: (frameInput, stepProgress) => RenderImGuiFrame(tanks, frameInput, stepProgress),
                onFrameMeasured: elapsed =>
                {
                    _drawProfile.TotalFrame += elapsed;
                    _drawProfile.FrameCount++;
                    if (_drawProfile.FrameCount >= 100)
                        _drawProfile.Report();

                    if (_perfSession.Capture(elapsed))
                        _isRunning = false;
                });

            _perfSession.ReportIfEnabled();
        }
        finally
        {
            FlushInputRecordingOnExit();
            DisposeResources();
        }
    }

    /// <summary>A match draws its HUD panels beside the game view; the terrain test gives the view the whole window.</summary>
    private bool ReservesUiViewport => _renderBackend.SupportsUi && !_terrainTestMode;

    /// <summary>
    /// Sizes the game view for this frame, in device pixels, and takes the display's pixel density. Call it before
    /// reading <see cref="RenderViewState.CellPixels"/>.
    /// </summary>
    private Size EnsureViewSize()
    {
        _renderViewState.SetPixelDensity(_renderer.PixelDensity);
        var (pointsW, pointsH) = _renderer.GetWindowSize();
        var (pixelsW, pixelsH) = _renderer.GetWindowSizeInPixels();
        var viewSize = _renderViewState.ComputeViewSize(
            new Size(pixelsW, pixelsH), new Size(pointsW, pointsH), ReservesUiViewport);
        _renderViewState.EnsureHiResBuffer(viewSize);
        return viewSize;
    }

    private void UpdateMatchCamera(IReadOnlyList<Core.Entities.Tank> tanks)
    {
        var viewSize = EnsureViewSize();
        int scale = _renderViewState.CellPixels;
        PositionF? player = tanks.Count > 0 ? new PositionF(tanks[0].Position.X, tanks[0].Position.Y) : null;
        _renderViewState.UpdateCamera(player, _terrainSize, viewSize.X, viewSize.Y, scale);
    }

    private void UpdateTerrainTestCamera()
    {
        var viewSize = EnsureViewSize();
        int scale = _renderViewState.CellPixels;

        // WASD panning: read keyboard state directly through the controller's held keys.
        // Four points a tick, in device pixels: scaled by density so the pan keeps its speed on a Retina display.
        int panSpeed = Math.Max(1, (int)MathF.Round(4f * _renderViewState.PixelDensity));
        int dx = 0, dy = 0;
        if (_p1Controller.IsLeftHeld) dx -= panSpeed;
        if (_p1Controller.IsRightHeld) dx += panSpeed;
        if (_p1Controller.IsUpHeld) dy -= panSpeed;
        if (_p1Controller.IsDownHeld) dy += panSpeed;
        _renderViewState.PanCamera(dx, dy, _terrainSize, viewSize.X, viewSize.Y, scale);
    }

    private void AdvanceOneSimulationStep(IReadOnlyList<Core.Entities.Tank> tanks, FrameInputSnapshot frameInput)
    {
        var aimDir = ComputeAimDirection(tanks, frameInput);
        bool mouseShoot = IsMouseInViewport(frameInput, out _, out _) && frameInput.IsLeftMouseDown;
        _simulationStepper.AdvanceOneStep(
            simFrame: _simFrameCounter,
            tanks: tanks,
            aimDirection: aimDir,
            mouseShoot: mouseShoot);
        _simFrameCounter++;
        _steppedSinceRender = true;
        _entityLayer.CaptureStep(_world);

        // With fog the renderer follows what the player knows of the terrain, not the terrain: a change out of
        // sight is drawn when the player next sees the cell.
        var changedCells = _vision?.KnownChanges ?? _world.Terrain.GetChangeList();
        _audio.AfterStep(_world);
        _terrainAuxUploader.ObserveTerrainChanges(_terrainView.Pixels, changedCells, VisualTime);

        DrawProfile.Measure(ref _drawProfile.TerrainDraw, DrawTerrainChangesToSurface);
    }

    private void DrawTerrainChangesToSurface()
    {
        if (_vision == null)
        {
            _world.Terrain.DrawChangesToSurface(_worldPixels);
            return;
        }

        int width = _terrainSize.X;
        foreach (Position cell in _vision.KnownChanges)
            _worldPixels[cell.X + cell.Y * width] = Pixel.GetColor(_vision.Known.GetPixelRaw(cell)).ToArgb();
        _world.Terrain.ClearChangeList();
    }

    private void LoadHudSprites()
    {
        string baseDir = Path.Combine(AppContext.BaseDirectory, "resources", "hud");
        nint Load(string name) => _textures.LoadTexture(Path.Combine(baseDir, name));

        _hud.Init(
            heatIcon: Load("heat_icon.png"),
            shieldIcon: Load("shield_icon.png"),
            panelFrame: Load("panel_frame.png"),
            buildPanel: Load("build_panel.png"));
    }

    public void Dispose()
    {
        DisposeResources();
        GC.SuppressFinalize(this);
    }

    private void DisposeResources()
    {
        // Before the renderer: it shuts SDL down.
        _audio.Dispose();
        _textures.Dispose();
        _renderBackend.Dispose();
        _renderer.Dispose();
    }

    private static void MarkEntityPixels(uint[] terrain, uint[] composite)
    {
        for (int i = 0; i < composite.Length; i++)
        {
            if (composite[i] != terrain[i])
                composite[i] = RenderingPixels.MarkEntity(composite[i]);
        }
    }

}
