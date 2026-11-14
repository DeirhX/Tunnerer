namespace Tunnerer.Desktop;

using Silk.NET.SDL;
using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Input;
using Tunnerer.Core.LevelGen;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Gui;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Input;
using Tunnerer.Desktop.Rendering;
using System.Diagnostics;
using System.Runtime.InteropServices;

public partial class Game : IDisposable
{
    private readonly SdlRenderer _renderer;
    private readonly IGameRenderBackend _renderBackend;
    private readonly ITextureLoader _textures;
    private readonly GameHud _hud;
    private readonly WorldCompositeRenderer _compositeRenderer = new();

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
    private readonly List<Position> _terrainDirtyCells = new();
    private readonly float[] _gpuTankHeatGlow = new float[Tweaks.World.MaxPlayers * 4];
    private readonly byte[] _gpuTerrainAux;
    private bool _gpuAuxFullUploadPending = true;
    private int _heatAuxFrameCounter;
    private readonly Stopwatch _gameTimer = Stopwatch.StartNew();

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
        RenderBackendKind? renderBackendOverride = null,
        bool enableAiTanks = true,
        bool terrainTestMode = false)
    {
        _terrainTestMode = terrainTestMode;
        _automatedRun = IsAutomatedRun(perfCapture);
        Seed = ResolveSeed(_automatedRun, terrainTestMode);
        var windowSize = DesktopScreenTweaks.WindowSize;
        RenderBackendKind selectedBackend = renderBackendOverride ?? DesktopTweaks.DefaultRenderBackend;
        bool deterministicSimulation = genMode == LevelGenMode.Deterministic;
        bool parallel = genMode == LevelGenMode.Optimized;
        _perfSession = PerfCaptureSession.Create(perfCapture);

        _renderer = new SdlRenderer(Tweaks.System.WindowTitle + (terrainTestMode ? " [Terrain Test]" : ""), windowSize);

        var renderServices = RenderBackendFactory.CreateServices(
            selectedBackend, _renderer.Sdl, _renderer.NativeWindow);
        _renderBackend = renderServices.Backend;
        _textures = renderServices.Textures;
        Console.WriteLine($"[Render] Backend={selectedBackend}");
        _hud = new GameHud();
        LoadHudSprites();

        if (terrainTestMode)
        {
            _terrainSize = terrainSizeOverride ?? TerrainTestPatternGenerator.DefaultSize;
            Console.WriteLine($"[TerrainTest] Generating test patterns {_terrainSize.X}x{_terrainSize.Y}...");
            var testTerrain = TerrainTestPatternGenerator.Generate(_terrainSize);

            _world = new World(_terrainSize, deterministicSimulation: true, simulationSeed: DefaultSeed,
                enableTerrainRegrowth: false);
            _world.Profile.ReportSink = static line => Console.WriteLine(line);
            // Copy pixels directly -- test patterns use final pixel values, skip materialization.
            for (int i = 0; i < testTerrain.Size.Area; i++)
            {
                _world.Terrain[i] = testTerrain[i];
                // Propagate scorch intensity from the pattern grid.
                _world.Terrain.SetScorchIntensity(i, testTerrain.GetScorchIntensity(i));
            }
            Console.WriteLine("[TerrainTest] Static terrain loaded. No simulation, WASD to pan camera.");
        }
        else
        {
            _terrainSize = terrainSizeOverride ?? DesktopScreenTweaks.RenderSurfaceSize;
            var modeLabel = parallel ? "optimized" : "deterministic";
            Console.WriteLine($"Generating terrain {_terrainSize.X}x{_terrainSize.Y} ({modeLabel}, seed={(deterministicSimulation ? Seed.ToString() : "random")})...");
            var genWatch = Stopwatch.StartNew();
            var generator = new ToastGenerator();
            int? seed = deterministicSimulation ? Seed : null;
            var (terrain, spawns) = generator.Generate(_terrainSize, seed, genMode);
            Console.WriteLine($"Level generated in {genWatch.Elapsed.TotalMilliseconds:F0} ms, {spawns.Length} spawns");

            _world = new World(
                _terrainSize,
                deterministicSimulation: deterministicSimulation,
                simulationSeed: deterministicSimulation ? Seed : 0,
                hotRockPocketCount: Tweaks.World.HotRockPocketCount);
            _world.Profile.ReportSink = static line => Console.WriteLine(line);
            int? decorationSeed = deterministicSimulation ? Seed + 1 : null;
            _world.Initialize(terrain, spawns, decorationSeed: decorationSeed, parallelMaterialize: parallel);
            if (_world.HotRockPockets.Count > 0)
                Console.WriteLine($"[HotRock] {_world.HotRockPockets.Count} pocket(s) at " +
                    string.Join(", ", _world.HotRockPockets.Select(p => $"({p.X},{p.Y})")));
        }

        _p2AI = new BotTankAI(seed: deterministicSimulation ? Seed + 2 : null);

        _worldPixels = new uint[_terrainSize.Area];
        _compositePixels = new uint[_terrainSize.Area];
        _gpuTerrainAux = new byte[_terrainSize.Area * 4];
        if (parallel)
            _world.Terrain.DrawAllToSurfaceParallel(_worldPixels);
        else
            _world.Terrain.DrawAllToSurface(_worldPixels);

        _p1Controller = new KeyboardController(_renderer.Sdl, new KeyBindings(
            Left: Scancode.ScancodeA, Right: Scancode.ScancodeD,
            Up: Scancode.ScancodeW, Down: Scancode.ScancodeS,
            Shoot: Scancode.ScancodeSpace,
            Build: Scancode.ScancodeE));
        _scriptedInput = ScriptedInputConfig.FromEnvironment();
        if (!terrainTestMode)
        {
            if (_scriptedInput.HasScriptedController())
                Console.WriteLine("[Input] Scripted controller enabled via TUNNERER_SCRIPTED_INPUT.");
            if (_scriptedInput.ScreenshotFrameCount > 0)
                Console.WriteLine($"[Input] Scripted screenshot frames enabled ({_scriptedInput.ScreenshotFrameCount} frame(s)).");
            if (_scriptedInput.CommandFrameCount > 0)
                Console.WriteLine($"[Input] Scripted commands enabled via TUNNERER_COMMAND_SCRIPT ({_scriptedInput.CommandFrameCount} frame slot(s)).");
            if (_scriptedInput.RecordPath is not null)
                Console.WriteLine($"[Input] Scripted input recording path: {_scriptedInput.RecordPath}");
            if (_scriptedInput.RecordAutoStart)
            {
                Console.WriteLine("[Input] Scripted input recording autostart enabled.");
                _inputRecorder.Start();
            }
        }
        bool effectiveEnableAiTanks = !terrainTestMode && enableAiTanks &&
            (!_scriptedInput.HasScriptedController() || _scriptedInput.KeepAi);
        if (enableAiTanks && !effectiveEnableAiTanks && !terrainTestMode)
            Console.WriteLine("[Input] AI tanks forced OFF during scripted replay.");
        _simulationStepper = new GameSimulationStepper(
            world: _world,
            p1Controller: _p1Controller,
            p2AI: _p2AI,
            enableAiTanks: effectiveEnableAiTanks,
            scriptedInput: _scriptedInput,
            inputRecorder: _inputRecorder,
            executeCommand: ExecuteGameCommand,
            requestScreenshot: label => _renderBackend.RequestScreenshot(label));
        if (!terrainTestMode)
            Console.WriteLine($"[Input] AI tanks: {(effectiveEnableAiTanks ? "enabled" : "disabled")}");
        Console.WriteLine("[Render] TerrainVisual=NativeContinuous");
    }

    public void Run()
    {
        try
        {
            var targetFrameTime = Tweaks.World.AdvanceStep;
            var frameCoordinator = new GameFrameCoordinator(targetFrameTime, MaxCatchUpSimulationSteps);
            var tanks = _world.TankList.Tanks;

            frameCoordinator.Run(
                isRunning: () => _isRunning,
                requestStop: () => _isRunning = false,
                pollEvents: () => _renderer.PollEvents(HandleEvent),
                isGameOver: () => _terrainTestMode ? false : _world.IsGameOver,
                isPaused: () => _paused,
                onPausedFrame: AdvancePausedFrame,
                onBeforeSimulationBatch: () =>
                {
                    _terrainDirtyCells.Clear();
                    if (_terrainTestMode)
                        UpdateTerrainTestCamera();
                    else
                        UpdateViewStateForInput(tanks);
                },
                captureFrameInput: CaptureFrameInput,
                advanceOneSimulationStep: frameInput =>
                {
                    if (_terrainTestMode)
                    {
                        _simulationStepper.ProcessScriptedCommands(_simFrameCounter);
                        _simFrameCounter++;
                    }
                    else
                        AdvanceOneSimulationStep(tanks, frameInput);
                },
                composeFrame: () =>
                {
                    ProfileSection(ref _drawProfile.ObjectsDraw, () =>
                    {
                        _compositeRenderer.Compose(_world, _worldPixels, _compositePixels, tanks.Count > 0 ? tanks[0] : null);
                        MarkEntityPixels(_worldPixels, _compositePixels);
                    });
                },
                renderFrame: frameInput => RenderImGuiFrame(tanks, frameInput),
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

    private void UpdateViewStateForInput(IReadOnlyList<Core.Entities.Tank> tanks)
    {
        var (winW, winH) = _renderer.GetWindowSize();
        int scale = DesktopScreenTweaks.PixelScale;
        var viewSize = _renderViewState.ComputeViewSize(winW, winH, _renderBackend.SupportsUi);
        _renderViewState.EnsureHiResBuffer(viewSize);
        var player = tanks.Count > 0 ? tanks[0] : null;
        _renderViewState.UpdateCamera(player, _terrainSize, viewSize.X, viewSize.Y, scale);
    }

    private void UpdateTerrainTestCamera()
    {
        var (winW, winH) = _renderer.GetWindowSize();
        int scale = DesktopScreenTweaks.PixelScale;
        var viewSize = _renderViewState.ComputeViewSize(winW, winH, supportsUi: false);
        _renderViewState.EnsureHiResBuffer(viewSize);

        // WASD panning: read keyboard state directly through the controller's held keys.
        const int panSpeed = 4;
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
        SimulationStepResult stepResult = _simulationStepper.AdvanceOneStep(
            simFrame: _simFrameCounter,
            tanks: tanks,
            aimDirection: aimDir,
            mouseShoot: mouseShoot);
        _simFrameCounter++;

        var changedCells = _world.Terrain.GetChangeList();
        if (changedCells.Count > 0)
            _terrainDirtyCells.AddRange(changedCells);

        ProfileSection(ref _drawProfile.TerrainDraw,
            () => _world.Terrain.DrawChangesToSurface(_worldPixels));

        if (stepResult.IsGameOver)
            _isRunning = false;
    }

    private void LoadHudSprites()
    {
        string baseDir = Path.Combine(AppContext.BaseDirectory, "resources", "hud");
        nint Load(string name) => _textures.LoadTexture(Path.Combine(baseDir, name));

        _hud.Init(
            energyIcon: Load("energy_icon.png"),
            shieldIcon: Load("shield_icon.png"),
            panelFrame: Load("panel_frame.png"),
            buildPanel: Load("build_panel.png"),
            digitStrip: _textures.LoadTexture(Path.Combine(baseDir, "digits.png")));
    }

    public void Dispose()
    {
        DisposeResources();
        GC.SuppressFinalize(this);
    }

    private void DisposeResources()
    {
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

    private static void ProfileSection(ref TimeSpan accumulator, Action action)
    {
        var w = Stopwatch.StartNew();
        action();
        accumulator += w.Elapsed;
    }

}
