namespace Tunnerer.Tests;

using System.Diagnostics.CodeAnalysis;
using Tunnerer.Protocol;

/// <summary>
/// A relay and its connections with no sockets: a <see cref="LockstepRoom"/> and one <see cref="Link"/> per slot.
/// Time is counted in tick periods. Each message takes a delay drawn per message (in order on each connection, as on
/// TCP), so tests can put clients on links of different quality.
/// </summary>
internal sealed class InMemoryRelay
{
    private readonly Random _random;
    private readonly Link[] _links;
    private int _now;

    public InMemoryRelay(int slots, int seed, int maxDelay)
    {
        Room = new LockstepRoom(slots);
        _random = new Random(seed);
        _links = new Link[slots];
        for (int i = 0; i < slots; i++)
            _links[i] = new Link(this, i, maxDelay);
    }

    public LockstepRoom Room { get; }

    public Link LinkFor(int slot) => _links[slot];

    /// <summary>One tick period: takes in what has arrived from the clients and, if asked, closes a tick.</summary>
    public void Advance(bool closeTick = true)
    {
        _now++;
        foreach (var link in _links)
            link.DeliverUp(Room);
        if (!closeTick)
            return;
        TickBundle bundle = Room.CloseTick();
        foreach (var link in _links)
            if (Room.IsConnected(link.Slot))
                link.QueueDown(bundle);
    }

    public void Disconnect(int slot)
    {
        Room.Disconnect(slot);
        _links[slot].Close();
    }

    internal sealed class Link(InMemoryRelay relay, int slot, int maxDelay) : ILockstepLink
    {
        private readonly Queue<(int At, uint Input, int HashTick, ulong Hash, bool IsHash)> _up = new();
        private readonly Queue<(int At, TickBundle Bundle)> _down = new();
        private int _lastUpAt;
        private int _lastDownAt;
        private bool _closed;

        public int Slot => slot;

        public void SendInput(uint input) => _up.Enqueue((NextArrival(ref _lastUpAt), input, 0, 0, false));

        public void SendHash(int tick, ulong hash) => _up.Enqueue((NextArrival(ref _lastUpAt), 0, tick, hash, true));

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

        /// <summary>True once everything queued in either direction has been delivered.</summary>
        public bool IsIdle => _closed || (_up.Count == 0 && _down.Count == 0);

        public void QueueDown(TickBundle bundle) => _down.Enqueue((NextArrival(ref _lastDownAt), bundle));

        public void DeliverUp(LockstepRoom room)
        {
            while (!_closed && _up.Count > 0 && _up.Peek().At <= relay._now)
            {
                var message = _up.Dequeue();
                if (message.IsHash)
                    room.SubmitHash(message.HashTick, message.Hash);
                else
                    room.Submit(slot, message.Input);
            }
        }

        public void Close()
        {
            _closed = true;
            _up.Clear();
            _down.Clear();
        }

        // A later message never arrives before an earlier one on the same connection.
        private int NextArrival(ref int last) =>
            last = Math.Max(last, relay._now + relay._random.Next(maxDelay + 1));
    }
}
