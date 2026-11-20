using Emberward.Core.LevelGen;
using Emberward.Core.Types;
using Emberward.Desktop.Config;
using Emberward.Desktop.Net;

// Numbers in logs, CSV files and scripts use '.' as the decimal separator whatever the system language is.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

if (args.Any(a => a is "--help" or "-h" or "-?" or "/?"))
{
    Console.WriteLine("""
        Emberflow - a 2D tunnelling tank game

        Usage: Emberward.Desktop [options]

        Match
          --seed=N            Play the map with seed N (shown in the pause menu). Default: a new map each match.
          --size=WxH          Terrain size in cells.
          --large             Terrain size 1500x750.
          --no-ai             No enemy tank.
          --no-fog            No fog of war: the whole map is shown live.
          --fast              Parallel level generation (another map per seed; still reproducible).
          --energy-network    Local matches run the energy network (capacitors, charge labels; G hides them)
                              per-machine upgrades (F next to a relay) and the tank's reserve (X off the
                              network burns carried emberite).

        Network match (see docs/multiplayer-design.md)
          --net[=ROOM]        Join ROOM (default: lobby) on the shared relay. The first player in opens the room
                              and their --seed, --size, --fast and --players apply to everyone.
          --relay=URL         Use the relay at URL instead (ws:// or wss://, ending in /play).
          --room=NAME         The room to join, when --relay is given without --net.
          --players=N         Tanks in the match, 2 to 8 (default 4). Bots take the slots nobody joins.
          --name=NAME         Your name in the room. After dropping out, the same name gets you back in.
          --secret=TEXT       The relay's shared secret, if it has one (or set EMBERWARD_RELAY_SECRET).

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
          --energy-audit      Print the heat energy balance with the simulation profile (sums the whole map).

        In game: W A S D drive and dig, mouse aims, left click or Space fires, E builds a relay,
        M mutes the sound, F1 shows the controls, Esc pauses.
        """);
    return;
}

var launchWarnings = new List<string>();
var launch = LaunchArguments.Parse(args, Environment.GetEnvironmentVariable, launchWarnings);
foreach (string warning in launchWarnings)
    Console.WriteLine(warning);

Size? terrainSize = launch.TerrainSize;
GameLaunchOptions.Seed = launch.Seed;
GameLaunchOptions.EnergyAudit = launch.EnergyAudit;
GameLaunchOptions.EnergyNetwork = launch.EnergyNetwork;
DesktopDisplayOptions.DisplayIndex = launch.DisplayIndex;
DesktopDisplayOptions.WindowedSize = launch.WindowedSize;
DesktopDisplayOptions.Hidden = launch.Hidden;
AudioOptions.Volume = launch.Volume ?? AudioOptions.DefaultVolume;
AudioOptions.Muted = launch.Mute;
string? audioCapturePath = Environment.GetEnvironmentVariable("EMBERWARD_AUDIO_CAPTURE");
AudioOptions.CapturePath = string.IsNullOrWhiteSpace(audioCapturePath) ? null : audioCapturePath.Trim();
if (!string.IsNullOrWhiteSpace(launch.DumpSoundsDirectory))
{
    Emberward.Desktop.Audio.GameAudio.DumpEffects(launch.DumpSoundsDirectory);
    return;
}
bool restartRequested;
bool rejoin;
Emberward.Desktop.Net.RelaunchRequest? relaunch;
// Set for the process a restart starts when the player is coming back to a match after losing the connection.
const string RejoinVariable = "EMBERWARD_NET_REJOIN";
// Set for the process a restart starts when the player left a network match: it opens the network menu.
const string NetMenuVariable = "EMBERWARD_NET_MENU";
var mode = launch.FastGen ? LevelGenMode.Optimized : LevelGenMode.Deterministic;
Emberward.Desktop.PerfCaptureOptions? perfOptions = launch.Perf
    ? new Emberward.Desktop.PerfCaptureOptions(launch.PerfWarmupFrames, launch.PerfMeasureFrames, launch.PerfCsvPath)
    : null;
