namespace Tunnerer.Desktop;

using System.Runtime.InteropServices;
using static SDL3.SDL;
using Tunnerer.Desktop.Gui;
using Tunnerer.Desktop.Net;

/// <summary>
/// The network menu in the pause menu: choosing a room, and leaving one. The menu only collects choices; a launch
/// closes the game and <see cref="Relaunch"/> says how to start it again (docs/multiplayer-lobby-ux.md section 3).
/// </summary>
public partial class Game
{
    /// <summary>The name the player gets in the menu until they choose one.</summary>
    private static string DefaultPlayerName => Environment.UserName;

    private readonly PlayerSettings? _playerSettings;
    private readonly NetMenuView _netMenuView = new();
    private NetMenuModel? _netMenu;
    private bool _showNetMenu;

    /// <summary>The player pressed Leave in a started match once; the next press leaves.</summary>
    private bool _leaveArmed;
    private bool _lastMatchSaved;

    /// <summary>How to start again when the game closes, if the menu asked for something other than the same match.</summary>
    public RelaunchRequest? Relaunch { get; private set; }

    /// <summary>A text field of the menu has the keyboard: the game's hotkeys must stay out of its way.</summary>
    private bool NetMenuHasTextInput => _showNetMenu && _netMenu is { TextInputActive: true };

    /// <summary>Opens the network menu over a paused game (the pause menu's Network match button, or a refused launch).</summary>
    private void OpenNetMenu(NetMenuRequest? request = null)
    {
        _netMenu = new NetMenuModel(_playerSettings ?? new PlayerSettings(), DefaultPlayerName, request);
        _showNetMenu = true;
        _showControls = false;
        _paused = true;
    }

    /// <summary>
    /// While the menu is open it takes every key and every typed character, so that nothing typed in a field
    /// reaches the game's hotkeys. Mouse events are left to the renderer's UI input.
    /// </summary>
    private unsafe bool HandleNetMenuEvent(Event ev)
    {
        if (_netMenu is not { } menu)
            return false;
        switch ((EventType)ev.Type)
        {
            case EventType.TextInput:
                menu.TypeText(Marshal.PtrToStringUTF8(ev.Text.Text) ?? string.Empty);
                return true;
            case EventType.KeyDown:
                if (IsPasteShortcut(ev.Key.Scancode, ev.Key.Mod))
                {
                    if (!ev.Key.Repeat)
                        menu.TypeText(GetClipboardText() ?? string.Empty);
                }
                else if (NetMenuKeyOf(ev.Key.Scancode, ev.Key.Mod) is { } key && (!ev.Key.Repeat || RepeatsWhenHeld(key)))
                {
                    ApplyNetMenuResult(menu.HandleKey(key));
                }
                return true;
            default:
                return false;
        }
    }

    private static bool IsPasteShortcut(Scancode scancode, Keymod mod) =>
        scancode == Scancode.V && (mod & (Keymod.Ctrl | Keymod.GUI)) != 0;

    private static NetMenuKey? NetMenuKeyOf(Scancode scancode, Keymod mod) => scancode switch
    {
        Scancode.Return or Scancode.Return2 or Scancode.KpEnter => NetMenuKey.Enter,
        Scancode.Escape => NetMenuKey.Escape,
        Scancode.Tab => (mod & Keymod.Shift) != 0 ? NetMenuKey.ShiftTab : NetMenuKey.Tab,
        Scancode.Up => NetMenuKey.Up,
        Scancode.Down => NetMenuKey.Down,
        Scancode.Left => NetMenuKey.Left,
        Scancode.Right => NetMenuKey.Right,
        Scancode.Home => NetMenuKey.Home,
        Scancode.End => NetMenuKey.End,
        Scancode.Backspace => NetMenuKey.Backspace,
        Scancode.Delete => NetMenuKey.Delete,
        Scancode.F2 => NetMenuKey.Create,
        _ => null,
    };

    private static bool RepeatsWhenHeld(NetMenuKey key) =>
        key is NetMenuKey.Left or NetMenuKey.Right or NetMenuKey.Up or NetMenuKey.Down or NetMenuKey.Backspace or NetMenuKey.Delete;

    private void ApplyNetMenuResult(NetMenuResult result)
    {
        switch (result.Action)
        {
            case NetMenuAction.Close:
                _showNetMenu = false;
                break;
            case NetMenuAction.Launch when result.Launch is { } launch && _netMenu is { } menu:
                if (_playerSettings is { } settings)
                {
                    menu.ApplyTo(settings, launch);
                    settings.Save();
                }
                Console.WriteLine($"[Net] Starting the game again in room '{launch.Room}' as '{launch.Name}'.");
                RequestRelaunch(new RelaunchRequest(launch));
                break;
        }
    }

    private void RequestRelaunch(RelaunchRequest request)
    {
        Relaunch = request;
        RejoinRequested = false;
        RestartRequested = true;
        _isRunning = false;
    }

    /// <summary>
    /// Closes the connection and starts a local match with the network menu open. In a started match the first press
    /// only asks: the player's tank would idle and could be lost.
    /// </summary>
    private void LeaveNetworkMatch()
    {
        if (_relay is { Started: true, Failure: null } && !_world.IsMatchOver && !_leaveArmed)
        {
            _leaveArmed = true;
            return;
        }
        RequestRelaunch(new RelaunchRequest(OpenNetMenu: true));
    }

    /// <summary>
    /// Remembers the room once the relay has told this player their name in it: the name to come back under, when the
    /// relay numbered it.
    /// </summary>
    private void SaveLastMatch(RelayConnection relay)
    {
        if (_lastMatchSaved || _playerSettings is not { } settings || relay.AssignedName is not { } assigned)
            return;
        _lastMatchSaved = true;
        settings.LastMatch = new LastMatch(relay.Address.AbsoluteUri, relay.Room, assigned, DateTimeOffset.UtcNow);
        settings.Save();
    }
}
