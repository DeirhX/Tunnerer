namespace Emberward.Desktop.Net;

using System.Globalization;
using Emberward.Desktop.Config;
using Emberward.Protocol;

public enum NetMenuScreen
{
    /// <summary>Name and room: Join, or go on to Create.</summary>
    Join,

    /// <summary>The settings of a room to create.</summary>
    Create,
}

public enum NetMenuField
{
    Name,
    Room,
    Secret,
    CreateName,
    Tanks,
    Map,
    SeedMode,
    SeedValue,
}

/// <summary>The keys the network menu understands; the game maps its own key events onto these.</summary>
public enum NetMenuKey
{
    Enter,
    Escape,
    Tab,
    ShiftTab,
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    Backspace,
    Delete,

    /// <summary>Open the settings of a new room.</summary>
    Create,
}

public enum NetMenuAction
{
    None,

    /// <summary>Leave the menu for the pause menu.</summary>
    Close,

    /// <summary>Start the game again in the network match <see cref="NetMenuResult.Launch"/> describes.</summary>
    Launch,
}

public readonly record struct NetMenuResult(NetMenuAction Action, NetLaunchOptions? Launch = null)
{
    public static NetMenuResult Nothing => default;
}

/// <summary>
/// What the menu was opened for: after a refused launch, the launch and the relay's reason; otherwise the secret
/// this process was started with (<c>--secret</c> or the environment), which is the secret of
/// <paramref name="SecretRelay"/>, or of the default relay when that is null.
/// </summary>
public sealed record NetMenuRequest(
    NetLaunchOptions? Previous = null, string? Refusal = null, string Secret = "", Uri? SecretRelay = null);

/// <summary>A line the menu shows above its fields: why a launch failed, or what is wrong with a field.</summary>
public sealed record NetMenuNotice(string Title, string Text, bool IsError);

/// <summary>
/// The network menu without its drawing: which screen is open, what each field holds, what each key does, and the
/// launch that Enter produces. The menu only collects choices; <see cref="NetMenuAction.Launch"/> hands them to the
/// game, which starts again in the match (docs/multiplayer-lobby-ux.md sections 3 and 8).
/// </summary>
public sealed class NetMenuModel
{
    /// <summary>
    /// What the relay's refusal says when it wants a secret (or a different one): "This relay needs a secret, and the
    /// one you sent is not it." The protocol has no code for it, so the text is what there is to go by;
    /// <c>RelayOverSocketsTests</c> checks it against the real relay.
    /// </summary>
    public const string SecretRefusalText = "needs a secret";

    private bool _createNameEdited;
    private int _players;
    private MapPreset? _chosenMap;

    public NetMenuModel(PlayerSettings settings, string userName, NetMenuRequest? request = null)
    {
        var previous = request?.Previous;
        Relay = previous?.Relay ?? ParseRelay(settings.Relay) ?? new Uri(NetworkTweaks.DefaultRelay);
        NameField = new TextField(MessageCodec.MaxTextLength);
        NameField.Set(previous?.Name ?? settings.Name ?? userName);
        RoomField = new TextField(MessageCodec.MaxTextLength);
        RoomField.Set(previous?.Room ?? settings.Room ?? settings.LastMatch?.Room ?? LaunchArguments.DefaultRoom);
        SecretField = new TextField(MessageCodec.MaxSecretLength, masked: true);
        SecretField.Set(previous?.Secret is { Length: > 0 } tried ? tried
            : request is { Secret.Length: > 0 } && (request.SecretRelay ?? new Uri(NetworkTweaks.DefaultRelay)) == Relay ? request.Secret
            : SavedSecretFor(Relay, settings) ?? string.Empty);
        CreateNameField = new TextField(MessageCodec.MaxTextLength);
        SeedField = new TextField(10, accepts: c => c is >= '0' and <= '9');
        _players = previous?.Players ?? settings.CreatePlayers ?? NetworkTweaks.DefaultPlayers;
        // A launch that asked for the map its tanks get anyway chose nothing; one that asked for another did.
        _chosenMap = previous?.MapSize is { } asked && MapPresets.PresetOf(asked) is { } preset && preset != MapPresets.DefaultFor(_players)
            ? preset
            : settings.CreateMap;
        if (settings.CreateSeed is { } seed)
        {
            SeedField.Set(seed.ToString(CultureInfo.InvariantCulture));
            FixedSeed = true;
        }

        Focus = NetMenuField.Room;
        if (request?.Refusal is { } refusal)
        {
            string room = previous?.Room ?? string.Empty;
            Notice = new NetMenuNotice(room.Length > 0 ? $"CANNOT JOIN  {room}" : "CANNOT JOIN", refusal, IsError: true);
            // The relay wants a secret (a wrong one is the same case): the field appears, ready to type in.
            if (refusal.Contains(SecretRefusalText, StringComparison.Ordinal))
            {
                SecretRequired = true;
                Focus = NetMenuField.Secret;
            }
        }
    }

