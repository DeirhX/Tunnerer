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
          --fast              Parallel level generation and simulation (not reproducible).

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
          --backend=dx11      Render backend.

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
RenderBackendKind? renderBackend = null;
bool enableAiTanks = true;
int? displayIndex = DesktopDisplayOptions.Parse(Environment.GetEnvironmentVariable("TUNNERER_DISPLAY"));
Size? windowedSize = DesktopDisplayOptions.ParseWindowed(Environment.GetEnvironmentVariable("TUNNERER_WINDOWED"));
bool hiddenWindow = DesktopDisplayOptions.ParseFlag(Environment.GetEnvironmentVariable("TUNNERER_HIDDEN"));
float? volume = AudioOptions.ParseVolume(Environment.GetEnvironmentVariable("TUNNERER_VOLUME"));
bool mute = DesktopDisplayOptions.ParseFlag(Environment.GetEnvironmentVariable("TUNNERER_MUTE"));
string? audioCapturePath = Environment.GetEnvironmentVariable("TUNNERER_AUDIO_CAPTURE");
string? dumpSoundsDirectory = null;
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
    else if (arg.StartsWith("--perf-warmup=") && int.TryParse(arg[14..], out int warmup))
        perfWarmupFrames = Math.Max(0, warmup);
    else if (arg.StartsWith("--perf-frames=") && int.TryParse(arg[14..], out int frames))
        perfMeasureFrames = Math.Max(1, frames);
    else if (arg.StartsWith("--perf-csv="))
        perfCsvPath = arg[11..];
    else if (arg.StartsWith("--size="))
    {
        var parts = arg[7..].Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
            terrainSize = new Size(w, h);
    }
    else if (arg.StartsWith("--backend="))
    {
        var backendRaw = arg[10..].Trim();
        if (backendRaw.Equals("dx11", StringComparison.OrdinalIgnoreCase))
            renderBackend = RenderBackendKind.Dx11;
        else
            Console.WriteLine($"[Args] Unknown backend '{backendRaw}', expected dx11. Using default.");
    }
    else if (arg == "--windowed" || arg.StartsWith("--windowed="))
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
    else if (arg.StartsWith("--display="))
    {
        var parsed = DesktopDisplayOptions.Parse(arg[10..]);
        if (parsed is null)
            Console.WriteLine($"[Args] Unknown display '{arg[10..]}', expected a number or 'secondary'. Using the default.");
        else
            displayIndex = parsed;
    }
    else if (arg.StartsWith("--volume="))
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
    else if (arg.StartsWith("--dump-sounds="))
    {
        dumpSoundsDirectory = arg[14..];
    }
    else if (arg == "--no-ai" || arg == "--disable-ai")
    {
        enableAiTanks = false;
    }
    else if (arg.StartsWith("--seed="))
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
var mode = fastGen ? LevelGenMode.Optimized : LevelGenMode.Deterministic;
Tunnerer.Desktop.PerfCaptureOptions? perfOptions = perfMode
    ? new Tunnerer.Desktop.PerfCaptureOptions(perfWarmupFrames, perfMeasureFrames, perfCsvPath)
    : null;
var game = new Tunnerer.Desktop.Game(terrainSize, mode, perfOptions, renderBackend, enableAiTanks,
    terrainTestMode: terrainTestMode);
game.Run();

// Enter after a match ends starts a new match. The renderer is not built to be created twice in one process,
// so the new match runs in a fresh process with the same arguments (the old window has been closed by now).
if (game.RestartRequested && Environment.ProcessPath is { } exePath)
{
    var start = new System.Diagnostics.ProcessStartInfo(exePath) { UseShellExecute = false };
    foreach (var arg in args)
        start.ArgumentList.Add(arg);
    System.Diagnostics.Process.Start(start);
}


