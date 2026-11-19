namespace Emberward.Desktop.Net;

using System.Reflection;
using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Input;
using Emberward.Core.LevelGen;
using Emberward.Core.Types;
using Emberward.Protocol;

/// <summary>What a client says about itself and the match it proposes when it joins a room: the desktop and the browser alike.</summary>
public static class NetMatch
{
    /// <summary>
    /// Identifies the simulation build: the version of the Core assembly, which carries the source commit. Clients
    /// of different builds would not simulate alike, so the relay keeps them out of one room. The desktop and the
    /// browser built from one commit have the same id, and compute the same world.
    /// </summary>
    public static string BuildId =>
        typeof(World).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(World).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>The match this client proposes if it turns out to be the one that opens the room.</summary>
    public static MatchConfig ProposeConfig(int seed, Size mapSize, LevelGenMode genMode, int players) => new(
        seed, mapSize.X, mapSize.Y, (int)genMode, players, Tweaks.Perf.TargetFps, ControllerOutputCodec.KeyMask);

    /// <summary>
    /// Why this client cannot build the room's match, or null when it can. The relay passes the settings through
    /// without reading the level generator, which this client must know.
    /// </summary>
    public static string? CannotBuild(MatchConfig config) =>
        Enum.IsDefined((LevelGenMode)config.GenMode)
            ? null
            : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"The room's level generator ({config.GenMode}) is not one this game knows. Update the game.");
}
