namespace Emberward.Tests;

using System.Diagnostics.CodeAnalysis;
using Emberward.Protocol;

/// <summary>
/// A relay and its connections with no sockets: a <see cref="LockstepRoom"/> and one <see cref="Link"/> per client.
/// Time is counted in tick periods. Each message takes a delay drawn per message (in order on each connection, as on
/// TCP), so tests can put clients on links of different quality. Each connection draws its delays from its own
/// generator, so clients may be stepped on different threads between two <see cref="Advance"/> calls.
/// </summary>
/// <remarks>
/// It also plays the relay's part in a join (docs/late-join-design.md §4.1, §5): a client added with
/// <see cref="AddJoiner"/> watches until it is seated; its <see cref="ReadyToLoad"/> holds the match at the next tick,
/// one player uploads and a second, if present, verifies the hashes (<see cref="SnapshotHold"/>). The joiner
/// receives the agreed snapshot, and its <see cref="TakeSeat"/> hands it a bot's slot from that tick. The hold ends when every
/// joiner in it is seated or has failed. The time limit, uploaders that leave and the refusals are the real relay's.
/// </remarks>
internal sealed class InMemoryRelay
{
    private readonly int _seed;
    private readonly int _maxDelay;
    private readonly Link[] _initial;
    private readonly Link?[] _seated;
    private readonly List<Link> _links = [];
    private readonly List<Link> _joiners = [];
    private int _now;

    public InMemoryRelay(int slots, int seed, int maxDelay)
    {
        Room = new LockstepRoom(slots);
        _seed = seed;
        _maxDelay = maxDelay;
        _initial = new Link[slots];
        _seated = new Link?[slots];
        for (int i = 0; i < slots; i++)
            _links.Add(_seated[i] = _initial[i] = NewLink(i));
    }

    public LockstepRoom Room { get; }

    /// <summary>The hold a join is in, or null while the match runs.</summary>
    public SnapshotHold? Hold { get; private set; }

    /// <summary>How many snapshot offers were sent to joiners, in all.</summary>
    public int OffersSent { get; private set; }

    /// <summary>The connection of the client that started in the slot.</summary>
    public Link LinkFor(int slot) => _initial[slot];

    /// <summary>A client that comes into the running match: in the room, driving nothing until it is seated.</summary>
    public Link AddJoiner()
    {
        var link = NewLink(-1);
        _links.Add(link);
        return link;
    }

    /// <summary>
    /// One tick period: takes in what has arrived from the clients and, if asked and no join holds the match, closes
    /// a tick.
    /// </summary>
    public void Advance(bool closeTick = true)
    {
        _now++;
        foreach (var link in _links.ToArray())
            link.DeliverUp();
        if (Hold != null && _joiners.Count == 0)
            Hold = null;
        if (!closeTick || Hold != null)
            return;
        TickBundle bundle = Room.CloseTick();
        for (int slot = 0; slot < _seated.Length; slot++)
            if (_seated[slot] is { } link && Room.IsConnected(slot))
                link.QueueDown(new Tick(bundle));
    }

    /// <summary>The slot's client is gone: its tank idles, and the seat stays its player's.</summary>
    public void Disconnect(int slot)
    {
        Room.Disconnect(slot);
        _seated[slot]?.Close();
        _seated[slot] = null;
    }

    /// <summary>Nobody took the slot: a bot drives it, as when the relay starts a match with the slot empty.</summary>
    public void SeatBot(int slot)
    {
        Room.SeatBot(slot);
        _seated[slot]?.Close();
        _seated[slot] = null;
    }

    private Link NewLink(int slot) => new(this, slot, _maxDelay, new Random(_seed * 7919 + _links.Count));

