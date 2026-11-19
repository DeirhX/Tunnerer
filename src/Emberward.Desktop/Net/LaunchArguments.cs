namespace Emberward.Desktop.Net;

using System.Globalization;
using Emberward.Core.Types;
using Emberward.Desktop.Config;

/// <summary>
/// What the command line (and the <c>EMBERWARD_*</c> variables that stand in for some of its options) asked for.
/// <see cref="Parse"/> only reads: it prints nothing and sets no global, so tests can run it and the caller decides
/// what to do with the result.
/// </summary>
public sealed record LaunchArguments
{
    // Match
    public Size? TerrainSize { get; init; }
    public bool FastGen { get; init; }
    public bool EnableAiTanks { get; init; } = true;
    public bool FogOfWar { get; init; } = true;

    /// <summary>The map and simulation seed (<c>--seed</c>); null lets the game choose.</summary>
    public int? Seed { get; init; }

    // Network match
    /// <summary>The relay of a network match (<c>--relay</c>, or the default relay for <c>--net</c>); null plays locally.</summary>
    public Uri? Relay { get; init; }

    public string Room { get; init; } = DefaultRoom;
    public string Name { get; init; } = Environment.UserName;
    public int Players { get; init; } = NetworkTweaks.DefaultPlayers;

    /// <summary>The relay's secret from <c>--secret</c> or <c>EMBERWARD_RELAY_SECRET</c>.</summary>
    public string Secret { get; init; } = string.Empty;

    // Display and sound
    public int? DisplayIndex { get; init; }
    public Size? WindowedSize { get; init; }
    public bool Hidden { get; init; }
    public float? Volume { get; init; }
    public bool Mute { get; init; }

    // Development
    public bool Perf { get; init; }
    public int PerfWarmupFrames { get; init; } = 180;
    public int PerfMeasureFrames { get; init; } = 900;
    public string? PerfCsvPath { get; init; }
    public bool TerrainTest { get; init; }
    public bool EnergyAudit { get; init; }
    public string? DumpSoundsDirectory { get; init; }

    /// <summary>The room <c>--net</c> without a name joins.</summary>
    public const string DefaultRoom = "lobby";

    /// <summary>The network match this asks for, or null for a local one.</summary>
    public NetLaunchOptions? Net =>
        Relay is null ? null : new NetLaunchOptions(Relay, Room, Name, Players, Secret, TerrainSize, Seed);

    /// <summary>
    /// Reads the arguments, on top of what the environment variables say. An option that makes no sense is left out
    /// and a line about it, ready to print, goes to <paramref name="warnings"/>.
    /// </summary>
    /// <param name="environment">Looks up an environment variable; null when it is not set.</param>
    public static LaunchArguments Parse(
        IEnumerable<string> args, Func<string, string?>? environment = null, ICollection<string>? warnings = null)
    {
        environment ??= _ => null;
        var result = new LaunchArguments
        {
            DisplayIndex = DesktopDisplayOptions.Parse(environment("EMBERWARD_DISPLAY")),
            WindowedSize = DesktopDisplayOptions.ParseWindowed(environment("EMBERWARD_WINDOWED")),
            Hidden = DesktopDisplayOptions.ParseFlag(environment("EMBERWARD_HIDDEN")),
            Volume = AudioOptions.ParseVolume(environment("EMBERWARD_VOLUME")),
            Mute = DesktopDisplayOptions.ParseFlag(environment("EMBERWARD_MUTE")),
            Secret = environment(NetLaunchOptions.SecretVariable) ?? string.Empty,
        };
        foreach (string arg in args)
            result = result.WithOption(arg, warnings);
        return result;
    }

