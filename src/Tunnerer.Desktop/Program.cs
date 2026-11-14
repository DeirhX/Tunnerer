using Tunnerer.Core.LevelGen;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Config;

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
    else if (arg == "--no-ai" || arg == "--disable-ai")
    {
        enableAiTanks = false;
    }
}

DesktopDisplayOptions.DisplayIndex = displayIndex;
DesktopDisplayOptions.WindowedSize = windowedSize;
DesktopDisplayOptions.Hidden = hiddenWindow;
var mode = fastGen ? LevelGenMode.Optimized : LevelGenMode.Deterministic;
Tunnerer.Desktop.PerfCaptureOptions? perfOptions = perfMode
    ? new Tunnerer.Desktop.PerfCaptureOptions(perfWarmupFrames, perfMeasureFrames, perfCsvPath)
    : null;
var game = new Tunnerer.Desktop.Game(terrainSize, mode, perfOptions, renderBackend, enableAiTanks,
    terrainTestMode: terrainTestMode);
game.Run();


