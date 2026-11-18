namespace Tunnerer.Desktop;

using static SDL3.SDL;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Gui;
using Tunnerer.Desktop.Input;

/// <summary>
/// Session-level behaviour around a match: pausing, the controls screen, quitting, and which map seed is used.
/// </summary>
public partial class Game
{
    private readonly SessionOverlay _sessionOverlay = new();
    private bool _automatedRun;
    private bool _paused;
    private bool _showControls;

    /// <summary>Seed of the current map and simulation. Pass it back with --seed=N to replay the same map.</summary>
    public int Seed { get; private set; } = DefaultSeed;

    /// <summary>Command-line arguments that rebuild the current map (the seed, plus --fast if it was used).</summary>
    public string ReplayArguments { get; private set; } = "";

    /// <summary>
    /// A person is playing: not a scripted, perf-capture, hidden or terrain-test run. Only then does Escape open the
    /// pause menu and losing window focus pause the game; automated runs must never stop and wait for input.
    /// </summary>
    private bool IsInteractiveSession => !_terrainTestMode && !_automatedRun;

    /// <summary>
    /// A scripted, perf-capture or hidden run, which nobody plays: it never waits for a menu, so a refused network
    /// launch ends it.
    /// </summary>
    public static bool IsAutomatedRun(PerfCaptureOptions? perfCapture)
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
    /// An explicit --seed always wins. Automated runs and the terrain test keep the fixed <see cref="DefaultSeed"/>
    /// so replays, perf captures and screenshots stay comparable. A normal match gets a fresh map every time.
    /// </summary>
    private static int ResolveSeed(bool interactiveSession)
    {
        if (GameLaunchOptions.Seed is int requested)
            return requested;
        if (!interactiveSession)
            return DefaultSeed;
        return Random.Shared.Next(1, int.MaxValue);
    }

    /// <summary>
    /// Handles Escape, P, F1, Q, N, L and the sound keys (M, - and =), and pauses when the window loses focus. Returns
    /// true when the event was consumed. While the network menu is open it takes every key (see
    /// <see cref="HandleNetMenuEvent"/>).
    /// </summary>
    private bool HandleSessionEvent(Event ev)
    {
        if (ev.Type == (uint)EventType.WindowFocusLost)
        {
            if (IsInteractiveSession && !IsNetMatch)
                _paused = true;
            return false;
        }

        if (IsInteractiveSession && _paused && _showNetMenu && HandleNetMenuEvent(ev))
            return true;

        if (ev.Type != (uint)EventType.KeyDown)
            return false;

        Scancode scancode = ev.Key.Scancode;
        bool isRepeat = ev.Key.Repeat;

        if (!IsInteractiveSession)
        {
            // Automated and terrain-test runs keep the old behaviour: Escape closes the game at once.
            if (scancode == Scancode.Escape)
            {
                _isRunning = false;
                return true;
            }
            return false;
        }

        switch (SessionKeys.Classify(scancode, _paused, _showControls, IsNetMatch, NetMenuHasTextInput))
        {
            case SessionKey.Escape:
                if (isRepeat)
                    return true;
                if (_showControls)
                    _showControls = false;
                else
                    _paused = !_paused;
                return true;
            case SessionKey.Pause:
                if (!isRepeat)
                    HandleSessionCommand(GameCommand.TogglePause);
                return true;
            case SessionKey.Controls:
                if (!isRepeat)
                    HandleSessionCommand(GameCommand.ToggleControls);
                return true;
            case SessionKey.Mute:
                if (!isRepeat)
                    _audio.ToggleMute();
                return true;
            case SessionKey.VolumeDown:
                _audio.AdjustVolume(-0.05f);
                return true;
            case SessionKey.VolumeUp:
                _audio.AdjustVolume(0.05f);
                return true;
            case SessionKey.Quit:
                _isRunning = false;
                return true;
            case SessionKey.NetworkMatch:
                if (!isRepeat)
                {
                    OpenNetMenu();
                    // Text input is always on, so the N also arrives as a typed "n", and again while it is held.
                    _openKeyGuard.Arm(scancode);
                }
                return true;
            case SessionKey.LeaveNetworkMatch:
                if (!isRepeat)
                    LeaveNetworkMatch();
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
            case GameCommand.TogglePause when _relay is { Started: true } relay:
                // A network match pauses for everyone or not at all: the relay holds the ticks.
                relay.SetPaused(!relay.IsPaused);
                return true;
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

        AdvanceScriptClock();
    }

    /// <summary>Runs the scripted commands and screenshots due this frame without stepping the simulation.</summary>
    private void AdvanceScriptClock()
    {
        _simulationStepper.ProcessScriptedCommands(_simFrameCounter);
        _simFrameCounter++;
    }

    private void DrawSessionOverlay(float deltaTime)
    {
        if (!IsInteractiveSession && !_paused)
            return;
        if (!_paused)
        {
            _leaveArmed = false;
            _showNetMenu = false;
            _openKeyGuard.Clear();
        }

        if (_paused && _showNetMenu && _netMenu is { } menu)
        {
            ApplyNetMenuResult(_netMenuView.Draw(_hud.PlayRect, menu));
            return;
        }

        var sound = new SoundMenuState(_audio.Enabled, AudioOptions.Volume, AudioOptions.Muted);
        SessionOverlayAction action = _sessionOverlay.Draw(
            _hud.PlayRect, _paused, _showControls, Seed, ReplayArguments, deltaTime, ref sound,
            showKeyReminder: IsInteractiveSession,
            network: new PauseNetworkEntry(IsNetMatch, _leaveArmed));
        // The menu only says what the player asked for; GameAudio owns the setting.
        if (sound.Muted != AudioOptions.Muted)
            _audio.SetMuted(sound.Muted);
        else if (sound.Volume != AudioOptions.Volume)
            _audio.SetVolume(sound.Volume);
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
            case SessionOverlayAction.NetworkMatch:
                OpenNetMenu();
                break;
            case SessionOverlayAction.LeaveNetworkMatch:
                LeaveNetworkMatch();
                break;
            case SessionOverlayAction.Quit:
                _isRunning = false;
                break;
        }
    }
}
