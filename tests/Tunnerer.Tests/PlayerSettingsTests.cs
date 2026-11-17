namespace Tunnerer.Tests;

using Tunnerer.Desktop.Net;

public sealed class PlayerSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tunnerer-settings-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "settings.ini");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static PlayerSettings Filled() => new()
    {
        Name = "Ann (2)",
        Relay = "wss://crust.bokovka.cz/play",
        Secret = " spaced = secret ",
        Room = "friday",
        CreatePlayers = 6,
        CreateMap = MapPreset.Large,
        CreateSeed = 424242,
        LastMatch = new LastMatch("wss://crust.bokovka.cz/play", "friday", "Ann (2)",
            new DateTimeOffset(2026, 11, 18, 9, 30, 15, TimeSpan.Zero)),
    };

    [Fact]
    public void SavedSettings_ComeBackAsTheyWere()
    {
        Assert.True(Filled().Save(FilePath));

        var loaded = PlayerSettings.Load(FilePath);

        Assert.Equal("Ann (2)", loaded.Name);
        Assert.Equal("wss://crust.bokovka.cz/play", loaded.Relay);
        Assert.Equal(" spaced = secret ", loaded.Secret);
        Assert.Equal("friday", loaded.Room);
        Assert.Equal(6, loaded.CreatePlayers);
        Assert.Equal(MapPreset.Large, loaded.CreateMap);
        Assert.Equal(424242, loaded.CreateSeed);
        Assert.Equal(Filled().LastMatch, loaded.LastMatch);
    }

    [Fact]
    public void SaveCreatesTheDirectory_AndAnEmptyFileLoadsAsTheDefaults()
    {
        Assert.False(Directory.Exists(_directory));

        Assert.True(new PlayerSettings().Save(FilePath));

        var loaded = PlayerSettings.Load(FilePath);
        Assert.Null(loaded.Name);
        Assert.Null(loaded.Secret);
        Assert.Null(loaded.CreatePlayers);
        Assert.Null(loaded.CreateMap);
        Assert.Null(loaded.CreateSeed);
        Assert.Null(loaded.LastMatch);
        Assert.Empty(loaded.UnknownKeys);
    }

    [Fact]
    public void MissingFile_GivesTheDefaults()
    {
        var loaded = PlayerSettings.Load(Path.Combine(_directory, "nothing-here.ini"));

        Assert.Null(loaded.Name);
        Assert.Null(loaded.Relay);
        Assert.Null(loaded.LastMatch);
    }

    [Fact]
    public void CorruptFile_GivesTheDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(FilePath, [0xFF, 0xFE, 0x00, 0x01, 0x80, 0x00, 0xC3, 0x28, 0x0A, 0x00, 0x00, 0x7F]);

        var loaded = PlayerSettings.Load(FilePath);

        Assert.Null(loaded.Name);
        Assert.Null(loaded.Relay);
        Assert.Null(loaded.Secret);
        Assert.Null(loaded.CreatePlayers);
        Assert.Null(loaded.LastMatch);
    }

    [Fact]
    public void ALineThatMakesNoSense_IsSkippedAndTheRestIsKept()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllLines(FilePath,
        [
            "this line has no equals sign",
            "name=Ann",
            "create.players=99",
            "create.map=gigantic",
            "create.seed=-4",
            "=value without a key",
            "lastmatch.relay=wss://x/play",
            "lastmatch.room=r",
            "room=friday",
        ]);

        var loaded = PlayerSettings.Load(FilePath);

        Assert.Equal("Ann", loaded.Name);
        Assert.Equal("friday", loaded.Room);
        Assert.Null(loaded.CreatePlayers);
        Assert.Null(loaded.CreateMap);
        Assert.Null(loaded.CreateSeed);
        Assert.Null(loaded.LastMatch);
    }

    [Fact]
    public void UnknownKeys_AreKeptWhenTheFileIsWrittenAgain()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllLines(FilePath, ["name=Ann", "volume=45", "# a comment", "future.thing=a=b"]);

        var loaded = PlayerSettings.Load(FilePath);
        loaded.Name = "Bob";
        Assert.True(loaded.Save(FilePath));
        var again = PlayerSettings.Load(FilePath);

        Assert.Equal("Bob", again.Name);
        Assert.Equal(
            new[] { new KeyValuePair<string, string>("volume", "45"), new KeyValuePair<string, string>("future.thing", "a=b") },
            again.UnknownKeys);
    }

    [Fact]
    public void SaveIsDoneThroughATemporaryFile_ThatIsNotLeftBehind()
    {
        Assert.True(Filled().Save(FilePath));
        Assert.True(Filled().Save(FilePath));

        Assert.Equal([FilePath], Directory.GetFiles(_directory));
    }

    [Fact]
    public void SaveThatCannotBeDone_ReportsItAndDoesNotThrow()
    {
        Directory.CreateDirectory(FilePath);

        Assert.False(Filled().Save(FilePath));
    }

    [Fact]
    public void OnUnix_TheFileWithTheSecretIsReadableByItsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.True(Filled().Save(FilePath));

        var mode = File.GetUnixFileMode(FilePath);
        Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite));
    }
}
