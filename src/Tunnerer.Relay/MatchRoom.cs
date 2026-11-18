namespace Tunnerer.Relay;

using System.Collections.Immutable;
using Tunnerer.Protocol;

/// <summary>Where a room sends a client's messages. Sending never blocks the room.</summary>
public interface IPeer
{
    void Send(Message message);

    /// <summary>
    /// Ends the connection once what was sent has gone out, and ignores anything sent later. The client's own
    /// leaving then takes it out of the room.
    /// </summary>
    void Close();
}

/// <summary>
/// One match on the relay: who is in which slot, the lobby until everyone is ready, then the ticks. It owns no
/// sockets and no timer: the hub passes it messages and tells it how much wall time has gone by
/// (<see cref="AdvanceClock"/>), and the room keeps the match time itself. Every member takes the room's lock, so it can be called from any connection's thread.
/// </summary>
public sealed partial class MatchRoom
{
    private readonly Lock _lock = new();
    private readonly IPeer?[] _peers;
    private readonly string[] _names;
    private readonly ulong?[] _readyHashes;
    private readonly LockstepRoom _lockstep;
    private bool _desyncSent;
    private bool _startRequested;
    private int _humanMask;
    // The time the match has run: not counted while it is paused or nobody is connected.
    private double _matchSeconds;
    private TimeSpan _emptyFor;
    private int _pausedBySlot;
    private int? _overAtTick;
    // Who played each slot when the match started: a player who drops out comes back under the same name.
    private string[] _startNames = [];

    public MatchRoom(string code, string buildId, MatchConfig config)
        : this(code, buildId, config, new SnapshotBudget())
    {
    }

    internal MatchRoom(string code, string buildId, MatchConfig config, SnapshotBudget budget)
    {
        _snapshotBudget = budget;
        _canUpload = new bool[config.Slots];
        _lastHashTick = new int[config.Slots];
        Code = code;
        BuildId = buildId;
        Config = config;
        _peers = new IPeer?[config.Slots];
        _names = new string[config.Slots];
        Array.Fill(_names, string.Empty);
        _readyHashes = new ulong?[config.Slots];
        _lockstep = new LockstepRoom(
            config.Slots, config.InputKeyMask, Math.Max(1, (int)(config.TicksPerSecond * SilentSeconds)));
    }

    /// <summary>
    /// How long a connected slot's last input is repeated while its client sends nothing; then its tank idles
    /// until the client sends again. Longer than any gap an ordinary connection makes, short enough that a tank
    /// whose player's machine has stalled does not drive far.
    /// </summary>
    public const double SilentSeconds = 0.5;

    /// <summary>
    /// How long a finished match goes on closing ticks, for the result screens. After that its clock stands, so a
    /// window left open on the result does not make the room's log grow all night.
    /// </summary>
    public const int SecondsPlayedAfterTheEnd = 30;

    public string Code { get; }
    public string BuildId { get; }
    public MatchConfig Config { get; }

    /// <summary>True from the moment every slot was ready with the same world; ticks are being closed.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>True while a running match is held: no ticks are closed until someone resumes it.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>
    /// True once a client has reported the match decided. A finished room is not kept when empty, nobody can come
    /// back to it, and the next player to ask for its name gets a new room.
    /// </summary>
    public bool IsOver
    {
        get
        {
            lock (_lock)
                return _overAtTick is not null;
        }
    }

