namespace Tunnerer.Desktop.Net;

using Tunnerer.Core;
using Tunnerer.Core.Bots;
using Tunnerer.Core.Config;
using Tunnerer.Core.Input;
using Tunnerer.Core.Snapshots;
using Tunnerer.Protocol;

/// <summary>
/// One client's part in a network match. It sends the local player's input to the relay, collects the bundles the
/// relay sends back, steps the world only from those bundles, and reports a hash of the world now and then so the
/// relay can tell when clients have drifted apart. When a player joins the match in progress, the relay may ask it
/// for a snapshot of the world (docs/late-join-design.md §9.2).
/// </summary>
/// <remarks>
/// The local tank is driven by the bundle like every other, never by the keyboard directly: what the relay put in
/// the bundle is what every client simulates.
/// </remarks>
public sealed class NetSession
{
    /// <summary>A hash is reported every this many ticks: once a second.</summary>
    public const int HashIntervalTicks = Tweaks.Perf.TargetFps;

    private readonly ILockstepLink _link;
    private readonly World _world;
    private readonly int _startTick;
    private readonly byte[]? _startSnapshot;
    private Seats _seats;
    private int _startMask;
    private readonly BundleBuffer _buffer;
    private readonly List<TickBundle> _played = [];
    private readonly Queue<(int Tick, SyncDigest Digest)> _digests = new();
    private SnapshotRequest? _snapshotRequested;
    private bool _matchOverSent;

    /// <param name="world">A world built from the match settings and not yet stepped.</param>
    public NetSession(ILockstepLink link, World world)
        : this(link, world, Seats.AllPlayers(world.TankList.Count), startSnapshot: null)
    {
        if (world.AdvanceCount != 0)
            throw new ArgumentException("A network session starts from a world that has not been stepped.", nameof(world));
    }

    private NetSession(ILockstepLink link, World world, Seats seats, byte[]? startSnapshot)
    {
        _link = link;
        _world = world;
        _seats = seats;
        _startMask = seats.HumanMask;
        _startTick = world.AdvanceCount;
        _startSnapshot = startSnapshot;
        _buffer = new BundleBuffer(firstTick: _startTick);
    }

    /// <summary>
    /// A session for a player who joined the match in progress: its world was loaded from a snapshot, and it steps on
    /// from the tick the snapshot was saved at.
    /// </summary>
    /// <param name="snapshot">The compressed snapshot the world was loaded from, kept for the recording (<see cref="StartSnapshot"/>).</param>
    public static NetSession FromSnapshot(ILockstepLink link, World world, Seats seats, byte[] snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new NetSession(link, world, seats, snapshot);
    }

    /// <summary>
    /// The match has started: bots take the slots no player took, made from the match seed so every client's bots
    /// are the same. Call it before the first step.
    /// </summary>
    public void StartSeats(int matchSeed, int humanMask)
    {
        if (_played.Count != 0 || _world.AdvanceCount != _startTick)
            throw new InvalidOperationException("Bots are chosen before the first tick.");
        _seats = Seats.ForStart(_world, matchSeed, humanMask);
        _startMask = _seats.HumanMask;
    }

    /// <summary>The bots driving the slots no player drives, indexed by slot (null where a player drives).</summary>
    public IReadOnlyList<BotTankAI?> Bots => _seats.Bots;

    /// <summary>Bit n is set while a player drives slot n, as of the last tick simulated.</summary>
    public int HumanMask => _seats.HumanMask;

    /// <summary>The tick the session started from: 0, or the tick of the snapshot a joiner loaded.</summary>
    public int StartTick => _startTick;

    /// <summary>Every bundle simulated so far, in order, from <see cref="StartTick"/>.</summary>
    public IReadOnlyList<TickBundle> Played => _played;

    /// <summary>The human mask the session started with: at the start of the match, or in the snapshot loaded.</summary>
    public int StartMask => _startMask;

