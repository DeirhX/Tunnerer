namespace Tunnerer.Desktop.Net;

using System.Collections.Immutable;
using System.Globalization;
using Tunnerer.Core;
using Tunnerer.Core.LevelGen;
using Tunnerer.Core.Types;
using Tunnerer.Protocol;

/// <summary>
/// A network match as data: its settings, which slots players drove, and every tick's inputs. That is all a match
/// is, so replaying it rebuilds the exact world, which is how a desync is examined afterwards.
/// </summary>
/// <remarks>
/// As text: <c>key=value</c> header lines, then one line per tick with each slot's input in hexadecimal. Lines
/// starting with <c>#</c> are notes and are skipped on loading.
/// </remarks>
public sealed record MatchRecording(MatchConfig Config, int HumanMask, IReadOnlyList<TickBundle> Bundles)
{
    public void Save(TextWriter writer)
    {
        var inv = CultureInfo.InvariantCulture;
        writer.WriteLine(string.Create(inv, $"seed={Config.Seed}"));
        writer.WriteLine(string.Create(inv, $"map={Config.MapWidth}x{Config.MapHeight}"));
        writer.WriteLine(string.Create(inv, $"genMode={Config.GenMode}"));
        writer.WriteLine(string.Create(inv, $"slots={Config.Slots}"));
        writer.WriteLine(string.Create(inv, $"ticksPerSecond={Config.TicksPerSecond}"));
        writer.WriteLine(string.Create(inv, $"inputKeyMask={Config.InputKeyMask:X}"));
        writer.WriteLine(string.Create(inv, $"humanMask={HumanMask}"));
        writer.WriteLine("ticks:");
        foreach (TickBundle bundle in Bundles)
            writer.WriteLine(string.Join(' ', bundle.Inputs.Select(input => input.ToString("X", inv))));
    }

    /// <exception cref="InvalidDataException">The text is not a recording.</exception>
    public static MatchRecording Load(TextReader reader)
    {
        var header = new Dictionary<string, string>(StringComparer.Ordinal);
        var bundles = new List<TickBundle>();
        bool inTicks = false;
        try
        {
            while (reader.ReadLine() is { } line)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                if (inTicks)
                {
                    var inputs = line.Split(' ').Select(x => uint.Parse(x, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    bundles.Add(new TickBundle(bundles.Count, [.. inputs]));
                }
                else if (line == "ticks:")
                    inTicks = true;
                else if (line.Split('=', 2) is [var key, var value])
                    header[key] = value;
            }

            int Number(string key) => int.Parse(header[key], CultureInfo.InvariantCulture);
            string[] map = header["map"].Split('x');
            var config = new MatchConfig(
                Number("seed"), int.Parse(map[0], CultureInfo.InvariantCulture), int.Parse(map[1], CultureInfo.InvariantCulture),
                Number("genMode"), Number("slots"), Number("ticksPerSecond"),
                uint.Parse(header["inputKeyMask"], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            return new MatchRecording(config, Number("humanMask"), bundles);
        }
        catch (Exception e) when (e is KeyNotFoundException or FormatException or IndexOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException("This is not a match recording: " + e.Message, e);
        }
    }

    /// <summary>The world a client builds for these settings, before the first tick.</summary>
    public static World BuildWorld(MatchConfig config) => WorldFactory.Build(new WorldLaunchOptions(
        TerrainTest: false, new Size(config.MapWidth, config.MapHeight), (LevelGenMode)config.GenMode, config.Seed,
        Players: config.Slots, CoreTapWins: true)).World;

    /// <summary>
    /// Plays the match again from the start and returns the world after <paramref name="ticks"/> ticks (all of
    /// them by default). <paramref name="afterTick"/> is called after each one.
    /// </summary>
    public World Replay(int? ticks = null, Action<World>? afterTick = null)
    {
        World world = BuildWorld(Config);
        var bots = NetSession.CreateBots(world, Config.Seed, HumanMask);
        int count = Math.Min(ticks ?? Bundles.Count, Bundles.Count);
        for (int i = 0; i < count; i++)
        {
            NetSession.Step(world, bots, Bundles[i]);
            afterTick?.Invoke(world);
        }
        return world;
    }

    /// <summary>The same recording cut to its first <paramref name="ticks"/> ticks.</summary>
    public MatchRecording Truncated(int ticks) => this with { Bundles = Bundles.Take(ticks).ToImmutableArray() };
}
