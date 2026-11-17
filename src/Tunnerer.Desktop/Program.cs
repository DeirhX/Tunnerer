using Tunnerer.Core.LevelGen;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Config;

// Numbers in logs, CSV files and scripts use '.' as the decimal separator whatever the system language is.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

if (args.Any(a => a is "--help" or "-h" or "-?" or "/?"))
{
    Console.WriteLine("""
        Tunnerer - a 2D tunnelling tank game

        Usage: Tunnerer.Desktop [options]

        Match
          --seed=N            Play the map with seed N (shown in the pause menu). Default: a new map each match.
          --size=WxH          Terrain size in cells.
          --large             Terrain size 1500x750.
          --no-ai             No enemy tank.
          --no-fog            No fog of war: the whole map is shown live.
          --fast              Parallel level generation and regrowth (another map per seed; still reproducible).

        Network match (see docs/multiplayer-design.md)
          --net[=ROOM]        Join ROOM (default: lobby) on the shared relay. The first player in opens the room
                              and their --seed, --size, --fast and --players apply to everyone.
          --relay=URL         Use the relay at URL instead (ws:// or wss://, ending in /play).
          --room=NAME         The room to join, when --relay is given without --net.
          --players=N         Tanks in the match, 2 to 8 (default 4). Bots take the slots nobody joins.
          --name=NAME         Your name in the room. After dropping out, the same name gets you back in.
          --secret=TEXT       The relay's shared secret, if it has one (or set TUNNERER_RELAY_SECRET).

        Display
          --windowed[=WxH]    Resizable window instead of borderless fullscreen (default 1280x720).
          --display=N         Open on display N, or 'secondary'.
          --hidden            Never show the window (automated runs).

        Sound
          --volume=N          Master volume, 0 to 100 (default 70). In game: - and = change it.
          --mute              Start with the sound off. In game: M toggles it.

        Development
          --dump-sounds=DIR   Write every sound effect to a WAV file in DIR, print its levels, and exit.
          --terrain-test      Static terrain test patterns instead of a match.
          --perf              Capture frame times and exit (--perf-warmup=N --perf-frames=N --perf-csv=PATH).

        In game: W A S D drive and dig, mouse aims, left click or Space fires, E builds a relay,
        M mutes the sound, F1 shows the controls, Esc pauses.
        """);
    return;
}

