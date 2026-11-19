namespace Emberward.Desktop.Net;

using System.Globalization;
using Emberward.Core.Types;
using Emberward.Protocol;

/// <summary>
/// What the player asked for about a network match, from the command line or from the network menu.
/// </summary>
/// <param name="Players">How many tanks the match has, if this client is the one that opens the room.</param>
/// <param name="Secret">The relay's shared secret, or empty for an open relay. Never put on a command line.</param>
/// <param name="MapSize">The map this client proposes when it opens the room; null for the default of the player count.</param>
/// <param name="Seed">The seed this client proposes when it opens the room; null for a new one each match.</param>
public sealed record NetLaunchOptions(
    Uri Relay, string Room, string Name, int Players, string Secret = "", Size? MapSize = null, int? Seed = null)
{
    /// <summary>The environment variable that carries the relay's secret to the game, and on to a restarted one.</summary>
    public const string SecretVariable = "EMBERWARD_RELAY_SECRET";

    /// <summary>
    /// The command-line arguments that start the game in this match (<see cref="LaunchArguments.Parse"/> reads
    /// them back). The secret is not among them: a command line is visible to every user of the machine
    /// (<c>ps</c>), so <see cref="SecretVariable"/> in the environment carries it.
    /// </summary>
    public IEnumerable<string> ToArguments()
    {
        yield return "--relay=" + Relay.AbsoluteUri;
        yield return "--room=" + Room;
        yield return "--name=" + Name;
        yield return string.Create(CultureInfo.InvariantCulture, $"--players={Players}");
        if (MapSize is { } size)
            yield return string.Create(CultureInfo.InvariantCulture, $"--size={size.X}x{size.Y}");
        if (Seed is { } seed)
            yield return string.Create(CultureInfo.InvariantCulture, $"--seed={seed}");
    }
}

/// <summary>Joins a room on the relay from the command line's options, before the game window opens.</summary>
public static class NetLaunch
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a player coming back after a lost connection keeps trying to get in.</summary>
    public static readonly TimeSpan RejoinPatience = TimeSpan.FromSeconds(40);

    /// <summary>
    /// Joins as <see cref="Join(NetLaunchOptions, MatchConfig)"/> does, trying again for <paramref name="patience"/>
    /// when it fails. A player whose connection broke comes back before the relay has noticed that the old one is
    /// dead (it takes up to twenty seconds), and until then their slot is still taken; the network that broke may
    /// also need a moment.
    /// </summary>
    /// <exception cref="RelayException">Still refused or unreachable when the patience ran out.</exception>
    public static RelayConnection Rejoin(
        NetLaunchOptions options, MatchConfig proposed, TimeSpan patience, TimeSpan? between = null)
    {
        var tried = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return Join(options, proposed);
            }
            catch (RelayException e) when (tried.Elapsed < patience)
            {
                Console.WriteLine($"[Net] {e.Message} Trying again.");
                Thread.Sleep(between ?? TimeSpan.FromSeconds(2));
            }
        }
    }

    /// <exception cref="RelayException">The relay could not be reached, or it refused the request.</exception>
    public static RelayConnection Join(NetLaunchOptions options, MatchConfig proposed)
    {
        var hello = new Hello(MessageCodec.ProtocolVersion, NetMatch.BuildId, options.Name, options.Room, proposed, options.Secret, SnapshotCapabilities.Upload);
        var connection = RelayConnection.Connect(options.Relay, hello, ConnectTimeout);
        if (NetMatch.CannotBuild(connection.Config) is { } reason)
        {
            connection.Dispose();
            throw new RelayException(reason);
        }
        return connection;
    }
}
