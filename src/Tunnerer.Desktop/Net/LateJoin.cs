namespace Tunnerer.Desktop.Net;

using Tunnerer.Core;
using Tunnerer.Core.Match;
using Tunnerer.Core.Snapshots;
using Tunnerer.Protocol;

/// <summary>
/// A player joining a match in progress, from a world built from the settings to a seat
/// (docs/late-join-design.md §9.4): it asks the relay to hold the match, loads the snapshot the players already in it
/// upload, checks it, takes over a bot's tank and then plays like everyone else through <see cref="Session"/>.
/// </summary>
/// <remarks>
/// A snapshot that fails any check fails the join, and the match goes on without this player: the world it was
/// loaded into is no longer the one the settings build.
/// </remarks>
public sealed class LateJoin
{
    private readonly ILockstepLink _link;
    private readonly World _world;
    private readonly int _matchSeed;
    private readonly HashSet<int> _refused = [];
    private SnapshotOffer? _offer;
    private SnapshotAssembly? _received;
    private Seats? _seats;
    private byte[]? _snapshot;
    private int _askedSlot = -1;

    /// <param name="world">A world built from the match settings and not yet stepped: the snapshot is loaded into it.</param>
    public LateJoin(ILockstepLink link, World world, int matchSeed)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(world);
        if (world.AdvanceCount != 0)
            throw new ArgumentException("A snapshot is loaded into a world that has not been stepped.", nameof(world));
        _link = link;
        _world = world;
        _matchSeed = matchSeed;
    }

    public enum Stage
    {
        /// <summary>The world is built and the relay asked to hold the match; the snapshot has not all arrived.</summary>
        CopyingTheMatch,

        /// <summary>The snapshot is loaded and checked, and the relay asked for a seat.</summary>
        TakingASeat,

        /// <summary>In the match: <see cref="Session"/> steps it, in <see cref="Slot"/>.</summary>
        Seated,

        /// <summary>The join failed (<see cref="Failure"/> says why); the match goes on without this player.</summary>
        Failed,
    }

    public Stage Current { get; private set; } = Stage.CopyingTheMatch;

    /// <summary>Why the join failed, for the player; null unless it has.</summary>
    public string? Failure { get; private set; }

    /// <summary>The slot this player drives once seated; -1 before.</summary>
    public int Slot { get; private set; } = -1;

    /// <summary>The session that steps the match once seated; null before.</summary>
    public NetSession? Session { get; private set; }

    /// <summary>Tells the relay the world is built: hold the match and send a snapshot.</summary>
    public void Begin() => _link.SendJoin(new ReadyToLoad());

    /// <summary>Takes in what the relay sent: the snapshot, then the answer about the seat.</summary>
    public void Receive()
    {
        while (Current is Stage.CopyingTheMatch or Stage.TakingASeat && _link.TryReceiveJoin(out Message? message))
        {
            switch (message)
            {
                case SnapshotOffer offer when Current == Stage.CopyingTheMatch && _offer == null:
                    if (offer.Length is <= 0 or > MessageCodec.MaxSnapshotBytes)
                    {
                        Fail("The match copy offered is not a size a match can be.");
                        break;
                    }
                    _offer = offer;
                    _received = new SnapshotAssembly(offer.Tick, offer.Length);
                    break;
                case SnapshotChunk chunk when Current == Stage.CopyingTheMatch && _received != null:
                    if (!_received.Add(chunk))
                        Fail("The match copy arrived out of order.");
                    else if (_received.Length == _offer!.Length)
                        Load(_offer, _received.ToArray());
                    break;
                case Seated seated when Current == Stage.TakingASeat && seated.Slot == _askedSlot:
                    if (seated.AtTick != _world.AdvanceCount)
                    {
                        Fail("The relay seated this player at another tick than the match copy's.");
                        break;
                    }
                    Slot = seated.Slot;
                    Session = NetSession.FromSnapshot(_link, _world, _seats!, _snapshot!);
                    Current = Stage.Seated;
                    break;
                case SeatRefused when Current == Stage.TakingASeat:
                    _refused.Add(_askedSlot);
                    AskForSeat();
                    break;
            }
        }
    }

    /// <summary>
    /// Loads the snapshot and checks it (docs/late-join-design.md §10.3, item 4): the bytes are the ones the
    /// uploaders saved, saving the loaded world gives them back, and its sync digest is the uploaders'.
    /// </summary>
    private void Load(SnapshotOffer offer, byte[] compressed)
    {
        try
        {
            byte[] snapshot = WorldSnapshot.Decompress(compressed);
            if (WorldSnapshot.Digest(snapshot) != offer.Digest)
            {
                Fail("The match copy that arrived is not the one the players saved.");
                return;
            }
            var bots = WorldSnapshot.Load(_world, snapshot);
            if (_world.AdvanceCount != offer.Tick)
                Fail("The match copy is of another tick than the one offered.");
            else if (!WorldSnapshot.Save(_world, bots).AsSpan().SequenceEqual(snapshot))
                Fail("The match copy did not load as it was saved.");
            else if (WorldStateHash.ComputeSync(_world, bots).Combined != offer.SyncHash)
                Fail("The match copy, loaded, differs from the players' match.");
            else
            {
                _seats = new Seats(_matchSeed, bots);
                _snapshot = compressed;
                AskForSeat();
            }
        }
        catch (InvalidDataException e)
        {
            Fail("The match copy could not be loaded: " + e.Message);
        }
    }

    private void AskForSeat()
    {
        _askedSlot = PickSeat(_world, _seats!, _refused);
        if (_askedSlot < 0)
        {
            Fail("Every tank a bot drives is out of lives, or taken: there is no seat to take.");
            return;
        }
        Current = Stage.TakingASeat;
        _link.SendJoin(new TakeSeat(_askedSlot));
    }

    /// <summary>
    /// The seat to ask for (docs/late-join-design.md §6.1): of the tanks a bot drives, the one with the most lives
    /// left, and among those the lowest slot. A tank out of lives is never picked. -1 when there is none.
    /// </summary>
    public static int PickSeat(World world, Seats seats, IReadOnlyCollection<int> excluded)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(seats);
        ArgumentNullException.ThrowIfNull(excluded);
        var tanks = world.TankList.Tanks;
        int best = -1;
        for (int slot = 0; slot < tanks.Count; slot++)
        {
            if (!seats.IsBotSeat(slot) || excluded.Contains(slot) || MatchReferee.IsOutOfLives(tanks[slot]))
                continue;
            if (best < 0 || tanks[slot].LivesLeft > tanks[best].LivesLeft)
                best = slot;
        }
        return best;
    }

    private void Fail(string reason)
    {
        Current = Stage.Failed;
        Failure = reason;
        _link.SendJoin(new JoinFailed(reason));
    }
}
