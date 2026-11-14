namespace Tunnerer.Desktop;

using Silk.NET.SDL;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Gui;
using Tunnerer.Desktop.Input;

/// <summary>
/// Session-level behaviour around a match: pausing, the controls screen, quitting, and which map seed is used.
/// </summary>
public partial class Game
{
    // SDL_WINDOWEVENT_FOCUS_LOST
    private const byte SdlWindowEventFocusLost = 13;

    private readonly SessionOverlay _sessionOverlay = new();
    private bool _automatedRun;
    private bool _paused;
    private bool _showControls;

    /// <summary>Seed of the current map and simulation. Pass it back with --seed=N to replay the same map.</summary>
    public int Seed { get; private set; } = DefaultSeed;

    /// <summary>
    /// A person is playing: not a scripted, perf-capture, hidden or terrain-test run. Only then does Escape open the
    /// pause menu and losing window focus pause the game; automated runs must never stop and wait for input.
    /// </summary>
    private bool IsInteractiveSession => !_terrainTestMode && !_automatedRun;

    private static bool IsAutomatedRun(PerfCaptureOptions? perfCapture)
    {
        if (perfCapture is not null || DesktopDisplayOptions.Hidden)
            return true;

        string[] automationVariables =
        [
            "TUNNERER_SCRIPTED_INPUT",
            "TUNNERER_COMMAND_SCRIPT",
            "TUNNERER_SCRIPT_SCREENSHOT_FRAME",
            "TUNNERER_SCRIPT_SCREENSHOT_FRAMES",
            "TUNNERER_RECORD_INPUT_PATH",
            "TUNNERER_RECORD_INPUT_AUTOSTART",
        ];
        foreach (string name in automationVariables)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// An explicit --seed always wins. Automated runs keep the fixed <see cref="DefaultSeed"/> so replays, perf
    /// captures and screenshots stay comparable. A normal match gets a fresh map every time.
    /// </summary>
    private static int ResolveSeed(bool automatedRun, bool terrainTestMode)
    {
        if (GameLaunchOptions.Seed is int requested)
            return requested;
        if (automatedRun || terrainTestMode)
            return DefaultSeed;
        return Random.Shared.Next(1, int.MaxValue);
    }

    /// <summary>
    /// Handles Escape, P, F1 and Q, and pauses when the window loses focus. Returns true when the event was consumed.
    /// </summary>
    private bool HandleSessionEvent(Event ev)
    {
        if (ev.Type == (uint)EventType.Windowevent)
        {
            if (ev.Window.Event == SdlWindowEventFocusLost && IsInteractiveSession)
                _paused = true;
            return false;
        }

        if (ev.Type != (uint)EventType.Keydown)
            return false;

        Scancode scancode = (Scancode)ev.Key.Keysym.Scancode;
        bool isRepeat = ev.Key.Repeat != 0;

        if (!IsInteractiveSession)
        {
            // Automated and terrain-test runs keep the old behaviour: Escape closes the game at once.
            if (scancode == Scancode.ScancodeEscape)
            {
                _isRunning = false;
                return true;
            }
            return false;
        }

        switch (scancode)
        {
            case Scancode.ScancodeEscape:
                if (isRepeat)
                    return true;
                if (_showControls)
                    _showControls = false;
                else
                    _paused = !_paused;
                return true;
            case Scancode.ScancodeP:
                if (!isRepeat)
                    HandleSessionCommand(GameCommand.TogglePause);
                return true;
            case Scancode.ScancodeF1:
                if (!isRepeat)
                    HandleSessionCommand(GameCommand.ToggleControls);
                return true;
            case Scancode.ScancodeQ when _paused && !_showControls:
                _isRunning = false;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Pause and controls-screen commands. They also work from a command script (TUNNERER_COMMAND_SCRIPT), which is
    /// how the two screens are captured in automated screenshots. They are not recorded for replay.
    /// </summary>
    private bool HandleSessionCommand(GameCommand command)
    {
        switch (command)
        {
            case GameCommand.TogglePause:
                _showControls = false;
                _paused = !_paused;
                return true;
            case GameCommand.ToggleControls:
                _showControls = !_showControls;
                if (_showControls)
                    _paused = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Runs once per drawn frame while paused. In an automated run the script clock keeps ticking, so a command
    /// script that pauses can still take its screenshots and resume (for example
    /// <c>30:TogglePause,60:RequestScreenshot,90:TogglePause</c>); scripted player input for the paused frames is
    /// skipped. When a person pauses, the frame counter stands still, so an input recording stays aligned.
    /// </summary>
    private void AdvancePausedFrame()
    {
        if (!_automatedRun)
            return;

        _simulationStepper.ProcessScriptedCommands(_simFrameCounter);
        _simFrameCounter++;
    }

    private void DrawSessionOverlay(float deltaTime)
    {
        if (!IsInteractiveSession && !_paused)
            return;

        SessionOverlayAction action = _sessionOverlay.Draw(
            _hud.ViewportRect, _paused, _showControls, Seed, deltaTime, showKeyReminder: IsInteractiveSession);
        switch (action)
        {
            case SessionOverlayAction.Resume:
                _paused = false;
                _showControls = false;
                break;
            case SessionOverlayAction.ShowControls:
                _showControls = true;
                break;
            case SessionOverlayAction.Back:
                _showControls = false;
                break;
            case SessionOverlayAction.Quit:
                _isRunning = false;
                break;
        }
    }
}
