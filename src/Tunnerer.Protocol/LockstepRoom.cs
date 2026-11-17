namespace Tunnerer.Protocol;

using System.Collections.Immutable;

/// <summary>
/// The relay's side of one match: it queues each slot's inputs and, when told to close a tick, takes one from every
/// slot and makes the bundle all clients simulate. It holds no clock and no sockets; the caller closes ticks at the
/// simulation rate and carries the messages.
/// </summary>
/// <remarks>
/// A slot with nothing queued repeats its last input, so a slow client never stalls the others. Inputs are queued,
/// not replaced, because the game reads key presses as changes between consecutive inputs: dropping one in the
/// middle could swallow a press. A disconnected slot plays the neutral input.
/// <para>
/// A queue more than one deep is a backlog: every input in it is played late, and since a client sends one input
/// per tick it would never shrink. So while a slot has a backlog, an input whose keys are the same as the next
/// one's is skipped. That loses no key press or release, only an aim on the way to the next aim.
/// </para>
/// </remarks>
public sealed class LockstepRoom
{
    /// <summary>The input of a tank nobody is driving: no movement, no keys, no aim.</summary>
    public const uint NeutralInput = 0;

    /// <summary>
    /// Inputs one slot may have waiting. A client that sends more at once loses an input that repeats its
    /// successor's keys, or, if there is none, the oldest.
    /// </summary>
    public const int MaxQueuedInputs = 4;

    private readonly List<uint>[] _queues;
    private readonly uint _keyMask;
    private readonly uint[] _last;
    private readonly bool[] _connected;
    private readonly int[] _dropped;
    private readonly List<TickBundle> _log = [];
    private readonly Dictionary<int, ulong> _hashes = [];

    /// <param name="keyMask">
    /// The bits of an input that are keys. With the default, every bit, only an input identical to the next is
    /// ever skipped.
    /// </param>
    public LockstepRoom(int slots, uint keyMask = uint.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);
        _keyMask = keyMask;
        _queues = new List<uint>[slots];
        for (int i = 0; i < slots; i++)
            _queues[i] = new List<uint>(MaxQueuedInputs);
        _last = new uint[slots];
        _connected = new bool[slots];
        _dropped = new int[slots];
        Array.Fill(_connected, true);
    }

    public int Slots => _queues.Length;

    /// <summary>The tick the next <see cref="CloseTick"/> will make: also how many ticks have been closed.</summary>
    public int NextTick => _log.Count;

    /// <summary>Every bundle so far, in order. With the match settings it rebuilds the match: a rejoin or a replay.</summary>
    public IReadOnlyList<TickBundle> Log => _log;

    /// <summary>The first tick at which two clients reported different hashes, or null while they agree.</summary>
    public int? DesyncTick { get; private set; }

    public bool IsConnected(int slot) => _connected[slot];

    /// <summary>How many of the slot's inputs were lost because its queue was full and none could be skipped safely.</summary>
    public int DroppedInputs(int slot) => _dropped[slot];

    /// <summary>How many of the slot's inputs are waiting: more than one is a backlog, played late.</summary>
    public int QueuedInputs(int slot) => _queues[slot].Count;

    /// <summary>Queues an input from a client. Ignored for a disconnected slot.</summary>
    public void Submit(int slot, uint input)
    {
        if (!_connected[slot])
            return;
        var queue = _queues[slot];
        queue.Add(input);
        if (queue.Count <= MaxQueuedInputs)
            return;
        // Losing an input whose keys its successor repeats loses no press or release; only when there is none does
        // the oldest go, and that can.
        int redundant = 0;
        while (redundant < queue.Count && !IsRedundant(queue, redundant))
            redundant++;
        if (redundant == queue.Count)
        {
            redundant = 0;
            _dropped[slot]++;
        }
        queue.RemoveAt(redundant);
    }

    /// <summary>
    /// True when the queued input at <paramref name="index"/> has the same keys as the one after it, so playing it
    /// would change nothing but the aim: skipping it loses no key press or release.
    /// </summary>
    private bool IsRedundant(List<uint> queue, int index) =>
        index + 1 < queue.Count && (queue[index] & _keyMask) == (queue[index + 1] & _keyMask);

    /// <summary>The slot's client is gone: from the next tick its tank plays the neutral input.</summary>
    public void Disconnect(int slot)
    {
        _connected[slot] = false;
        _queues[slot].Clear();
        _last[slot] = NeutralInput;
    }

    /// <summary>The slot's client is back and its inputs count again.</summary>
    public void Reconnect(int slot) => _connected[slot] = true;

    /// <summary>Makes the bundle for the next tick from one queued input per slot, or the slot's last input.</summary>
    public TickBundle CloseTick()
    {
        var inputs = ImmutableArray.CreateBuilder<uint>(Slots);
        for (int slot = 0; slot < Slots; slot++)
        {
            var queue = _queues[slot];
            while (IsRedundant(queue, 0))
                queue.RemoveAt(0);
            if (queue.Count > 0)
            {
                _last[slot] = queue[0];
                queue.RemoveAt(0);
            }
            inputs.Add(_last[slot]);
        }
        var bundle = new TickBundle(_log.Count, inputs.MoveToImmutable());
        _log.Add(bundle);
        return bundle;
    }

    /// <summary>
    /// Records a client's hash of its world after <paramref name="tick"/> ticks and compares it with what another
    /// client reported for the same tick. A difference sets <see cref="DesyncTick"/>. The first report for a tick is
    /// kept for the whole match (clients report about once a second, so it stays small), so a client far behind,
    /// catching up, is still checked. A tick that has not been closed yet cannot have been simulated by anyone and
    /// is ignored.
    /// </summary>
    public void SubmitHash(int tick, ulong hash)
    {
        if (tick < 0 || tick > NextTick)
            return;
        if (_hashes.TryGetValue(tick, out ulong known))
        {
            if (known != hash && (DesyncTick is not int first || tick < first))
                DesyncTick = tick;
            return;
        }
        _hashes[tick] = hash;
    }
}
