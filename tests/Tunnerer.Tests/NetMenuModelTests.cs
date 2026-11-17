namespace Tunnerer.Tests;

using Tunnerer.Core.Types;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Net;
using Tunnerer.Protocol;

public class NetMenuModelTests
{
    private static readonly Uri Relay = new("wss://crust.bokovka.cz/play");

    private static NetMenuModel Menu(PlayerSettings? settings = null, string user = "Ann", NetMenuRequest? request = null) =>
        new(settings ?? new PlayerSettings(), user, request);

    private static void Type(NetMenuModel menu, NetMenuField field, string text)
    {
        menu.Click(field);
        menu.TextFieldOf(field)!.Set(string.Empty);
        menu.TypeText(text);
    }

    private static NetLaunchOptions Launched(NetMenuResult result)
    {
        Assert.Equal(NetMenuAction.Launch, result.Action);
        return Assert.IsType<NetLaunchOptions>(result.Launch);
    }

    // ---- Validation against what the relay accepts -------------------------------------------------------------------

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("A", true)]
    [InlineData("  Ann  ", true)]
    public void Name_NeedsAtLeastOneCharacterAfterTrimming(string name, bool valid) =>
        Assert.Equal(valid, NetMenuModel.ValidateName(name) is null);

    [Fact]
    public void Name_AndRoom_AreLimitedToWhatTheCodecCarries()
    {
        string longest = new('x', MessageCodec.MaxTextLength);
        string tooLong = longest + "x";

        Assert.Null(NetMenuModel.ValidateName(longest));
        Assert.NotNull(NetMenuModel.ValidateName(tooLong));
        Assert.Null(NetMenuModel.ValidateRoom(longest));
        Assert.NotNull(NetMenuModel.ValidateRoom(tooLong));
        Assert.NotNull(NetMenuModel.ValidateRoom("  "));
        Assert.Null(NetMenuModel.ValidateRoom(" friday "));
    }

    [Fact]
    public void TheFields_CannotHoldMoreThanTheCodecCarries()
    {
        var menu = Menu();

        Type(menu, NetMenuField.Name, new string('n', 200));
        Type(menu, NetMenuField.Room, new string('r', 200));

        Assert.Equal(MessageCodec.MaxTextLength, menu.NameField.Text.Length);
        Assert.Equal(MessageCodec.MaxTextLength, menu.RoomField.Text.Length);
        Assert.Equal(MessageCodec.MaxSecretLength, menu.SecretField.MaxLength);
    }

    [Fact]
    public void TypedText_LeavesOutControlCharacters()
    {
        var menu = Menu();

        Type(menu, NetMenuField.Room, "fri\nday\t\u0007!");

        Assert.Equal("friday!", menu.RoomField.Text);
    }

    // ---- Maps and tanks -----------------------------------------------------------------------------------------------

    [Fact]
    public void MapPresets_AreTheThreeSizesOfTheDesign()
    {
        Assert.Equal(new Size(320, 200), MapPresets.SizeOf(MapPreset.Small));
        Assert.Equal(new Size(480, 300), MapPresets.SizeOf(MapPreset.Medium));
        Assert.Equal(new Size(1500, 750), MapPresets.SizeOf(MapPreset.Large));
        Assert.Null(MapPresets.Warning(MapPreset.Small));
        Assert.Null(MapPresets.Warning(MapPreset.Medium));
        Assert.NotNull(MapPresets.Warning(MapPreset.Large));
    }

    [Fact]
    public void WithFourTanksTheMapIsMedium_AndWithTwoItIsSmall()
    {
        var menu = Menu();
        Assert.Equal(NetworkTweaks.DefaultPlayers, menu.Players);
        Assert.Equal(MapPreset.Medium, menu.Map);

        menu.Adjust(NetMenuField.Tanks, -1);
        menu.Adjust(NetMenuField.Tanks, -1);
        Assert.Equal(2, menu.Players);
        Assert.Equal(MapPreset.Small, menu.Map);

        menu.Adjust(NetMenuField.Tanks, +1);
        Assert.Equal(MapPreset.Medium, menu.Map);
    }

    [Fact]
    public void AChosenMap_StaysWhenTheNumberOfTanksChanges()
    {
        var menu = Menu();

        menu.Adjust(NetMenuField.Map, +1);
        Assert.Equal(MapPreset.Large, menu.Map);
        menu.Adjust(NetMenuField.Tanks, -1);
        menu.Adjust(NetMenuField.Tanks, -1);

        Assert.Equal(MapPreset.Large, menu.Map);
        menu.Adjust(NetMenuField.Map, +1);
        Assert.Equal(MapPreset.Small, menu.Map);
    }

    [Fact]
    public void Tanks_StayBetweenTwoAndEight()
    {
        var menu = Menu();

        for (int i = 0; i < 20; i++)
            menu.Adjust(NetMenuField.Tanks, +1);
        Assert.Equal(MessageCodec.MaxSlots, menu.Players);
        Assert.Equal(Tunnerer.Core.Config.Tweaks.World.MaxPlayers, menu.Players);
        for (int i = 0; i < 20; i++)
            menu.Adjust(NetMenuField.Tanks, -1);
        Assert.Equal(MessageCodec.MinSlots, menu.Players);
    }

    // ---- What Enter makes of each screen -----------------------------------------------------------------------------

    [Fact]
    public void Enter_OnTheFirstScreen_JoinsTheRoomWithTheDefaultSettings()
    {
        var menu = Menu(user: "Ann");
        Type(menu, NetMenuField.Room, "  friday ");

        var launch = Launched(menu.HandleKey(NetMenuKey.Enter));

        Assert.Equal(new NetLaunchOptions(
            new Uri(NetworkTweaks.DefaultRelay), "friday", "Ann", NetworkTweaks.DefaultPlayers, "", new Size(480, 300), null), launch);
    }

    [Fact]
    public void Enter_WithoutAName_OrARoom_ShowsWhatIsMissingAndLaunchesNothing()
    {
        var menu = Menu();
        Type(menu, NetMenuField.Name, "   ");

        var result = menu.HandleKey(NetMenuKey.Enter);

        Assert.Equal(NetMenuAction.None, result.Action);
        Assert.True(menu.Notice!.IsError);
        Assert.Equal(NetMenuField.Name, menu.Focus);

        Type(menu, NetMenuField.Name, "Ann");
        Type(menu, NetMenuField.Room, "");
        result = menu.HandleKey(NetMenuKey.Enter);

        Assert.Equal(NetMenuAction.None, result.Action);
        Assert.Contains("room", menu.Notice!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(NetMenuField.Room, menu.Focus);
    }

    [Fact]
    public void TheCreateKey_OpensTheRoomSettings_WithTheTypedRoomAsItsName()
    {
        var menu = Menu();
        Type(menu, NetMenuField.Room, "friday");

        var result = menu.HandleKey(NetMenuKey.Create);

        Assert.Equal(NetMenuAction.None, result.Action);
        Assert.Equal(NetMenuScreen.Create, menu.Screen);
        Assert.Equal("friday", menu.CreateNameField.Text);
        Assert.Equal(NetMenuField.CreateName, menu.Focus);
    }

    [Fact]
    public void CreateWithoutATypedRoom_ProposesTheNamesGame()
    {
        var menu = Menu(user: "Ann");
        Type(menu, NetMenuField.Room, "");

        menu.OpenCreate();

        Assert.Equal("Ann's game", menu.CreateNameField.Text);
    }

    [Fact]
    public void TheDefaultRoomName_FitsTheCodecWhateverTheName()
    {
        string name = NetMenuModel.DefaultRoomName(new string('x', 64));

        Assert.Equal(MessageCodec.MaxTextLength, name.Length);
        Assert.EndsWith("'s game", name, StringComparison.Ordinal);
        Assert.Equal("My game", NetMenuModel.DefaultRoomName("  "));
    }

    [Fact]
    public void Enter_OnTheCreateScreen_CreatesTheRoomWithTheChosenSettings()
    {
        var menu = Menu(user: "Ann");
        menu.OpenCreate();
        Type(menu, NetMenuField.CreateName, "lunch duel");
        menu.Adjust(NetMenuField.Tanks, -1);
        menu.Adjust(NetMenuField.Tanks, -1);
        Assert.Equal(MapPreset.Small, menu.Map);
        menu.Adjust(NetMenuField.Map, -1);
        Assert.Equal(MapPreset.Large, menu.Map);
        menu.Adjust(NetMenuField.SeedMode, +1);
        Type(menu, NetMenuField.SeedValue, "12345");

        var launch = Launched(menu.HandleKey(NetMenuKey.Enter));

        Assert.Equal("lunch duel", launch.Room);
        Assert.Equal(2, launch.Players);
        Assert.Equal(new Size(1500, 750), launch.MapSize);
        Assert.Equal(12345, launch.Seed);
        Assert.Equal("Ann", launch.Name);
    }

    [Fact]
    public void ANewMapEachMatch_LaunchesWithoutASeed()
    {
        var menu = Menu();
        menu.OpenCreate();
        menu.Adjust(NetMenuField.SeedMode, +1);
        Type(menu, NetMenuField.SeedValue, "55");
        menu.Adjust(NetMenuField.SeedMode, +1);

        var launch = Launched(menu.Submit());

        Assert.False(menu.FixedSeed);
        Assert.Null(launch.Seed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("99999999999")]
    public void AFixedSeedThatIsNotAWholeNumberFromOne_IsNotLaunched(string seed)
    {
        var menu = Menu();
        menu.OpenCreate();
        menu.Adjust(NetMenuField.SeedMode, +1);
        Type(menu, NetMenuField.SeedValue, seed);

        var result = menu.Submit();

        Assert.Equal(NetMenuAction.None, result.Action);
        Assert.Equal(NetMenuField.SeedValue, menu.Focus);
        Assert.True(menu.Notice!.IsError);
    }

    [Fact]
    public void TheSeedField_TakesDigitsOnly()
    {
        var menu = Menu();
        menu.OpenCreate();
        menu.Adjust(NetMenuField.SeedMode, +1);

        Type(menu, NetMenuField.SeedValue, "12ab-3");

        Assert.Equal("123", menu.SeedField.Text);
        Assert.Equal(123, NetMenuModel.ParseSeed("123"));
        Assert.Null(NetMenuModel.ParseSeed("-5"));
    }

    [Fact]
    public void Escape_GoesBackOneLevelAtATime()
    {
        var menu = Menu();
        menu.OpenCreate();

        Assert.Equal(NetMenuAction.None, menu.HandleKey(NetMenuKey.Escape).Action);
        Assert.Equal(NetMenuScreen.Join, menu.Screen);

        Assert.Equal(NetMenuAction.Close, menu.HandleKey(NetMenuKey.Escape).Action);
    }

    [Fact]
    public void EscapeFromAFailedCheck_StillGoesBack_AndClearsTheNotice()
    {
        var menu = Menu();
        Type(menu, NetMenuField.Room, "");
        menu.HandleKey(NetMenuKey.Enter);
        Assert.NotNull(menu.Notice);

        menu.HandleKey(NetMenuKey.Escape);

        Assert.Null(menu.Notice);
    }

    // ---- Focus, typing and the keys that edit -------------------------------------------------------------------------

    [Fact]
    public void TextInput_IsActiveOnlyWhileATextFieldHasTheKeyboard()
    {
        var menu = Menu();
        Assert.True(menu.TextInputActive);

        menu.OpenCreate();
        menu.Click(NetMenuField.Tanks);
        Assert.False(menu.TextInputActive);
        string before = menu.CreateNameField.Text;
        menu.TypeText("Quit");
        Assert.Equal(before, menu.CreateNameField.Text);

        menu.Click(NetMenuField.CreateName);
        Assert.True(menu.TextInputActive);
    }

    [Fact]
    public void TabAndTheArrows_MoveBetweenFields_AndWrapAround()
    {
        var menu = Menu();
        Assert.Equal(NetMenuField.Room, menu.Focus);

        menu.HandleKey(NetMenuKey.Tab);
        Assert.Equal(NetMenuField.Name, menu.Focus);
        menu.HandleKey(NetMenuKey.ShiftTab);
        Assert.Equal(NetMenuField.Room, menu.Focus);
        menu.HandleKey(NetMenuKey.Up);
        Assert.Equal(NetMenuField.Name, menu.Focus);
        menu.HandleKey(NetMenuKey.Down);
        Assert.Equal(NetMenuField.Room, menu.Focus);
    }

    [Fact]
    public void EditingKeys_ActOnTheFocusedField()
    {
        var menu = Menu();
        Type(menu, NetMenuField.Room, "friday");

        menu.HandleKey(NetMenuKey.Home);
        menu.HandleKey(NetMenuKey.Delete);
        menu.HandleKey(NetMenuKey.Right);
        menu.HandleKey(NetMenuKey.Backspace);
        menu.TypeText("X");
        menu.HandleKey(NetMenuKey.End);
        menu.HandleKey(NetMenuKey.Left);
        menu.TypeText("Y");

        Assert.Equal("XidaYy", menu.RoomField.Text);
    }

    [Fact]
    public void LeftAndRight_OnTheTanksRow_ChangeTheNumberOfTanks()
    {
        var menu = Menu();
        menu.OpenCreate();
        menu.Click(NetMenuField.Tanks);

        menu.HandleKey(NetMenuKey.Left);
        Assert.Equal(NetworkTweaks.DefaultPlayers - 1, menu.Players);
        menu.HandleKey(NetMenuKey.Right);
        menu.HandleKey(NetMenuKey.Right);
        Assert.Equal(NetworkTweaks.DefaultPlayers + 1, menu.Players);
    }

    // ---- Refusals and the secret ---------------------------------------------------------------------------------------

    [Fact]
    public void ARefusal_IsShownWithItsReason_AndTheFormComesBackAsItWas()
    {
        var failed = new NetLaunchOptions(Relay, "friday", "Ann", 3, Secret: "", MapSize: new Size(480, 300));
        var menu = Menu(request: new NetMenuRequest(failed, "This room is full."));

        Assert.Equal("This room is full.", menu.Notice!.Text);
        Assert.True(menu.Notice.IsError);
        Assert.Contains("friday", menu.Notice.Title, StringComparison.Ordinal);
        Assert.Equal("friday", menu.RoomField.Text);
        Assert.Equal("Ann", menu.NameField.Text);
        Assert.Equal(3, menu.Players);
        Assert.False(menu.SecretRequired);
        Assert.DoesNotContain(NetMenuField.Secret, menu.Fields);
    }

    [Fact]
    public void ASecretRefusal_ShowsTheMaskedSecretField_ReadyToType()
    {
        var failed = new NetLaunchOptions(Relay, "friday", "Ann", 4, Secret: "wrong");
        var menu = Menu(request: new NetMenuRequest(failed, "This relay needs a secret, and the one you sent is not it."));

        Assert.True(menu.SecretRequired);
        Assert.Contains(NetMenuField.Secret, menu.Fields);
        Assert.Equal(NetMenuField.Secret, menu.Focus);
        Assert.True(menu.SecretField.Masked);
        Assert.Equal("wrong", menu.SecretField.Text);
        Assert.True(menu.TextInputActive);

        Type(menu, NetMenuField.Secret, "right");
        var launch = Launched(menu.Submit());
        Assert.Equal("right", launch.Secret);
    }

    [Fact]
    public void TheSecret_IsStoredOnlyOnceTheRelayAskedForIt()
    {
        var withoutRefusal = Menu(request: new NetMenuRequest(Secret: "from-the-environment"));
        var launch = Launched(withoutRefusal.Submit());
        var settings = new PlayerSettings();
        withoutRefusal.ApplyTo(settings, launch);
        Assert.Equal("from-the-environment", launch.Secret);
        Assert.Null(settings.Secret);

        var failed = new NetLaunchOptions(Relay, "friday", "Ann", 4);
        var asked = Menu(request: new NetMenuRequest(failed, "This relay needs a secret, and the one you sent is not it."));
        Type(asked, NetMenuField.Secret, "hush");
        launch = Launched(asked.Submit());
        asked.ApplyTo(settings, launch);
        Assert.Equal("hush", settings.Secret);
    }

    // ---- Remembering --------------------------------------------------------------------------------------------------------

    [Fact]
    public void ALaunch_IsRemembered_AndTheNextMenuStartsFromIt()
    {
        var settings = new PlayerSettings();
        var menu = Menu(settings, user: "Ann");
        Type(menu, NetMenuField.Name, "Bob");
        Type(menu, NetMenuField.Room, "lunch");
        menu.OpenCreate();
        Type(menu, NetMenuField.CreateName, "lunch");
        menu.Adjust(NetMenuField.Tanks, -1);
        menu.Adjust(NetMenuField.Map, +1);
        menu.Adjust(NetMenuField.SeedMode, +1);
        Type(menu, NetMenuField.SeedValue, "77");
        var launch = Launched(menu.Submit());

        menu.ApplyTo(settings, launch);
        var next = Menu(settings, user: "SomeoneElse");

        Assert.Equal("Bob", next.NameField.Text);
        Assert.Equal("lunch", next.RoomField.Text);
        Assert.Equal(3, next.Players);
        Assert.Equal(MapPreset.Large, next.Map);
        Assert.True(next.FixedSeed);
        Assert.Equal("77", next.SeedField.Text);
        Assert.Equal(new Uri(NetworkTweaks.DefaultRelay), next.Relay);
    }

    [Fact]
    public void WithNothingRemembered_TheNameIsTheUserName_AndTheRoomIsTheSharedLobby()
    {
        var menu = Menu(user: "deirh");

        Assert.Equal("deirh", menu.NameField.Text);
        Assert.Equal(LaunchArguments.DefaultRoom, menu.RoomField.Text);
        Assert.Equal(new Uri(NetworkTweaks.DefaultRelay), menu.Relay);
        Assert.Null(menu.Notice);
    }

    [Fact]
    public void ASavedRelay_IsUsed_AndOneThatIsNotAWebSocketAddressIsNot()
    {
        Assert.Equal(new Uri("ws://127.0.0.1:8130/play"), Menu(new PlayerSettings { Relay = "ws://127.0.0.1:8130/play" }).Relay);
        Assert.Equal(new Uri(NetworkTweaks.DefaultRelay), Menu(new PlayerSettings { Relay = "http://example.com/play" }).Relay);
        Assert.Equal(new Uri(NetworkTweaks.DefaultRelay), Menu(new PlayerSettings { Relay = "nonsense" }).Relay);
    }

    [Fact]
    public void ALaunchFromTheMenu_ReadsBackThroughTheCommandLine()
    {
        var menu = Menu(user: "Ann Q.");
        menu.OpenCreate();
        Type(menu, NetMenuField.CreateName, "Ann's game");
        var launch = Launched(menu.Submit());

        var parsed = LaunchArguments.Parse(launch.ToArguments());

        Assert.Equal(launch, parsed.Net);
    }
}
