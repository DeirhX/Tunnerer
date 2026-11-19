namespace Emberward.Desktop.Config;

using Emberward.Core.Types;

/// <summary>Defaults for network matches (docs/multiplayer-design.md).</summary>
public static class NetworkTweaks
{
    /// <summary>The relay <c>--net</c> connects to when <c>--relay</c> names no other.</summary>
    public const string DefaultRelay = "wss://crust.bokovka.cz/play";

    /// <summary>Tanks in a network match unless the host asks for another number; bots take the slots nobody joins.</summary>
    public const int DefaultPlayers = 4;

    /// <summary>
    /// The map of a network match with more than two tanks, unless the host passes --size. Every player's machine
    /// must simulate it in real time, so it is kept modest: about twice the cells of the single-player map.
    /// </summary>
    public static readonly Size LargeMatchMapSize = new(480, 300);

    /// <summary>
    /// Ticks received and not yet simulated beyond which the player is told their machine is not keeping up
    /// (two seconds' worth).
    /// </summary>
    public const int BehindWarningTicks = 48;

    /// <summary>
    /// Ticks received and not yet simulated beyond which the client stops playing at the normal rate and replays as
    /// fast as it can (one second's worth): it has just rejoined, or it stalled.
    /// </summary>
    public const int CatchUpTicks = 24;

    /// <summary>Time one frame may spend replaying ticks while catching up, so the window stays responsive.</summary>
    public const int CatchUpBudgetMs = 25;
}