    public NetMenuScreen Screen { get; private set; }

    public NetMenuField Focus { get; private set; }

    public NetMenuNotice? Notice { get; private set; }

    /// <summary>The relay a launch from this menu joins. Not editable here: <c>--relay</c> or the settings file choose it.</summary>
    public Uri Relay { get; }

    public TextField NameField { get; }

    public TextField RoomField { get; }

    /// <summary>The relay's secret; masked, and on screen only after the relay asked for one (<see cref="SecretRequired"/>).</summary>
    public TextField SecretField { get; }

    public TextField CreateNameField { get; }

    public TextField SeedField { get; }

    /// <summary>The relay refused for want of a secret, so the secret field is shown.</summary>
    public bool SecretRequired { get; private set; }

    /// <summary>Tanks in a room this player creates.</summary>
    public int Players => _players;

    /// <summary>The map of a room this player creates: the one chosen, else the one the number of tanks gets.</summary>
    public MapPreset Map => _chosenMap ?? MapPresets.DefaultFor(_players);

    /// <summary>True when the rooms get a seed typed in, and false when each match gets a new map.</summary>
    public bool FixedSeed { get; private set; }

    /// <summary>The fields on the open screen, in the order Tab visits them.</summary>
    public IReadOnlyList<NetMenuField> Fields => Screen switch
    {
        NetMenuScreen.Join => SecretRequired
            ? [NetMenuField.Name, NetMenuField.Room, NetMenuField.Secret]
            : [NetMenuField.Name, NetMenuField.Room],
        _ => FixedSeed
            ? [NetMenuField.Name, NetMenuField.CreateName, NetMenuField.Tanks, NetMenuField.Map, NetMenuField.SeedMode, NetMenuField.SeedValue]
            : [NetMenuField.Name, NetMenuField.CreateName, NetMenuField.Tanks, NetMenuField.Map, NetMenuField.SeedMode],
    };

    /// <summary>
    /// The text field that has the keyboard, or null when a row that is not text (tanks, map, seed mode) has it. While
    /// this is not null, every key and character goes to it and the game's hotkeys (Q quits, M mutes) do nothing.
    /// </summary>
    public TextField? FocusedText => TextFieldOf(Focus);

    public bool TextInputActive => FocusedText != null;

    public TextField? TextFieldOf(NetMenuField field) => field switch
    {
        NetMenuField.Name => NameField,
        NetMenuField.Room => RoomField,
        NetMenuField.Secret => SecretField,
        NetMenuField.CreateName => CreateNameField,
        NetMenuField.SeedValue => SeedField,
        _ => null,
    };

    /// <summary>
    /// The saved secret, only for the relay it was saved with (the settings' relay, else the default one): a secret is
    /// a key to one relay, and a menu pointed at another (a launch with <c>--relay</c>) must not send it there.
    /// </summary>
    private static string? SavedSecretFor(Uri relay, PlayerSettings settings)
    {
        var savedFor = ParseRelay(settings.Relay) ?? new Uri(NetworkTweaks.DefaultRelay);
        return savedFor == relay ? settings.Secret : null;
    }