    /// <summary>True when nobody is connected. A lobby is then forgotten at once, a running match after a grace period.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_lock)
                return _peers.All(p => p is null);
        }
    }

    /// <summary>How long a running match has had nobody connected; zero while someone is.</summary>
    public TimeSpan EmptyFor
    {
        get
        {
            lock (_lock)
                return _emptyFor;
        }
    }

    public int TicksClosed
    {
        get
        {
            lock (_lock)
                return _lockstep.NextTick;
        }
    }

    /// <summary>
    /// Returns the player slot or -1. A watcher receives Welcome(-1) and an empty refusal. A rejected peer gets a reason.
    /// </summary>
    public int Join(IPeer peer, string name, string buildId, out string refusal, SnapshotCapabilities snapshots = SnapshotCapabilities.None)
    {
        lock (_lock)
        {
            refusal = string.Empty;
            if (!string.Equals(buildId, BuildId, StringComparison.Ordinal))
            {
                refusal = $"This room plays build {BuildId}; you have {buildId}.";
                return -1;
            }
            if (IsRunning)
            {
                int returning = Rejoin(peer, name, out refusal);
                if (returning >= 0)
                    _canUpload[returning] = snapshots.HasFlag(SnapshotCapabilities.Upload);
                else if (snapshots.HasFlag(SnapshotCapabilities.Join))
                    AdmitWatcher(peer, name, snapshots, out refusal);
                return returning;
            }
            int slot = Array.IndexOf(_peers, null);
            if (slot < 0)
            {
                refusal = "This room is full.";
                return -1;
            }
            _peers[slot] = peer;
            _canUpload[slot] = snapshots.HasFlag(SnapshotCapabilities.Upload);
            _names[slot] = UniqueName(string.IsNullOrWhiteSpace(name) ? $"Player {slot + 1}" : name.Trim());
            peer.Send(new Welcome(slot, Config));
            Broadcast(new Roster([.. _names]));
            return slot;
        }
    }

    /// <summary>
    /// The name as it will stand in the roster: with a number after it when someone here already has it. A
    /// player comes back to a match by name, so two players must not share one. The numbered name is what the
    /// second player would have to rejoin under; their lobby shows it.
    /// </summary>
    private string UniqueName(string name)
    {
        string candidate = name;
        for (int n = 2; (_names.Contains(candidate, StringComparer.OrdinalIgnoreCase) || _startNames.Contains(candidate, StringComparer.OrdinalIgnoreCase)); n++)
        {
            string suffix = string.Create(System.Globalization.CultureInfo.InvariantCulture, $" ({n})");
            candidate = name[..Math.Min(name.Length, MessageCodec.MaxTextLength - suffix.Length)] + suffix;
        }
        return candidate;
    }

    /// <summary>
    /// A player who dropped out of the running match takes their slot back: they get the match settings, every
    /// tick so far, and from then on the live ticks, and their inputs count again.
    /// </summary>
    private int Rejoin(IPeer peer, string name, out string refusal)
    {
        refusal = string.Empty;
        int slot = -1;
        for (int i = 0; i < _peers.Length && slot < 0; i++)
            if (_peers[i] is null && !_lockstep.IsBotSeat(i) &&
                string.Equals(_startNames[i], name, StringComparison.OrdinalIgnoreCase))
                slot = i;
        if (slot < 0)
        {
            refusal = "The match in this room has already started. A player who dropped out can come back under the name they had.";
            return -1;
        }

        _peers[slot] = peer;
        _emptyFor = TimeSpan.Zero;
        _names[slot] = _startNames[slot];
        peer.Send(new Welcome(slot, Config));
        peer.Send(new Rejoined(_humanMask));
        foreach (var chunk in Log.Chunk(_lockstep.Log, Config.Slots))
            peer.Send(chunk);
        if (IsPaused)
            peer.Send(new PauseState(true, _pausedBySlot));
        if (_hold != null)
            peer.Send(new JoinHoldState(_watchName));
        _lockstep.Reconnect(slot);
        Broadcast(new Roster([.. _names]));
        return slot;
    }

    /// <summary>The slot's client is gone. In a running match its tank idles from the next tick.</summary>
    public void Leave(int slot)
    {
        lock (_lock)
        {
            bool wasHost = slot == HostSlot;
            _peers[slot] = null;
            _names[slot] = string.Empty;
            _readyHashes[slot] = null;
            _canUpload[slot] = false;
            _lastHashTick[slot] = 0;
            ProviderLeft(slot);
            if (IsRunning)
                _lockstep.Disconnect(slot);
            else if (wasHost)
                _startRequested = false; // The next host decides for themself.
            Broadcast(new Roster([.. _names]));
            if (!IsRunning)
                StartIfEveryoneIsReady();
        }
    }

    public void Handle(int slot, Message message)
    {
        lock (_lock)
        {
            switch (message)
            {
                case Ready ready when !IsRunning:
                    _readyHashes[slot] = ready.WorldHash;
                    StartIfEveryoneIsReady();
                    break;
                case StartRequest when !IsRunning && slot == HostSlot:
                    _startRequested = true;
                    StartIfEveryoneIsReady();
                    break;
                case SnapshotChunk chunk:
                    ReceiveChunk(slot, chunk);
                    break;
                case SnapshotDone done:
                    FinishReport(slot, done);
                    break;
                case JoinFailed when _hold is { } hold && (hold.Uploader == slot || hold.Verifier == slot):
                    RefuseWatcher("A seated player could not copy the match.");
                    break;
                case Input input when IsRunning:
                    _lockstep.Submit(slot, input.Encoded);
                    break;
                case SetPaused pause when IsRunning && pause.Paused != IsPaused:
                    IsPaused = pause.Paused;
                    _pausedBySlot = slot;
                    Broadcast(new PauseState(IsPaused, slot));
                    break;
                // Every client simulates the same match, so the first to say so is believed. The tick must be one
                // the relay has closed: nobody can have simulated a later one.
                case MatchOver over when IsRunning && _overAtTick is null &&
                                         over.AtTick > 0 && over.AtTick <= _lockstep.NextTick:
                    _overAtTick = over.AtTick;
                    RefuseWatcher("The match is over.");
                    break;
                case Ping ping:
                    _peers[slot]?.Send(new Pong(ping.Id));
                    break;
                case Hash hash when IsRunning:
                    _lockstep.SubmitHash(hash.AtTick, hash.Value);
                    if (hash.AtTick <= _lockstep.NextTick)
                        _lastHashTick[slot] = Math.Max(_lastHashTick[slot], hash.AtTick);
                    ReportDesync();
                    break;
            }
        }
    }

    /// <summary>
    /// Lets <paramref name="elapsed"/> wall time go by and closes the ticks that are now due. The match clock runs
    /// only while the match is on, not paused, and someone is connected: a room nobody is in waits, so bots do not
    /// play on alone and a returning player does not face a stretch of catch-up, and a pause is not counted. A
    /// finished match stops <see cref="SecondsPlayedAfterTheEnd"/> seconds after its end. The
    /// number of ticks due follows the total match time, so a late call is made up at once and the rate does not
    /// drift.
    /// </summary>
    public void AdvanceClock(TimeSpan elapsed)
    {
        lock (_lock)
        {
            if (!IsRunning)
                return;
            if (AdvanceJoinClock(elapsed))
                return;
            if (_peers.All(p => p is null))
            {
                _emptyFor += elapsed;
                return;
            }
            _emptyFor = TimeSpan.Zero;
            if (IsPaused)
                return;
            if (_overAtTick is int overAt &&
                _lockstep.NextTick >= overAt + SecondsPlayedAfterTheEnd * Config.TicksPerSecond)
                return;
            _matchSeconds += elapsed.TotalSeconds;
            CloseTicksUpTo((int)(_matchSeconds * Config.TicksPerSecond));
        }
    }

    /// <summary>Closes ticks until <paramref name="ticksDue"/> have been closed in all, sending each to every client.</summary>
    public void CloseTicksUpTo(int ticksDue)
    {
        lock (_lock)
        {
            while (IsRunning && !IsPaused && _hold == null && _lockstep.NextTick < ticksDue)
                Broadcast(new Tick(_lockstep.CloseTick()));
        }
    }

    /// <summary>The host is whoever sits in the lowest occupied slot; -1 in an empty room.</summary>
    private int HostSlot => RoomRules.HostSlot(_peers.Length, slot => _peers[slot] is not null);

    /// <summary>
    /// Starts once everyone here is ready, if the room is full or the host asked. Slots nobody took are left to
    /// bots, which every client runs for itself.
    /// </summary>
    private void StartIfEveryoneIsReady()
    {
        int present = _peers.Count(p => p is not null);
        if (present == 0 || (present < _peers.Length && !_startRequested))
            return;
        var hashes = new List<ulong>(present);
        for (int slot = 0; slot < _peers.Length; slot++)
        {
            if (_peers[slot] is null)
                continue;
            if (_readyHashes[slot] is not ulong hash)
                return;
            hashes.Add(hash);
        }
        if (hashes.Distinct().Count() > 1)
        {
            // Same seed and build, different worlds: playing on would only desync a second later.
            // The refusal is final for the clients, and the connections close after it: the room empties and goes.
            Broadcast(new Refusal("The players' worlds differ before the first tick. Check that everyone runs the same build."));
            foreach (IPeer? peer in _peers)
                peer?.Close();
            Array.Clear(_readyHashes);
            _startRequested = false;
            return;
        }

        int humanMask = 0;
        for (int slot = 0; slot < _peers.Length; slot++)
        {
            if (_peers[slot] is not null)
                humanMask |= 1 << slot;
            else
                _lockstep.SeatBot(slot);
        }
        IsRunning = true;
        _humanMask = humanMask;
        _startNames = [.. _names];
        Broadcast(new Start(humanMask));
    }

    private void Broadcast(Message message)
    {
        foreach (IPeer? peer in _peers)
            peer?.Send(message);
    }
}