Emberward.Desktop.Net.RelayConnection? relay = null;
// The relay refused: this process plays a local match, and a restart after it is local too.
bool joinRefused = false;
Emberward.Desktop.Net.NetMenuRequest? openNetMenu =
    DesktopDisplayOptions.ParseFlag(Environment.GetEnvironmentVariable(NetMenuVariable)) ? new NetMenuRequest(Secret: launch.Secret) : null;
if (launch.Net is { } netOptions && !launch.TerrainTest)
{
    // The first player into the room decides the match; everyone builds what the relay hands back.
    var proposed = Emberward.Desktop.Net.NetMatch.ProposeConfig(
        launch.Seed ?? Random.Shared.Next(1, int.MaxValue),
        terrainSize ?? (launch.Players > 2 ? NetworkTweaks.LargeMatchMapSize : DesktopScreenTweaks.RenderSurfaceSize),
        mode, launch.Players);
    try
    {
        // Started again by a player whose connection broke: their slot may still be held by the dead connection.
        relay = DesktopDisplayOptions.ParseFlag(Environment.GetEnvironmentVariable(RejoinVariable))
            ? Emberward.Desktop.Net.NetLaunch.Rejoin(netOptions, proposed, Emberward.Desktop.Net.NetLaunch.RejoinPatience)
            : Emberward.Desktop.Net.NetLaunch.Join(netOptions, proposed);
    }
    catch (Emberward.Desktop.Net.RelayException e)
    {
        Console.WriteLine($"[Net] {e.Message}");
        // A person who started the game from a shortcut would otherwise see nothing at all. They get a local match
        // with the network menu open and the relay's reason on it. Nobody is there to read it in an automated run.
        if (Emberward.Desktop.Game.IsAutomatedRun(perfOptions))
            return;
        openNetMenu = new NetMenuRequest(netOptions, e.Message);
        joinRefused = true;
        // The map the refused launch asked for is not the one the menu's local match needs: no huge map to build
        // just to be able to show a menu.
        terrainSize = null;
        GameLaunchOptions.Seed = null;
    }
    if (relay != null)
    {
        terrainSize = new Size(relay.Config.MapWidth, relay.Config.MapHeight);
        mode = (LevelGenMode)relay.Config.GenMode;
    }
}

using (relay)
{
    var game = new Emberward.Desktop.Game(terrainSize, mode, perfOptions, launch.EnableAiTanks,
        terrainTestMode: launch.TerrainTest, fogOfWar: launch.FogOfWar, relay: relay, openNetMenu: openNetMenu,
        launchSecret: launch.Secret, launchRelay: launch.Relay);
    game.Run();
    restartRequested = game.RestartRequested;
    rejoin = game.RejoinRequested;
    relaunch = game.Relaunch;
}

// Enter after a match ends starts a new match, and Enter after losing the connection comes back to the match. The renderer is not built to be created twice in one process,
// so the new match runs in a fresh process with the same arguments (the old window has been closed by now). The
// network menu asks for other arguments: a match in the room the player chose, or a local one with the menu open.
if (restartRequested && Environment.ProcessPath is { } exePath)
{
    var start = new System.Diagnostics.ProcessStartInfo(exePath) { UseShellExecute = false };
    var restartArgs = relaunch is not null ? launch.Relaunch(relaunch.Net)
        : joinRefused ? launch.Relaunch(null)
        : args;
    foreach (string arg in restartArgs)
        start.ArgumentList.Add(arg);
    start.Environment[RejoinVariable] = rejoin ? "1" : "0";
    start.Environment[NetMenuVariable] = relaunch?.OpenNetMenu == true ? "1" : "0";
    if (relaunch?.Net is { } chosen)
    {
        // The secret goes in the environment, where a process list does not show it.
        if (chosen.Secret.Length > 0)
            start.Environment[NetLaunchOptions.SecretVariable] = chosen.Secret;
        else
            start.Environment.Remove(NetLaunchOptions.SecretVariable);
    }
    else if (relaunch is { Secret.Length: > 0 })
    {
        // Leaving a match: the menu that opens next still has the secret this game was started with.
        start.Environment[NetLaunchOptions.SecretVariable] = relaunch.Secret;
    }
    System.Diagnostics.Process.Start(start);
}