    /// <summary>The compressed snapshot a joiner's world was loaded from; null for a session from the start.</summary>
    public byte[]? StartSnapshot => _startSnapshot;

    /// <summary>The last few digests reported to the relay, oldest first, for a desync report.</summary>
    public IReadOnlyCollection<(int Tick, SyncDigest Digest)> RecentDigests => _digests;

    /// <summary>How many reported digests are kept.</summary>
    public const int DigestsKept = 12;

    /// <summary>Bundles received and not yet simulated.</summary>
    public int Buffered => _buffer.Count;

    /// <summary>Sends the local player's input for one step.</summary>
    public void SendInput(in ControllerOutput input) => _link.SendInput(ControllerOutputCodec.Encode(input));

    /// <summary>
    /// Moves every bundle that has arrived into the buffer, and answers the relay's request for a snapshot once the
    /// world has reached the tick asked for. Called once a frame, so a match nobody joins pays nothing per tick.
    /// </summary>
    public void Receive()
    {
        while (_link.TryReceive(out TickBundle? bundle))
            _buffer.Add(bundle);
        while (_link.TryReceiveJoin(out Message? message))
            if (message is SnapshotRequest request)
                _snapshotRequested = request;
        if (_snapshotRequested is { } pending && _world.AdvanceCount >= pending.Tick)
        {
            _snapshotRequested = null;
            // The match is held at the tick asked for, so the world cannot have passed it unless the relay is wrong.
            if (_world.AdvanceCount == pending.Tick)
                AnswerSnapshotRequest(pending);
        }
    }

    private void AnswerSnapshotRequest(SnapshotRequest request)
    {
        byte[] snapshot = WorldSnapshot.Save(_world, _seats.Bots);
        if (snapshot.Length > WorldSnapshot.MaxBytes)
        {
            _link.SendJoin(new JoinFailed("The match is too large to copy."));
            return;
        }
        int length = 0;
        if (request.SendChunks)
        {
            byte[] compressed = WorldSnapshot.Compress(snapshot);
            if (compressed.Length > MessageCodec.MaxSnapshotBytes)
            {
                _link.SendJoin(new JoinFailed("The compressed match exceeds the relay limit."));
                return;
            }
            length = compressed.Length;
            foreach (SnapshotChunk chunk in SnapshotChunk.Split(request.Tick, compressed))
                _link.SendJoin(chunk);
        }
        _link.SendJoin(new SnapshotDone(request.Tick, length, WorldSnapshot.Digest(snapshot),
            WorldStateHash.ComputeSync(_world, _seats.Bots).Combined));
    }

    /// <summary>How many steps to simulate now; see <see cref="BundleBuffer.StepsToTake"/>.</summary>
    public int StepsToTake(int dueByClock, int maxSteps) => _buffer.StepsToTake(dueByClock, maxSteps);

    /// <summary>Simulates the next buffered tick. False when none is buffered.</summary>
    public bool TryStep()
    {
        if (!_buffer.TryTake(out TickBundle? bundle))
            return false;
        if (bundle.Tick != _world.AdvanceCount)
            throw new InvalidOperationException(
                $"The world is at tick {_world.AdvanceCount} but the next bundle is for tick {bundle.Tick}.");

        _seats.Step(_world, bundle);
        _played.Add(bundle);
        if (_world.AdvanceCount % HashIntervalTicks == 0)
        {
            SyncDigest digest = WorldStateHash.ComputeSync(_world, _seats.Bots);
            _digests.Enqueue((_world.AdvanceCount, digest));
            if (_digests.Count > DigestsKept)
                _digests.Dequeue();
            _link.SendHash(_world.AdvanceCount, digest.Combined);
        }
        // The relay does not simulate, so it has to be told: a finished match gives its room up for the next one.
        if (_world.IsMatchOver && !_matchOverSent)
        {
            _matchOverSent = true;
            _link.SendMatchOver(_world.AdvanceCount);
        }
        return true;
    }
}