Size? terrainSize = null;
bool fastGen = false;
bool perfMode = false;
bool terrainTestMode = false;
int perfWarmupFrames = 180;
int perfMeasureFrames = 900;
string? perfCsvPath = null;
bool enableAiTanks = true;
bool fogOfWar = true;
int? displayIndex = DesktopDisplayOptions.Parse(Environment.GetEnvironmentVariable("TUNNERER_DISPLAY"));
Size? windowedSize = DesktopDisplayOptions.ParseWindowed(Environment.GetEnvironmentVariable("TUNNERER_WINDOWED"));
bool hiddenWindow = DesktopDisplayOptions.ParseFlag(Environment.GetEnvironmentVariable("TUNNERER_HIDDEN"));
float? volume = AudioOptions.ParseVolume(Environment.GetEnvironmentVariable("TUNNERER_VOLUME"));
bool mute = DesktopDisplayOptions.ParseFlag(Environment.GetEnvironmentVariable("TUNNERER_MUTE"));
string? audioCapturePath = Environment.GetEnvironmentVariable("TUNNERER_AUDIO_CAPTURE");
string? dumpSoundsDirectory = null;
Uri? relayUri = null;
string room = "lobby";
string playerName = Environment.UserName;
int players = NetworkTweaks.DefaultPlayers;
string relaySecret = Environment.GetEnvironmentVariable("TUNNERER_RELAY_SECRET") ?? string.Empty;
foreach (var arg in args)
{
    if (arg == "--large")
        terrainSize = new Size(1500, 750);
    else if (arg == "--fast")
        fastGen = true;
    else if (arg == "--perf")
        perfMode = true;
    else if (arg == "--terrain-test")
        terrainTestMode = true;
    else if (arg.StartsWith("--perf-warmup=", StringComparison.Ordinal) && int.TryParse(arg[14..], out int warmup))
        perfWarmupFrames = Math.Max(0, warmup);
    else if (arg.StartsWith("--perf-frames=", StringComparison.Ordinal) && int.TryParse(arg[14..], out int frames))
        perfMeasureFrames = Math.Max(1, frames);
    else if (arg.StartsWith("--perf-csv=", StringComparison.Ordinal))
        perfCsvPath = arg[11..];
    else if (arg.StartsWith("--size=", StringComparison.Ordinal))
    {
        var parts = arg[7..].Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
            terrainSize = new Size(w, h);
    }
    else if (arg == "--windowed" || arg.StartsWith("--windowed=", StringComparison.Ordinal))
    {
        string value = arg.Length > "--windowed".Length ? arg["--windowed=".Length..] : string.Empty;
        var parsed = DesktopDisplayOptions.ParseWindowed(value, flagPresent: true);
        if (parsed is null)
            Console.WriteLine($"[Args] Unknown window size '{value}', expected WxH (at least 320x200). Using fullscreen.");
        else
            windowedSize = parsed;
    }
    else if (arg == "--hidden")
    {
        hiddenWindow = true;
    }
    else if (arg.StartsWith("--display=", StringComparison.Ordinal))
    {
        var parsed = DesktopDisplayOptions.Parse(arg[10..]);
        if (parsed is null)
            Console.WriteLine($"[Args] Unknown display '{arg[10..]}', expected a number or 'secondary'. Using the default.");
        else
            displayIndex = parsed;
    }
    else if (arg.StartsWith("--volume=", StringComparison.Ordinal))
    {
        var parsed = AudioOptions.ParseVolume(arg[9..]);
        if (parsed is null)
            Console.WriteLine($"[Args] Unknown volume '{arg[9..]}', expected a number from 0 to 100. Using the default.");
        else
            volume = parsed;
    }
    else if (arg == "--mute")
    {
        mute = true;
    }
    else if (arg.StartsWith("--dump-sounds=", StringComparison.Ordinal))
    {
        dumpSoundsDirectory = arg[14..];
    }
    else if (arg == "--no-ai" || arg == "--disable-ai")
    {
        enableAiTanks = false;
    }
    else if (arg == "--no-fog")
    {
        fogOfWar = false;
    }
    else if (arg.StartsWith("--relay=", StringComparison.Ordinal))
    {
        if (Uri.TryCreate(arg[8..], UriKind.Absolute, out Uri? parsed) && parsed.Scheme is "ws" or "wss")
            relayUri = parsed;
        else
            Console.WriteLine($"[Args] Unknown relay '{arg[8..]}', expected a ws:// or wss:// address. Playing a local match.");
    }
    else if (arg == "--net" || arg.StartsWith("--net=", StringComparison.Ordinal))
    {
        relayUri ??= new Uri(NetworkTweaks.DefaultRelay);
        if (arg.Length > 6)
            room = arg[6..];
    }
    else if (arg.StartsWith("--room=", StringComparison.Ordinal) && arg.Length > 7)
    {
        room = arg[7..];
    }
    else if (arg.StartsWith("--name=", StringComparison.Ordinal) && arg.Length > 7)
    {
        playerName = arg[7..];
    }
    else if (arg.StartsWith("--secret=", StringComparison.Ordinal))
    {
        relaySecret = arg[9..];
    }
    else if (arg.StartsWith("--players=", StringComparison.Ordinal))
    {
        if (int.TryParse(arg[10..], out int count) && count is >= 2 and <= Tunnerer.Core.Config.Tweaks.World.MaxPlayers)
            players = count;
        else
            Console.WriteLine($"[Args] Unknown player count '{arg[10..]}', expected 2 to 8. Using {players}.");
    }
    else if (arg.StartsWith("--seed=", StringComparison.Ordinal))
    {
        if (int.TryParse(arg[7..], out int seedValue) && seedValue != 0)
            GameLaunchOptions.Seed = seedValue;
        else
            Console.WriteLine($"[Args] Unknown seed '{arg[7..]}', expected a non-zero whole number. Using a new map.");
    }
    else
    {
        Console.WriteLine($"[Args] Unknown option '{arg}' ignored. Run with --help to list the options.");
    }
}

