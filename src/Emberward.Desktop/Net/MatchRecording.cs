namespace Emberward.Desktop.Net;

using System.Collections.Immutable;
using System.Globalization;
using Emberward.Core;
using Emberward.Core.Bots;
using Emberward.Core.LevelGen;
using Emberward.Core.Snapshots;
using Emberward.Core.Types;
using Emberward.Protocol;

/// <summary>
/// A network match as data: its settings, which slots players drove, and every tick's inputs. That is all a match
/// is, so replaying it rebuilds the exact world, which is how a desync is examined afterwards. A player who joined
/// the match in progress records from the snapshot it loaded (docs/late-join-design.md §6.2).
/// </summary>
/// <remarks>
/// As text: <c>key=value</c> header lines, then one line per tick with each slot's input in hexadecimal. A line
/// <c>mask=</c> among the ticks gives the human mask (in hexadecimal) from the next tick on; the header's
/// <c>humanMask</c> is the mask before the first. A recording from a snapshot has it in the header as
/// <c>snapshot=</c>, compressed and in base 64, with <c>firstTick</c>, the tick it was saved at. Lines starting with
/// <c>#</c> are notes and are skipped on loading.
/// </remarks>
/// <param name="HumanMask">Which slots players drove before the first recorded tick.</param>
/// <param name="Bundles">Every tick from the first recorded one, each with its human mask.</param>
/// <param name="Snapshot">The compressed snapshot the recording starts from, or null for one from tick 0.</param>
public sealed record MatchRecording(
    MatchConfig Config, int HumanMask, IReadOnlyList<TickBundle> Bundles, byte[]? Snapshot = null)
{
    public void Save(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var inv = CultureInfo.InvariantCulture;
        writer.WriteLine(string.Create(inv, $"seed={Config.Seed}"));
        writer.WriteLine(string.Create(inv, $"map={Config.MapWidth}x{Config.MapHeight}"));
        writer.WriteLine(string.Create(inv, $"genMode={Config.GenMode}"));
        writer.WriteLine(string.Create(inv, $"slots={Config.Slots}"));
        writer.WriteLine(string.Create(inv, $"ticksPerSecond={Config.TicksPerSecond}"));
        writer.WriteLine(string.Create(inv, $"inputKeyMask={Config.InputKeyMask:X}"));
        writer.WriteLine(string.Create(inv, $"humanMask={HumanMask}"));
        if (Snapshot != null)
        {
            writer.WriteLine(string.Create(inv, $"firstTick={(Bundles.Count > 0 ? Bundles[0].Tick : 0)}"));
            writer.WriteLine("snapshot=" + Convert.ToBase64String(Snapshot));
        }
        writer.WriteLine("ticks:");
        int mask = HumanMask;
        foreach (TickBundle bundle in Bundles)
        {
            if (bundle.HumanMask != mask)
            {
                mask = bundle.HumanMask;
                writer.WriteLine(string.Create(inv, $"mask={mask:X}"));
            }
            writer.WriteLine(string.Join(' ', bundle.Inputs.Select(input => input.ToString("X", inv))));
        }
    }

    /// <exception cref="InvalidDataException">The text is not a recording.</exception>
    public static MatchRecording Load(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var header = new Dictionary<string, string>(StringComparer.Ordinal);
        var bundles = new List<TickBundle>();
        bool inTicks = false;
        const string MaskLine = "mask=";
        try
        {
            int Number(string key) => int.Parse(header[key], CultureInfo.InvariantCulture);
            int firstTick = 0, mask = 0;
            while (reader.ReadLine() is { } line)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                if (inTicks && line.StartsWith(MaskLine, StringComparison.Ordinal))
                    mask = int.Parse(line.AsSpan(MaskLine.Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                else if (inTicks)
                {
                    var inputs = line.Split(' ').Select(x => uint.Parse(x, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    bundles.Add(new TickBundle(firstTick + bundles.Count, [.. inputs], mask));
                }
                else if (line == "ticks:")
                {
                    inTicks = true;
                    mask = Number("humanMask");
                    firstTick = header.ContainsKey("firstTick") ? Number("firstTick") : 0;
                }
                else if (line.Split('=', 2) is [var key, var value])
                    header[key] = value;
            }

            string[] map = header["map"].Split('x');
            var config = new MatchConfig(
                Number("seed"), int.Parse(map[0], CultureInfo.InvariantCulture), int.Parse(map[1], CultureInfo.InvariantCulture),
                Number("genMode"), Number("slots"), Number("ticksPerSecond"),
                uint.Parse(header["inputKeyMask"], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            byte[]? snapshot = header.TryGetValue("snapshot", out string? base64) ? Convert.FromBase64String(base64) : null;
            return new MatchRecording(config, Number("humanMask"), bundles, snapshot);
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
    /// Plays the match again from the start (or from the snapshot) and returns the world after the first
    /// <paramref name="ticks"/> recorded ticks (all of them by default), with the bots playing in it then.
    /// <paramref name="afterTick"/> is called after each one.
    /// </summary>
    public (World World, IReadOnlyList<BotTankAI?> Bots) Replay(int? ticks = null, Action<World>? afterTick = null)
    {
        World world = BuildWorld(Config);
        Seats seats = Snapshot is { } snapshot
            ? new Seats(Config.Seed, WorldSnapshot.Load(world, WorldSnapshot.Decompress(snapshot)))
            : Seats.ForStart(world, Config.Seed, HumanMask);
        int count = Math.Min(ticks ?? Bundles.Count, Bundles.Count);
        for (int i = 0; i < count; i++)
        {
            seats.Step(world, Bundles[i]);
            afterTick?.Invoke(world);
        }
        return (world, seats.Bots);
    }

    /// <summary>The same recording cut to its first <paramref name="ticks"/> ticks.</summary>
    public MatchRecording Truncated(int ticks) => this with { Bundles = Bundles.Take(ticks).ToImmutableArray() };
}
