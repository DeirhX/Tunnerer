namespace Emberward.Desktop.Input;

using static SDL3.SDL;

/// <summary>What a key means to the session around a match (the pause menu, sound, quitting).</summary>
public enum SessionKey
{
    None,
    Escape,
    Pause,
    Controls,
    Mute,
    VolumeDown,
    VolumeUp,
    Quit,
    NetworkMatch,
    LeaveNetworkMatch,
    ToggleNewcomers,
}

public static class SessionKeys
{
    /// <summary>
    /// What <paramref name="scancode"/> does for the session. While a text field has the keyboard
    /// (<paramref name="textInputActive"/>) nothing does: typing a name must not quit the game or mute it.
    /// </summary>
    public static SessionKey Classify(
        Scancode scancode, bool paused, bool showControls, bool inNetworkMatch, bool textInputActive,
        bool canChangeNewcomers = false)
    {
        if (textInputActive)
            return SessionKey.None;
        bool onPauseMenu = paused && !showControls;
        return scancode switch
        {
            Scancode.Escape => SessionKey.Escape,
            Scancode.P => SessionKey.Pause,
            Scancode.F1 => SessionKey.Controls,
            Scancode.M => SessionKey.Mute,
            Scancode.Minus => SessionKey.VolumeDown,
            Scancode.Equals => SessionKey.VolumeUp,
            Scancode.Q when onPauseMenu => SessionKey.Quit,
            Scancode.N when onPauseMenu && !inNetworkMatch => SessionKey.NetworkMatch,
            Scancode.L when onPauseMenu && inNetworkMatch => SessionKey.LeaveNetworkMatch,
            Scancode.C when onPauseMenu && canChangeNewcomers => SessionKey.ToggleNewcomers,
            _ => SessionKey.None,
        };
    }
}
