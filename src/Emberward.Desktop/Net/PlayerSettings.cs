namespace Emberward.Desktop.Net;

using System.Globalization;
using System.Text;

/// <summary>The last network match a player was in: where to find it again, and the name the relay gave them in it.</summary>
public sealed record LastMatch(string Relay, string Room, string Name, DateTimeOffset At);

/// <summary>
/// What the game remembers between runs, in <c>settings.ini</c>: one <c>key=value</c> line each. Reading never
/// fails: a missing, unreadable or damaged file gives the defaults, a line that makes no sense is skipped, and keys
/// this version does not know are written back unchanged, so a newer game's settings survive an older one.
/// </summary>
/// <remarks>
/// The relay's secret is stored in plain text, as it is in the <c>EMBERWARD_RELAY_SECRET</c> variable it replaces;
/// on Unix the file is readable by its owner only.
/// </remarks>
public sealed class PlayerSettings
{
    /// <summary>The path of the settings file: <c>EMBERWARD_SETTINGS_FILE</c>, else <c>Emberward/settings.ini</c> under the user's application data.</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("EMBERWARD_SETTINGS_FILE") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emberward", "settings.ini");

    /// <summary>The player's name in network matches; null until they have chosen one.</summary>
    public string? Name { get; set; }

    /// <summary>The relay of the last network match, as a <c>ws://</c> or <c>wss://</c> address.</summary>
    public string? Relay { get; set; }

    /// <summary>The relay's shared secret.</summary>
    public string? Secret { get; set; }

    /// <summary>The room the player last typed.</summary>
    public string? Room { get; set; }

    /// <summary>Tanks in the rooms the player creates.</summary>
    public int? CreatePlayers { get; set; }

    /// <summary>The map the player chose for the rooms they create; null leaves it to the number of tanks.</summary>
    public MapPreset? CreateMap { get; set; }

    /// <summary>The fixed seed of the rooms the player creates; null makes a new map each match.</summary>
    public int? CreateSeed { get; set; }

    public LastMatch? LastMatch { get; set; }

    /// <summary>Lines of keys this version does not know, in file order.</summary>
    private readonly List<KeyValuePair<string, string>> _unknown = [];

    /// <summary>The keys this version does not know, with their values.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> UnknownKeys => _unknown;

    /// <summary>Reads the file at <paramref name="path"/> (<see cref="DefaultPath"/> when null).</summary>
    public static PlayerSettings Load(string? path = null)
    {
        var settings = new PlayerSettings();
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path ?? DefaultPath, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return settings;
        }

        var lastMatch = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || line[0] is '#' or ';' or '[')
                continue;
            string key = line[..equals].Trim();
            string value = line[(equals + 1)..];
            if (key.Length == 0 || value.Any(char.IsControl))
                continue;
            if (key.StartsWith(LastMatchPrefix, StringComparison.Ordinal))
                lastMatch[key[LastMatchPrefix.Length..]] = value;
            else if (!settings.TrySet(key, value))
                settings._unknown.Add(new KeyValuePair<string, string>(key, value));
        }
        settings.LastMatch = ReadLastMatch(lastMatch);
        return settings;
    }

    private const string LastMatchPrefix = "lastmatch.";

    private bool TrySet(string key, string value)
    {
        switch (key)
        {
            case "name": Name = NonEmpty(value); return true;
            case "relay": Relay = NonEmpty(value); return true;
            case "secret": Secret = NonEmpty(value); return true;
            case "room": Room = NonEmpty(value); return true;
            case "create.players":
                CreatePlayers = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int players) &&
                    players is >= 2 and <= Emberward.Core.Config.Tweaks.World.MaxPlayers ? players : null;
                return true;
            case "create.map":
                CreateMap = Enum.TryParse(value, ignoreCase: true, out MapPreset map) && Enum.IsDefined(map) ? map : null;
                return true;
            case "create.seed":
                CreateSeed = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seed) && seed != 0
                    ? seed
                    : null;
                return true;
            default:
                return false;
        }
    }

    private static string? NonEmpty(string value) => value.Length > 0 ? value : null;

    private static LastMatch? ReadLastMatch(Dictionary<string, string> values) =>
        values.TryGetValue("relay", out string? relay) && values.TryGetValue("room", out string? room) &&
        values.TryGetValue("name", out string? name) && values.TryGetValue("at", out string? at) &&
        relay.Length > 0 && room.Length > 0 && name.Length > 0 &&
        DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
            ? new LastMatch(relay, room, name, when)
            : null;

    /// <summary>
    /// Writes the file at <paramref name="path"/> (<see cref="DefaultPath"/> when null), through a temporary file, so
    /// a crash or a full disk leaves the old settings as they were.
    /// </summary>
    /// <returns>False when it could not be written; the game goes on without remembering.</returns>
    public bool Save(string? path = null)
    {
        path ??= DefaultPath;
        string temporary = path + ".tmp";
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var writer = new StreamWriter(temporary, new UTF8Encoding(false), options))
            {
                foreach (string line in Lines())
                    writer.WriteLine(line);
            }
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.WriteLine($"[Settings] Could not save {path}: {e.Message}");
            return false;
        }
    }

    private IEnumerable<string> Lines()
    {
        yield return "# Emberward settings. The secret is stored as written.";
        if (Name is not null) yield return "name=" + Name;
        if (Relay is not null) yield return "relay=" + Relay;
        if (Secret is not null) yield return "secret=" + Secret;
        if (Room is not null) yield return "room=" + Room;
        if (CreatePlayers is { } players) yield return string.Create(CultureInfo.InvariantCulture, $"create.players={players}");
        if (CreateMap is { } map) yield return "create.map=" + map;
        if (CreateSeed is { } seed) yield return string.Create(CultureInfo.InvariantCulture, $"create.seed={seed}");
        if (LastMatch is { } last)
        {
            yield return LastMatchPrefix + "relay=" + last.Relay;
            yield return LastMatchPrefix + "room=" + last.Room;
            yield return LastMatchPrefix + "name=" + last.Name;
            yield return LastMatchPrefix + "at=" + last.At.ToString("o", CultureInfo.InvariantCulture);
        }
        foreach (var (key, value) in _unknown)
            yield return key + "=" + value;
    }
}