DesktopDisplayOptions.DisplayIndex = displayIndex;
DesktopDisplayOptions.WindowedSize = windowedSize;
DesktopDisplayOptions.Hidden = hiddenWindow;
AudioOptions.Volume = volume ?? AudioOptions.DefaultVolume;
AudioOptions.Muted = mute;
AudioOptions.CapturePath = string.IsNullOrWhiteSpace(audioCapturePath) ? null : audioCapturePath.Trim();
if (!string.IsNullOrWhiteSpace(dumpSoundsDirectory))
{
    Tunnerer.Desktop.Audio.GameAudio.DumpEffects(dumpSoundsDirectory);
    return;
}
bool restartRequested;
bool rejoin;
// Set for the process a restart starts when the player is coming back to a match after losing the connection.
const string RejoinVariable = "TUNNERER_NET_REJOIN";
var mode = fastGen ? LevelGenMode.Optimized : LevelGenMode.Deterministic;
Tunnerer.Desktop.PerfCaptureOptions? perfOptions = perfMode
    ? new Tunnerer.Desktop.PerfCaptureOptions(perfWarmupFrames, perfMeasureFrames, perfCsvPath)
    : null;
Tunnerer.Desktop.Net.RelayConnection? relay = null;
if (relayUri != null && !terrainTestMode)
{
    // The first player into the room decides the match; everyone builds what the relay hands back.
    var proposed = Tunnerer.Desktop.Net.NetLaunch.ProposeConfig(
        GameLaunchOptions.Seed ?? Random.Shared.Next(1, int.MaxValue),
        terrainSize ?? (players > 2 ? NetworkTweaks.LargeMatchMapSize : DesktopScreenTweaks.RenderSurfaceSize), mode, players);
    var netOptions = new Tunnerer.Desktop.Net.NetLaunchOptions(relayUri, room, playerName, players, relaySecret);
    try
    {
        // Started again by a player whose connection broke: their slot may still be held by the dead connection.
        relay = DesktopDisplayOptions.ParseFlag(Environment.GetEnvironmentVariable(RejoinVariable))
            ? Tunnerer.Desktop.Net.NetLaunch.Rejoin(netOptions, proposed, Tunnerer.Desktop.Net.NetLaunch.RejoinPatience)
            : Tunnerer.Desktop.Net.NetLaunch.Join(netOptions, proposed);
    }
    catch (Tunnerer.Desktop.Net.RelayException e)
    {
        Console.WriteLine($"[Net] {e.Message}");
        return;
    }
    terrainSize = new Size(relay.Config.MapWidth, relay.Config.MapHeight);
    mode = (LevelGenMode)relay.Config.GenMode;
}

using (relay)
{
    var game = new Tunnerer.Desktop.Game(terrainSize, mode, perfOptions, enableAiTanks,
        terrainTestMode: terrainTestMode, fogOfWar: fogOfWar, relay: relay);
    game.Run();
    restartRequested = game.RestartRequested;
    rejoin = game.RejoinRequested;
}

// Enter after a match ends starts a new match, and Enter after losing the connection comes back to the match. The renderer is not built to be created twice in one process,
// so the new match runs in a fresh process with the same arguments (the old window has been closed by now).
if (restartRequested && Environment.ProcessPath is { } exePath)
{
    var start = new System.Diagnostics.ProcessStartInfo(exePath) { UseShellExecute = false };
    foreach (var arg in args)
        start.ArgumentList.Add(arg);
    start.Environment[RejoinVariable] = rejoin ? "1" : "0";
    System.Diagnostics.Process.Start(start);
}


