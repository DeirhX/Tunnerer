namespace Emberward.Desktop;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using static SDL3.SDL;
using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.LevelGen;
using Emberward.Core.Sensing;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using Emberward.Desktop.Audio;
using Emberward.Desktop.Gui;
using Emberward.Desktop.Config;
using Emberward.Desktop.Input;
using Emberward.Desktop.Rendering;
using Emberward.Desktop.Rendering.SdlGpu;
using Emberward.Core.Bots;
using Emberward.Core.Entities.Tanks;
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
    private readonly EntityLayerBuilder _entityLayer = new();
    private readonly ScreenEffects _screenEffects = new();
    private readonly GameAudio _audio;

    private readonly Size _terrainSize;
    private readonly World _world;
    private readonly uint[] _worldPixels;
    private readonly KeyboardController _localController;
    /// <summary>
    /// Which tank this machine's player drives: the camera, HUD, fog of war, radar and sound all follow it. Always
    /// the first tank today; a network match will hand each machine its own slot.
    /// </summary>
    private readonly int _localSlot;
    private Tank? LocalTank =>
        _localSlot < _world.TankList.Count ? _world.TankList.Tanks[_localSlot] : null;
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
    /// <summary>The player's radar, which shows blips on ground out of sight; null when fog of war is off.</summary>
    private readonly Radar? _radar;
    /// <summary>What the renderer draws the terrain from: the world, or with fog what the player last saw of it.</summary>
    private readonly TerrainView _terrainView;
    private bool _worldPixelsChanged = true;
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
        bool fogOfWar = true,
        Net.RelayConnection? relay = null,
        Net.NetMenuRequest? openNetMenu = null,
        string launchSecret = "",
        Uri? launchRelay = null)
    {
        _launchSecret = launchSecret;
        _launchRelay = launchRelay;
        _relay = relay;
        _localSlot = relay?.Slot ?? 0;
        _terrainTestMode = terrainTestMode;
        _automatedRun = IsAutomatedRun(perfCapture);
        // Only a person playing remembers anything between runs.
        _playerSettings = IsInteractiveSession ? Net.PlayerSettings.Load() : null;
        // Automated runs display exactly one frame per simulation step so their screenshots and timings are
        // reproducible; EMBERWARD_INTERPOLATE=1 turns the in-between frames back on for them.
        _interpolateFrames = !_automatedRun ||
            DesktopDisplayOptions.ParseFlag(Environment.GetEnvironmentVariable("EMBERWARD_INTERPOLATE"));
        _visualClock = _automatedRun ? VisualClock.StepLocked(Tweaks.World.AdvanceStep) : VisualClock.RealTime();
        Seed = relay?.Config.Seed ?? ResolveSeed(IsInteractiveSession);
        var windowSize = DesktopScreenTweaks.WindowSize;
        _perfSession = PerfCaptureSession.Create(perfCapture);

        _renderer = new SdlRenderer(Tweaks.System.WindowTitle + (terrainTestMode ? " [Terrain Test]" : ""), windowSize);

        var sdlGpuBackend = new SdlGpuBackend(_renderer.NativeWindow);
        _renderBackend = sdlGpuBackend;
        _textures = new SdlGpuTextureLoader(sdlGpuBackend);
        if (!DesktopDisplayOptions.Hidden)
            DrawStartupSplash();
        _hud = new GameHud();
        LoadHudSprites();
        // Only a person playing a match hears sound.
        _audio = GameAudio.Create(!IsInteractiveSession);

        ReplayArguments = genMode == LevelGenMode.Optimized
            ? $"--fast --seed={Seed.ToString(CultureInfo.InvariantCulture)}"
            : $"--seed={Seed.ToString(CultureInfo.InvariantCulture)}";
        var built = WorldFactory.Build(new WorldLaunchOptions(
            terrainTestMode, terrainSizeOverride, genMode, Seed,
            Players: relay?.Config.Slots ?? Tweaks.LevelGen.DefaultPlayers, CoreTapWins: relay != null));
        _world = built.World;
        _terrainSize = built.TerrainSize;
        _entityLayer.CaptureStep(_world, _localSlot);
        // The terrain test has no tanks, and so no fog.
        if (fogOfWar && LocalTank is { } localTank)
        {
            _vision = _world.TrackVision(localTank);
            _fogLayer = new FogLayer(_terrainSize);
            _radar = _world.TrackRadar(localTank);
        }
        _terrainView = _vision != null ? TerrainView.Fogged(_world.Terrain, _vision) : TerrainView.Live(_world.Terrain);

        _worldPixels = new uint[_terrainSize.Area];
        _terrainAuxUploader = new TerrainAuxUploader(_terrainSize, _terrainAuxBuilder, _drawProfile);
        if (built.Parallel)
            _world.Terrain.DrawAllToSurfaceParallel(_worldPixels);
        else
            _world.Terrain.DrawAllToSurface(_worldPixels);

        _localController = new KeyboardController(new KeyBindings(
            Left: Scancode.A, Right: Scancode.D,
            Up: Scancode.W, Down: Scancode.S,
            Shoot: Scancode.Space));
        _scriptedInput = ScriptedInputConfig.FromEnvironment();
        // The terrain test has no match: no player input to script or record, and no enemy tank.
        bool aiTanks = !terrainTestMode && StartMatchInput(enableAiTanks && relay == null);
        var bots = CreateBots(built, aiTanks);
        _simulationStepper = new GameSimulationStepper(
            world: _world,
            localSlot: _localSlot,
            localController: _localController,
            bots: bots,
            scriptedInput: _scriptedInput,
            inputRecorder: _inputRecorder,
            executeCommand: ExecuteGameCommand,
            requestScreenshot: label => _renderBackend.RequestScreenshot(label));
        if (relay != null)
            StartNetSession(relay);
        // A refused launch, or a player who left a network match, comes back to the menu over a paused local match.
        if (openNetMenu != null && IsInteractiveSession)
            OpenNetMenu(openNetMenu);
        Console.WriteLine("[Render] TerrainVisual=NativeContinuous");
    }

    /// <summary>One AI per tank the local player does not drive, or none at all when AI tanks are off.</summary>
    private BotTankAI?[] CreateBots(BuiltWorld built, bool aiTanks)
    {
        var bots = new BotTankAI?[_world.TankList.Count];
        for (int slot = 0; aiTanks && slot < bots.Length; slot++)
            if (slot != _localSlot)
            {
                bots[slot] = new BotTankAI(seed: built.BotSeedFor(slot));
                // Its base gets the bot's stand-in ore income.
                _world.SetBotControlled(slot, botControlled: true);
            }
        return bots;
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
                isPaused: () => SimulationPaused,
                onPausedFrame: AdvancePausedFrame,
                onBeforeSimulationBatch: () =>
                {
                    _terrainAuxUploader.ClearDirtyCells();
                    _audio.SetPaused(WorldStandsStill);
                    updateView();
                },
                captureFrameInput: CaptureFrameInput,
                advanceOneSimulationStep: advanceStep,
                composeFrame: () =>
                {
                    _worldPixelsChanged = true;
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

    private void UpdateMatchCamera(IReadOnlyList<Tank> tanks)
    {
        var viewSize = EnsureViewSize();
        int scale = _renderViewState.CellPixels;
        PositionF? player = LocalTank is { } tank ? new PositionF(tank.Position.X, tank.Position.Y) : null;
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
        if (_localController.IsLeftHeld) dx -= panSpeed;
        if (_localController.IsRightHeld) dx += panSpeed;
        if (_localController.IsUpHeld) dy -= panSpeed;
        if (_localController.IsDownHeld) dy += panSpeed;
        _renderViewState.PanCamera(dx, dy, _terrainSize, viewSize.X, viewSize.Y, scale);
    }

    private void AdvanceOneSimulationStep(IReadOnlyList<Tank> tanks, FrameInputSnapshot frameInput)
    {
        var aimDir = ComputeAimDirection(tanks, frameInput);
        bool mouseShoot = IsMouseInViewport(frameInput, out _, out _) && frameInput.IsLeftMouseDown;
        if (_relay != null && _netSession != null)
        {
            AdvanceNetSteps(_relay, _netSession, aimDir, mouseShoot);
            return;
        }

        _simulationStepper.AdvanceOneStep(
            simFrame: _simFrameCounter,
            aimDirection: aimDir,
            mouseShoot: mouseShoot);
        AfterSimulationStep();
    }

    /// <summary>What follows every simulated step: the entity snapshot, sound, and the terrain the step changed.</summary>
    /// <param name="silent">No sound or hit shake for this step: it is a tick from the past, replayed to catch up.</param>
    private void AfterSimulationStep(bool silent = false)
    {
        _simFrameCounter++;
        _steppedSinceRender = true;
        _entityLayer.CaptureStep(_world, _localSlot);

        // With fog the renderer follows what the player knows of the terrain, not the terrain: a change out of
        // sight is drawn when the player next sees the cell.
        var changedCells = _vision?.KnownChanges ?? _world.Terrain.GetChangeList();
        if (!silent)
            _audio.AfterStep(_world, _localSlot);
        var tanks = _world.TankList.Tanks;
        if (_localSlot < tanks.Count)
        {
            _hud.AfterStep(_world, tanks[_localSlot]);
            if (!silent && !_terrainTestMode)
                _screenEffects.AfterStep(_world.StepEvents, tanks[_localSlot]);
        }
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
    }

    private void LoadHudSprites()
    {
        nint Load(string name) => _textures.LoadTexture("resources/hud/" + name);

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
}