    private LaunchArguments WithOption(string arg, ICollection<string>? warnings)
    {
        void Warn(string text) => warnings?.Add("[Args] " + text);

        if (arg == "--large")
            return this with { TerrainSize = MapPresets.LargeSize };
        if (arg == "--fast")
            return this with { FastGen = true };
        if (arg == "--perf")
            return this with { Perf = true };
        if (arg == "--terrain-test")
            return this with { TerrainTest = true };
        if (arg.StartsWith("--perf-warmup=", StringComparison.Ordinal) && int.TryParse(arg[14..], out int warmup))
            return this with { PerfWarmupFrames = Math.Max(0, warmup) };
        if (arg.StartsWith("--perf-frames=", StringComparison.Ordinal) && int.TryParse(arg[14..], out int frames))
            return this with { PerfMeasureFrames = Math.Max(1, frames) };
        if (arg.StartsWith("--perf-csv=", StringComparison.Ordinal))
            return this with { PerfCsvPath = arg[11..] };
        if (arg.StartsWith("--size=", StringComparison.Ordinal))
        {
            var parts = arg[7..].Split('x');
            return parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h)
                ? this with { TerrainSize = new Size(w, h) }
                : this;
        }
        if (arg == "--windowed" || arg.StartsWith("--windowed=", StringComparison.Ordinal))
        {
            string value = arg.Length > "--windowed".Length ? arg["--windowed=".Length..] : string.Empty;
            var parsed = DesktopDisplayOptions.ParseWindowed(value, flagPresent: true);
            if (parsed is null)
            {
                Warn($"Unknown window size '{value}', expected WxH (at least 320x200). Using fullscreen.");
                return this;
            }
            return this with { WindowedSize = parsed };
        }
        if (arg == "--hidden")
            return this with { Hidden = true };
        if (arg.StartsWith("--display=", StringComparison.Ordinal))
        {
            var parsed = DesktopDisplayOptions.Parse(arg[10..]);
            if (parsed is null)
            {
                Warn($"Unknown display '{arg[10..]}', expected a number or 'secondary'. Using the default.");
                return this;
            }
            return this with { DisplayIndex = parsed };
        }
        if (arg.StartsWith("--volume=", StringComparison.Ordinal))
        {
            var parsed = AudioOptions.ParseVolume(arg[9..]);
            if (parsed is null)
            {
                Warn($"Unknown volume '{arg[9..]}', expected a number from 0 to 100. Using the default.");
                return this;
            }
            return this with { Volume = parsed };
        }
        if (arg == "--mute")
            return this with { Mute = true };
        if (arg.StartsWith("--dump-sounds=", StringComparison.Ordinal))
            return this with { DumpSoundsDirectory = arg[14..] };
        if (arg is "--no-ai" or "--disable-ai")
            return this with { EnableAiTanks = false };
        if (arg == "--no-fog")
            return this with { FogOfWar = false };
        if (arg == "--energy-audit")
            return this with { EnergyAudit = true };
        if (arg.StartsWith("--relay=", StringComparison.Ordinal))
        {
            if (Uri.TryCreate(arg[8..], UriKind.Absolute, out Uri? parsed) && parsed.Scheme is "ws" or "wss")
                return this with { Relay = parsed };
            Warn($"Unknown relay '{arg[8..]}', expected a ws:// or wss:// address. Playing a local match.");
            return this;
        }
        if (arg == "--net" || arg.StartsWith("--net=", StringComparison.Ordinal))
        {
            var next = this with { Relay = Relay ?? new Uri(NetworkTweaks.DefaultRelay) };
            return arg.Length > 6 ? next with { Room = arg[6..] } : next;
        }
        if (arg.StartsWith("--room=", StringComparison.Ordinal) && arg.Length > 7)
            return this with { Room = arg[7..] };
        if (arg.StartsWith("--name=", StringComparison.Ordinal) && arg.Length > 7)
            return this with { Name = arg[7..] };
        if (arg.StartsWith("--secret=", StringComparison.Ordinal))
            return this with { Secret = arg[9..] };
        if (arg.StartsWith("--players=", StringComparison.Ordinal))
        {
            if (int.TryParse(arg[10..], out int count) && count is >= 2 and <= Emberward.Core.Config.Tweaks.World.MaxPlayers)
                return this with { Players = count };
            Warn($"Unknown player count '{arg[10..]}', expected 2 to 8. Using {Players.ToString(CultureInfo.InvariantCulture)}.");
            return this;
        }
        if (arg.StartsWith("--seed=", StringComparison.Ordinal))
        {
            if (int.TryParse(arg[7..], out int seedValue) && seedValue != 0)
                return this with { Seed = seedValue };
            Warn($"Unknown seed '{arg[7..]}', expected a non-zero whole number. Using a new map.");
            return this;
        }
        Warn($"Unknown option '{arg}' ignored. Run with --help to list the options.");
        return this;
    }

    /// <summary>
    /// The arguments that start the game again with the same display and sound options, in the network match
    /// <paramref name="net"/> or, when it is null, in a local match. The match's own options (map size, seed, room)
    /// come from <paramref name="net"/> only; development options and the secret are not carried (the secret goes
    /// through <see cref="NetLaunchOptions.SecretVariable"/>).
    /// </summary>
    public IReadOnlyList<string> Relaunch(NetLaunchOptions? net)
    {
        var args = new List<string>();
        if (WindowedSize is { } windowed)
            args.Add(string.Create(CultureInfo.InvariantCulture, $"--windowed={windowed.X}x{windowed.Y}"));
        if (DisplayIndex is { } display)
            args.Add(string.Create(CultureInfo.InvariantCulture, $"--display={display}"));
        if (Hidden)
            args.Add("--hidden");
        if (Volume is { } volume)
            args.Add(string.Create(CultureInfo.InvariantCulture, $"--volume={(int)MathF.Round(volume * 100f)}"));
        if (Mute)
            args.Add("--mute");
        if (!EnableAiTanks)
            args.Add("--no-ai");
        if (!FogOfWar)
            args.Add("--no-fog");
        if (net is not null)
            args.AddRange(net.ToArguments());
        return args;
    }
}
