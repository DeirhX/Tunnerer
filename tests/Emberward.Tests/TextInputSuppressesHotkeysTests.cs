namespace Emberward.Tests;

using SDL3;
using Emberward.Desktop.Input;
using Emberward.Desktop.Net;
using static SDL3.SDL;

/// <summary>
/// Typing in a text field of the network menu must not reach the game's hotkeys: "Quinn" in the room field must not
/// quit the game, mute it or pause it.
/// </summary>
public class TextInputSuppressesHotkeysTests
{
    public static TheoryData<Scancode> SessionHotkeys => new()
    {
        Scancode.Q, Scancode.M, Scancode.P, Scancode.N, Scancode.L, Scancode.Minus, Scancode.Equals, Scancode.F1, Scancode.C,
    };

    [Theory]
    [MemberData(nameof(SessionHotkeys))]
    public void WithTextInputActive_NoSessionKeyDoesAnything(Scancode key)
    {
        foreach (bool paused in new[] { false, true })
            foreach (bool inNetworkMatch in new[] { false, true })
                Assert.Equal(SessionKey.None, SessionKeys.Classify(key, paused, showControls: false, inNetworkMatch, textInputActive: true, canChangeNewcomers: true));
    }

    [Fact]
    public void WithoutTextInput_QMAndTheOthersDoWhatTheyAlwaysDid()
    {
        Assert.Equal(SessionKey.Quit, SessionKeys.Classify(Scancode.Q, paused: true, showControls: false, inNetworkMatch: false, textInputActive: false));
        Assert.Equal(SessionKey.Mute, SessionKeys.Classify(Scancode.M, paused: false, showControls: false, inNetworkMatch: false, textInputActive: false));
        Assert.Equal(SessionKey.Pause, SessionKeys.Classify(Scancode.P, paused: false, showControls: false, inNetworkMatch: false, textInputActive: false));
        Assert.Equal(SessionKey.Controls, SessionKeys.Classify(Scancode.F1, paused: true, showControls: false, inNetworkMatch: false, textInputActive: false));
        Assert.Equal(SessionKey.Escape, SessionKeys.Classify(Scancode.Escape, paused: true, showControls: true, inNetworkMatch: false, textInputActive: false));
        Assert.Equal(SessionKey.VolumeDown, SessionKeys.Classify(Scancode.Minus, paused: false, showControls: false, inNetworkMatch: false, textInputActive: false));
        Assert.Equal(SessionKey.VolumeUp, SessionKeys.Classify(Scancode.Equals, paused: false, showControls: false, inNetworkMatch: false, textInputActive: false));
    }

    [Fact]
    public void QuitNetworkAndLeave_NeedThePauseMenuToBeOpen()
    {
        Assert.Equal(SessionKey.None, SessionKeys.Classify(Scancode.Q, paused: false, showControls: false, inNetworkMatch: false, textInputActive: false));
        Assert.Equal(SessionKey.None, SessionKeys.Classify(Scancode.Q, paused: true, showControls: true, inNetworkMatch: false, textInputActive: false));
        Assert.Equal(SessionKey.NetworkMatch, SessionKeys.Classify(Scancode.N, paused: true, showControls: false, inNetworkMatch: false, textInputActive: false));
        Assert.Equal(SessionKey.None, SessionKeys.Classify(Scancode.N, paused: false, showControls: false, inNetworkMatch: false, textInputActive: false));
        Assert.Equal(SessionKey.None, SessionKeys.Classify(Scancode.N, paused: true, showControls: false, inNetworkMatch: true, textInputActive: false));
        Assert.Equal(SessionKey.LeaveNetworkMatch, SessionKeys.Classify(Scancode.L, paused: true, showControls: false, inNetworkMatch: true, textInputActive: false));
        Assert.Equal(SessionKey.None, SessionKeys.Classify(Scancode.L, paused: true, showControls: false, inNetworkMatch: false, textInputActive: false));
    }

    [Fact]
    public void TheMenusFields_ReportTextInputExactlyWhileATextFieldHasTheKeyboard()
    {
        var menu = new NetMenuModel(new PlayerSettings(), "Ann");

        // Room has the keyboard: typing goes into it, and the session sees text input.
        Assert.True(menu.TextInputActive);
        menu.TypeText("Quinn Q M P N L");
        Assert.Equal("Quinn Q M P N L", menu.RoomField.Text[^15..]);

        // On a row that is no text field, characters go nowhere, and the session hotkeys may act again.
        menu.OpenCreate();
        menu.Click(NetMenuField.Map);
        Assert.False(menu.TextInputActive);
        string before = menu.CreateNameField.Text;
        menu.TypeText("q");
        Assert.Equal(before, menu.CreateNameField.Text);
    }

    [Fact]
    public void WhileTheMenuIsOpen_EveryHotkeyIsMeantForTheMenu()
    {
        // The game hands a key to the menu before it consults SessionKeys; the menu answers for the keys it knows,
        // and Q, M, P, N and L are no keys of its own, so they only ever type.
        var menu = new NetMenuModel(new PlayerSettings(), "Ann");
        menu.RoomField.Set(string.Empty);

        foreach (char c in "qmpnl")
            menu.TypeText(c.ToString());

        Assert.Equal("qmpnl", menu.RoomField.Text);
        Assert.Equal(NetMenuScreen.Join, menu.Screen);
    }

    [Fact]
    public void CloseToNewcomers_NeedsThePauseMenuAndTheHost()
    {
        Assert.Equal(SessionKey.ToggleNewcomers, SessionKeys.Classify(Scancode.C, paused: true, showControls: false, inNetworkMatch: true, textInputActive: false, canChangeNewcomers: true));
        Assert.Equal(SessionKey.None, SessionKeys.Classify(Scancode.C, paused: true, showControls: false, inNetworkMatch: true, textInputActive: false, canChangeNewcomers: false));
        Assert.Equal(SessionKey.None, SessionKeys.Classify(Scancode.C, paused: false, showControls: false, inNetworkMatch: true, textInputActive: false, canChangeNewcomers: true));
    }
}
