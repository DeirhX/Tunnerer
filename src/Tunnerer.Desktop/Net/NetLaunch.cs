namespace Tunnerer.Desktop.Net;

using System.Reflection;
using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Input;
using Tunnerer.Core.LevelGen;
using Tunnerer.Core.Types;
using Tunnerer.Protocol;

/// <summary>What the command line asked for about a network match (<c>--relay</c>, <c>--room</c>, ...).</summary>
/// <param name="Players">How many tanks the match has, if this client is the one that opens the room.</param>
/// <param name="Secret">The relay's shared secret, or empty for an open relay.</param>
public sealed record NetLaunchOptions(Uri Relay, string Room, string Name, int Players, string Secret = "");

/// <summary>Joins a room on the relay from the command line's options, before the game window opens.</summary>
public static class NetLaunch
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Identifies the simulation build: the version of the Core assembly, which carries the source commit. Clients
    /// of different builds would not simulate alike, so the relay keeps them out of one room.
    /// </summary>
    public static string BuildId =>
        typeof(World).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(World).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>The match this client proposes if it turns out to be the one that opens the room.</summary>
    public static MatchConfig ProposeConfig(int seed, Size mapSize, LevelGenMode genMode, int players) => new(
        seed, mapSize.X, mapSize.Y, (int)genMode, players, Tweaks.Perf.TargetFps, ControllerOutputCodec.KeyMask);

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
        var hello = new Hello(MessageCodec.ProtocolVersion, BuildId, options.Name, options.Room, proposed, options.Secret);
        var connection = RelayConnection.Connect(options.Relay, hello, ConnectTimeout);
        // The relay passes the settings through without reading the level generator, which this client must know.
        if (!Enum.IsDefined((LevelGenMode)connection.Config.GenMode))
        {
            int genMode = connection.Config.GenMode;
            connection.Dispose();
            throw new RelayException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"The room's level generator ({genMode}) is not one this game knows. Update the game."));
        }
        return connection;
    }
}
