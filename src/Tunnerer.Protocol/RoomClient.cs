namespace Tunnerer.Protocol;

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

/// <summary>
/// One client's side of its room on the relay, without the socket: what the relay has said (the slot, the roster,
/// the start, the pause, the round trip, a desync or a refusal), the bundles it has sent, and the messages this
/// client sends back. The desktop's connection feeds it from a WebSocket on background tasks; the browser feeds it
/// from the page's WebSocket. Not thread-safe: call it from one thread.
/// </summary>
public sealed class RoomClient : ILockstepLink
{
    /// <summary>A ping is sent this often while the last one has been answered.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);

    /// <summary>A ping unanswered this long is given up on, and the next one sent.</summary>
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);

    private readonly Action<Message> _send;
    private readonly Queue<TickBundle> _bundles = new();
    private TimeSpan _pingSentAt;
    private uint _pingId;
    private bool _pingPending;

    /// <param name="hello">What this client said to get in.</param>
    /// <param name="welcome">The relay's answer.</param>
    /// <param name="send">Sends a message to the relay; never blocks.</param>
    public RoomClient(Hello hello, Welcome welcome, Action<Message> send)
    {
        ArgumentNullException.ThrowIfNull(hello);
        ArgumentNullException.ThrowIfNull(welcome);
        Room = hello.Room;
        RequestedName = hello.Name;
        Slot = welcome.Slot;
        Config = welcome.Config;
        _send = send;
    }

    /// <summary>The room this client is in.</summary>
    public string Room { get; }

    /// <summary>The name this client asked for in its hello.</summary>
    public string RequestedName { get; }

    /// <summary>
    /// The name the relay gave this client, which it must use to come back to the match. It differs from
    /// <see cref="RequestedName"/> when someone in the room already had the name ("Ann (2)"). Null until the relay's
    /// first roster has arrived.
    /// </summary>
    public string? AssignedName => Slot < Roster.Length && Roster[Slot].Length > 0 ? Roster[Slot] : null;

    /// <summary>The slot the relay gave this client: which tank it drives.</summary>
    public int Slot { get; }

    /// <summary>The match every client in the room builds.</summary>
    public MatchConfig Config { get; }

    /// <summary>The players' names by slot; an empty name is an empty slot.</summary>
    public ImmutableArray<string> Roster { get; private set; } = [];

    /// <summary>True once the relay has started the match: ticks are coming.</summary>
    public bool Started { get; private set; }

    /// <summary>Once started: bit n is set when a player drives slot n; the other slots are for bots.</summary>
    public int HumanMask { get; private set; }

    /// <summary>The slot of the host, who may start the match before the room is full: the lowest slot taken.</summary>
    public int HostSlot => RoomRules.HostSlot(Roster.Length, slot => Roster[slot].Length > 0);

    public bool IsHost => HostSlot == Slot;

    /// <summary>How many players are in the room now.</summary>
    public int PlayersHere => Roster.Count(name => name.Length > 0);

    /// <summary>True when this client came back to a match that was already running, and is replaying it.</summary>
    public bool Rejoined { get; private set; }

    /// <summary>True while the match is held for everyone; no ticks come until someone resumes it.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>The slot of whoever paused or resumed last.</summary>
    public int PausedBySlot { get; private set; }

    /// <summary>The name of whoever paused or resumed last, or "a player".</summary>
    public string PausedByName =>
        PausedBySlot >= 0 && PausedBySlot < Roster.Length && Roster[PausedBySlot].Length > 0
            ? Roster[PausedBySlot]
            : "a player";

    /// <summary>The last measured round trip to the relay and back, in milliseconds; null before the first.</summary>
    public int? LatencyMs { get; private set; }

    /// <summary>The tick at which the relay found two clients' worlds different, if it has.</summary>
    public int? DesyncTick { get; private set; }

    /// <summary>Why the connection is over (refused, or lost), or null while it is alive.</summary>
    public string? Failure { get; private set; }

    /// <summary>Takes in one message from the relay, which arrived at <paramref name="arrived"/> by the client's clock.</summary>
    public void Accept(Message message, TimeSpan arrived)
    {
        switch (message)
        {
            case Tick tick: _bundles.Enqueue(tick.Bundle); break;
            case Roster roster: Roster = roster.Names; break;
            case Start start:
                Started = true;
                HumanMask = start.HumanMask;
                break;
            case Rejoined rejoined:
                Started = true;
                Rejoined = true;
                HumanMask = rejoined.HumanMask;
                break;
            case Log log:
                foreach (var bundle in log.Bundles())
                    _bundles.Enqueue(bundle);
                break;
            case PauseState pause:
                IsPaused = pause.Paused;
                PausedBySlot = pause.BySlot;
                break;
            case Pong pong when _pingPending && pong.Id == _pingId:
                _pingPending = false;
                // Timed to when the answer arrived, not to when the game loop got round to reading it.
                LatencyMs = (int)Math.Round((arrived - _pingSentAt).TotalMilliseconds);
                break;
            case Desync desync: DesyncTick ??= desync.AtTick; break;
            case Refusal refusal: Failure ??= refusal.Reason; break;
        }
    }

    /// <summary>The connection ended, for <paramref name="reason"/> (for the player). The first reason given stands.</summary>
    public void Lose(string reason) => Failure ??= reason;

    /// <summary>
    /// Sends a ping when one is due: one a second, and a lost one is given up on after five. Call once per frame
    /// with the clock <see cref="Accept"/> is given.
    /// </summary>
    public void KeepTime(TimeSpan now)
    {
        TimeSpan sincePing = now - _pingSentAt;
        if (Failure == null && (_pingPending ? sincePing > PingTimeout : sincePing > PingInterval || _pingId == 0))
        {
            _pingSentAt = now;
            _pingPending = true;
            _send(new Ping(++_pingId));
        }
    }

    public void SendReady(ulong worldHash) => _send(new Ready(worldHash));

    /// <summary>Asks the relay to hold the match for everyone, or to let it run again.</summary>
    public void SetPaused(bool paused) => _send(new SetPaused(paused));

    /// <summary>Asks the relay to start with the players who are here. Only the host's request counts.</summary>
    public void RequestStart() => _send(new StartRequest());

    public void SendInput(uint input) => _send(new Input(input));

    public void SendHash(int tick, ulong hash) => _send(new Hash(tick, hash));

    public void SendMatchOver(int tick) => _send(new MatchOver(tick));

    public bool TryReceive([NotNullWhen(true)] out TickBundle? bundle) => _bundles.TryDequeue(out bundle);
}