    private void Handle(Link from, Message message)
    {
        switch (message)
        {
            case Input input when from.Slot >= 0:
                Room.Submit(from.Slot, input.Encoded);
                break;
            case Hash hash when from.Slot >= 0:
                Room.SubmitHash(hash.AtTick, hash.Value);
                break;
            case ReadyToLoad when from.Slot < 0 && !_joiners.Contains(from):
                JoinHold(from);
                break;
            case SnapshotChunk chunk when from.Slot >= 0 && Hold != null:
                Hold.AddChunk(from.Slot, chunk);
                break;
            case SnapshotDone done when from.Slot >= 0 && Hold != null && Hold.Finish(from.Slot, done):
                // The uploader's sync hash goes into the comparison like any other; two digests that differ are a
                // desync at the hold's tick, and the joiners are given neither.
                Room.SubmitHash(done.Tick, done.SyncHash);
                Room.SubmitSnapshotDigest(done.Tick, done.Digest);
                if (Hold.Offer != null && Room.DesyncTick == null)
                    foreach (var joiner in _joiners)
                        Offer(joiner);
                break;
            case TakeSeat take when from.Slot < 0 && Hold?.Offer != null && Room.DesyncTick == null && _joiners.Contains(from):
                if (take.Slot >= 0 && take.Slot < _seated.Length && Room.Seat(take.Slot))
                {
                    _joiners.Remove(from);
                    from.Slot = take.Slot;
                    _seated[take.Slot] = from;
                    from.QueueDown(new Seated(take.Slot, Hold.Tick));
                }
                else
                    from.QueueDown(new SeatRefused("Another player took that seat."));
                break;
            case JoinFailed:
                _joiners.Remove(from);
                break;
        }
    }

    /// <summary>
    /// A joiner is ready to load: holds the match after the ticks closed so far and asks up to two seated players for
    /// a snapshot there, or puts the joiner in the hold already on.
    /// </summary>
    private void JoinHold(Link joiner)
    {
        _joiners.Add(joiner);
        if (Hold == null)
        {
            var players = Enumerable.Range(0, _seated.Length)
                .Where(slot => _seated[slot] != null && Room.IsConnected(slot) && !Room.IsBotSeat(slot))
                .Take(2).ToArray();
            Hold = new SnapshotHold(Room.NextTick, players[0], players.Length == 2 ? players[1] : null, MessageCodec.MaxSnapshotBytes);
            foreach (int slot in players)
                _seated[slot]!.QueueDown(new SnapshotRequest(Hold.Tick, SendChunks: slot == players[0]));
        }
        else if (Hold.Offer != null && Room.DesyncTick == null)
            Offer(joiner);
    }

    private void Offer(Link joiner)
    {
        OffersSent++;
        joiner.QueueDown(Hold!.Offer!);
        foreach (var chunk in Hold.Chunks)
            joiner.QueueDown(chunk);
    }

    internal sealed class Link(InMemoryRelay relay, int slot, int maxDelay, Random random) : ILockstepLink
    {
        private readonly Queue<(int At, Message Message)> _up = new();
        private readonly Queue<(int At, TickBundle Bundle)> _down = new();
        private readonly Queue<(int At, Message Message)> _downJoin = new();
        private int _lastUpAt;
        private int _lastDownAt;
        private bool _closed;

        /// <summary>The slot this client drives; -1 while it watches, before it is seated.</summary>
        public int Slot { get; set; } = slot;

        public void SendInput(uint input) => SendUp(new Input(input));

        public void SendHash(int tick, ulong hash) => SendUp(new Hash(tick, hash));

        public void SendJoin(Message message) => SendUp(message);

        /// <summary>The tick at which this client reported the match decided, or null.</summary>
        public int? MatchOverTick { get; private set; }

        public void SendMatchOver(int tick) => MatchOverTick = tick;

        public bool TryReceive([NotNullWhen(true)] out TickBundle? bundle)
        {
            bundle = null;
            if (_closed || _down.Count == 0 || _down.Peek().At > relay._now)
                return false;
            bundle = _down.Dequeue().Bundle;
            return true;
        }

        public bool TryReceiveJoin([NotNullWhen(true)] out Message? message)
        {
            message = null;
            if (_closed || _downJoin.Count == 0 || _downJoin.Peek().At > relay._now)
                return false;
            message = _downJoin.Dequeue().Message;
            return true;
        }

        public void QueueDown(Message message)
        {
            message = MessageCodec.Decode(MessageCodec.Encode(message));
            int at = NextArrival(ref _lastDownAt);
            if (message is Tick tick)
                _down.Enqueue((at, tick.Bundle));
            else
                _downJoin.Enqueue((at, message));
        }

        public void DeliverUp()
        {
            while (!_closed && _up.Count > 0 && _up.Peek().At <= relay._now)
                relay.Handle(this, MessageCodec.Decode(MessageCodec.Encode(_up.Dequeue().Message)));
        }

        public void Close()
        {
            _closed = true;
            _up.Clear();
            _down.Clear();
            _downJoin.Clear();
        }

        private void SendUp(Message message) => _up.Enqueue((NextArrival(ref _lastUpAt), message));

        // A later message never arrives before an earlier one on the same connection.
        private int NextArrival(ref int last) =>
            last = Math.Max(last, relay._now + random.Next(maxDelay + 1));
    }
}