    private static Uri? ParseRelay(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && uri.Scheme is "ws" or "wss" ? uri : null;

    // ---- Checks, as the relay makes them -------------------------------------------------------------------------

    /// <summary>What is wrong with a player name, or null. The relay trims it and wants 1 to 64 characters.</summary>
    public static string? ValidateName(string text)
    {
        string name = text.Trim();
        if (name.Length == 0)
            return "Type your name.";
        return name.Length > MessageCodec.MaxTextLength
            ? string.Create(CultureInfo.InvariantCulture, $"A name has at most {MessageCodec.MaxTextLength} characters.")
            : null;
    }

    /// <summary>What is wrong with a room name, or null. Room names are not case sensitive on the relay.</summary>
    public static string? ValidateRoom(string text)
    {
        string room = text.Trim();
        if (room.Length == 0)
            return "Type the name of a room.";
        return room.Length > MessageCodec.MaxTextLength
            ? string.Create(CultureInfo.InvariantCulture, $"A room name has at most {MessageCodec.MaxTextLength} characters.")
            : null;
    }

    /// <summary>The seed typed in, or null when the text is not a whole number from 1 up.</summary>
    public static int? ParseSeed(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int seed) && seed >= 1 ? seed : null;

    /// <summary>The name of the room a player gets when they create one without choosing a name.</summary>
    public static string DefaultRoomName(string playerName)
    {
        string name = playerName.Trim();
        if (name.Length == 0)
            return "My game";
        const string suffix = "'s game";
        return name[..Math.Min(name.Length, MessageCodec.MaxTextLength - suffix.Length)] + suffix;
    }

    // ---- Keys and clicks ----------------------------------------------------------------------------------------------

    /// <summary>Typed or pasted text goes to the field that has the keyboard, if it is a text field.</summary>
    public void TypeText(string text)
    {
        if (FocusedText is not { } field)
            return;
        field.Insert(text);
        if (Focus == NetMenuField.CreateName)
            _createNameEdited = true;
    }

    public NetMenuResult HandleKey(NetMenuKey key)
    {
        switch (key)
        {
            case NetMenuKey.Enter:
                return Submit();
            case NetMenuKey.Escape:
                return Back();
            case NetMenuKey.Create:
                OpenCreate();
                return NetMenuResult.Nothing;
            case NetMenuKey.Tab:
            case NetMenuKey.Down:
                MoveFocus(+1);
                return NetMenuResult.Nothing;
            case NetMenuKey.ShiftTab:
            case NetMenuKey.Up:
                MoveFocus(-1);
                return NetMenuResult.Nothing;
        }

        if (FocusedText is { } field)
        {
            switch (key)
            {
                case NetMenuKey.Left: field.MoveLeft(); break;
                case NetMenuKey.Right: field.MoveRight(); break;
                case NetMenuKey.Home: field.Home(); break;
                case NetMenuKey.End: field.End(); break;
                case NetMenuKey.Backspace: field.Backspace(); MarkEdited(); break;
                case NetMenuKey.Delete: field.Delete(); MarkEdited(); break;
            }
        }
        else if (key is NetMenuKey.Left or NetMenuKey.Right)
        {
            Adjust(Focus, key == NetMenuKey.Left ? -1 : +1);
        }
        return NetMenuResult.Nothing;
    }

    private void MarkEdited()
    {
        if (Focus == NetMenuField.CreateName)
            _createNameEdited = true;
    }

    /// <summary>Enter: join the room on the first screen, create it on the second.</summary>
    public NetMenuResult Submit() => Screen == NetMenuScreen.Join ? Join() : Create();

    /// <summary>Escape: from the room's settings back to the first screen, from there out of the menu.</summary>
    public NetMenuResult Back()
    {
        Notice = null;
        if (Screen == NetMenuScreen.Create)
        {
            Screen = NetMenuScreen.Join;
            Focus = NetMenuField.Room;
            return NetMenuResult.Nothing;
        }
        return new NetMenuResult(NetMenuAction.Close);
    }

    /// <summary>Gives the keyboard to a field, as a click on it does. A field of the other screen is ignored.</summary>
    public void Click(NetMenuField field)
    {
        if (Fields.Contains(field))
            Focus = field;
    }

    /// <summary>Opens the settings of a room to create, with the room's name filled in from the first screen.</summary>
    public void OpenCreate()
    {
        if (Screen == NetMenuScreen.Create)
            return;
        Notice = null;
        if (!_createNameEdited || CreateNameField.Text.Length == 0)
        {
            string typed = RoomField.Text.Trim();
            CreateNameField.Set(typed.Length > 0 ? typed : DefaultRoomName(NameField.Text));
            _createNameEdited = false;
        }
        Screen = NetMenuScreen.Create;
        Focus = NetMenuField.CreateName;
    }

    private void MoveFocus(int step)
    {
        var fields = Fields;
        int at = Math.Max(0, fields.ToList().IndexOf(Focus));
        Focus = fields[(at + step + fields.Count) % fields.Count];
    }

    /// <summary>Changes a row that is not text: tanks and map by a step, the seed between new and fixed.</summary>
    public void Adjust(NetMenuField field, int step)
    {
        switch (field)
        {
            case NetMenuField.Tanks:
                _players = Math.Clamp(_players + step, MessageCodec.MinSlots, MessageCodec.MaxSlots);
                break;
            case NetMenuField.Map:
                int count = Enum.GetValues<MapPreset>().Length;
                _chosenMap = (MapPreset)(((int)Map + step + count) % count);
                break;
            case NetMenuField.SeedMode:
                FixedSeed = !FixedSeed;
                break;
        }
    }

    // ---- What Enter makes of it -------------------------------------------------------------------------------------------

    private NetMenuResult Join()
    {
        if (Check(ValidateName(NameField.Text), NetMenuField.Name) ||
            Check(ValidateRoom(RoomField.Text), NetMenuField.Room))
            return NetMenuResult.Nothing;

        // A room that does not exist yet is opened with the settings this player last made for one.
        return Launch(RoomField.Text.Trim(), seed: null);
    }

    private NetMenuResult Create()
    {
        if (Check(ValidateName(NameField.Text), NetMenuField.Name) ||
            Check(ValidateRoom(CreateNameField.Text), NetMenuField.CreateName))
            return NetMenuResult.Nothing;

        int? seed = null;
        if (FixedSeed)
        {
            seed = ParseSeed(SeedField.Text);
            if (seed is null)
            {
                Fail("Type a whole number from 1 up, or choose a new map each match.", NetMenuField.SeedValue);
                return NetMenuResult.Nothing;
            }
        }
        return Launch(CreateNameField.Text.Trim(), seed);
    }

    /// <summary>Shows what is wrong with a field and puts the keyboard on it; false when there is nothing wrong.</summary>
    private bool Check(string? problem, NetMenuField field)
    {
        if (problem is null)
            return false;
        Fail(problem, field);
        return true;
    }

    private void Fail(string problem, NetMenuField field)
    {
        Notice = new NetMenuNotice("CHECK THIS", problem, IsError: true);
        if (Fields.Contains(field))
            Focus = field;
    }

    private NetMenuResult Launch(string room, int? seed)
    {
        Notice = null;
        var launch = new NetLaunchOptions(
            Relay, room, NameField.Text.Trim(), _players, SecretField.Text, MapPresets.SizeOf(Map), seed);
        return new NetMenuResult(NetMenuAction.Launch, launch);
    }

    /// <summary>Writes the choices of a launch into the settings, for the next time.</summary>
    public void ApplyTo(PlayerSettings settings, NetLaunchOptions launched)
    {
        settings.Name = launched.Name;
        settings.Relay = launched.Relay.AbsoluteUri;
        settings.Room = launched.Room;
        // A secret is stored only once the relay has asked for one, as the player typed it.
        if (SecretRequired)
            settings.Secret = launched.Secret.Length > 0 ? launched.Secret : null;
        settings.CreatePlayers = _players;
        settings.CreateMap = _chosenMap;
        settings.CreateSeed = FixedSeed ? ParseSeed(SeedField.Text) : null;
    }
}
