namespace Tunnerer.Relay;

using System.Collections.Immutable;
using Tunnerer.Protocol;

/// <summary>Where a room sends a client's messages. Sending never blocks the room.</summary>
public interface IPeer
{
    void Send(Message message);
}

/// <summary>
/// One match on the relay: who is in which slot, the lobby until everyone is ready, then the ticks. It owns no
/// sockets and no clock: the hub passes it messages and tells it how many ticks should have been closed by now.
/// Every member takes the room's lock, so it can be called from any connection's thread.
/// </summary>
public sealed class MatchRoom
{
    private readonly Lock _lock = new();
    private readonly IPeer?[] _peers;
    private readonly string[] _names;
    private readonly ulong?[] _readyHashes;
    private readonly LockstepRoom _lockstep;
    private bool _desyncSent;
    private bool _startRequested;
    private int _humanMask;
    // Who played each slot when the match started: a player who drops out comes back under the same name.
    private string[] _startNames = [];

    public MatchRoom(string code, string buildId, MatchConfig config)
    {
        Code = code;
        BuildId = buildId;
        Config = config;
        _peers = new IPeer?[config.Slots];
        _names = new string[config.Slots];
        Array.Fill(_names, string.Empty);
        _readyHashes = new ulong?[config.Slots];
        _lockstep = new LockstepRoom(config.Slots, config.InputKeyMask);
    }

    public string Code { get; }
    public string BuildId { get; }
    public MatchConfig Config { get; }

    /// <summary>True from the moment every slot was ready with the same world; ticks are being closed.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>True while a running match is held: no ticks are closed until someone resumes it.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>True once nobody is connected: the hub then forgets the room.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_lock)
                return _peers.All(p => p is null);
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
    /// Puts a client in the first free slot and tells it the match settings. Returns the slot, or -1 with the
    /// <paramref name="refusal"/> to send when it cannot join.
    /// </summary>
    public int Join(IPeer peer, string name, string buildId, out string refusal)
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
                return Rejoin(peer, name, out refusal);
            int slot = Array.IndexOf(_peers, null);
            if (slot < 0)
            {
                refusal = "This room is full.";
                return -1;
            }
            _peers[slot] = peer;
            _names[slot] = string.IsNullOrWhiteSpace(name) ? $"Player {slot + 1}" : name;
            peer.Send(new Welcome(slot, Config));
            Broadcast(new Roster([.. _names]));
            return slot;
        }
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
            if (_peers[i] is null && (_humanMask & 1 << i) != 0 &&
                string.Equals(_startNames[i], name, StringComparison.OrdinalIgnoreCase))
                slot = i;
        if (slot < 0)
        {
            refusal = "The match in this room has already started. A player who dropped out can come back under the name they had.";
            return -1;
        }

        _peers[slot] = peer;
        _names[slot] = _startNames[slot];
        peer.Send(new Welcome(slot, Config));
        peer.Send(new Rejoined(_humanMask));
        var log = _lockstep.Log;
        for (int first = 0; first < log.Count; first += MessageCodec.MaxLogTicks)
        {
            int count = Math.Min(MessageCodec.MaxLogTicks, log.Count - first);
            var inputs = ImmutableArray.CreateBuilder<uint>(count * Config.Slots);
            for (int i = 0; i < count; i++)
                inputs.AddRange(log[first + i].Inputs);
            peer.Send(new Log(first, Config.Slots, inputs.MoveToImmutable()));
        }
        if (IsPaused)
            peer.Send(new PauseState(true, slot));
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
                case Input input when IsRunning:
                    _lockstep.Submit(slot, input.Encoded);
                    break;
                case SetPaused pause when IsRunning && pause.Paused != IsPaused:
                    IsPaused = pause.Paused;
                    Broadcast(new PauseState(IsPaused, slot));
                    break;
                case Ping ping:
                    _peers[slot]?.Send(new Pong(ping.Id));
                    break;
                case Hash hash when IsRunning:
                    _lockstep.SubmitHash(hash.AtTick, hash.Value);
                    if (_lockstep.DesyncTick is int tick && !_desyncSent)
                    {
                        _desyncSent = true;
                        Broadcast(new Desync(tick));
                    }
                    break;
            }
        }
    }

    /// <summary>Closes ticks until <paramref name="ticksDue"/> have been closed in all, sending each to every client.</summary>
    public void CloseTicksUpTo(int ticksDue)
    {
        lock (_lock)
        {
            while (IsRunning && !IsPaused && _lockstep.NextTick < ticksDue)
                Broadcast(new Tick(_lockstep.CloseTick()));
        }
    }

    /// <summary>The host is whoever sits in the lowest occupied slot; -1 in an empty room.</summary>
    private int HostSlot => Array.FindIndex(_peers, p => p is not null);

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
            Broadcast(new Refusal("The players' worlds differ before the first tick. Check that everyone runs the same build."));
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
                _lockstep.Disconnect(slot);
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
