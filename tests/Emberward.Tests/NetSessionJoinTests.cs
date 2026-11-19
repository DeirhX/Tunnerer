namespace Emberward.Tests;

using Emberward.Core;
using Emberward.Core.LevelGen;
using Emberward.Core.Snapshots;
using Emberward.Core.Types;
using Emberward.Desktop.Net;
using Emberward.Protocol;

/// <summary>
/// Joining a match in progress, through the in-memory relay (docs/late-join-design.md §13, step 4): the match holds,
/// the players in it upload a snapshot, the joiner loads it, takes over a bot's tank and plays on in step with them.
/// A class of its own because the test is slow; the quicker join tests are in <see cref="NetSessionTests"/>, and use
/// <see cref="PlayAndJoin"/> too.
/// </summary>
public class NetSessionJoinTests
{
    private const int Slots = 4, Players = 0b0011, MatchSeed = 42;

    internal static MatchConfig Config { get; } = NetMatch.ProposeConfig(
        MatchSeed, new Size(480, 300), LevelGenMode.Optimized, players: Slots);

    /// <summary>A client of the match: its world and session, and the slot it plays (scripted input).</summary>
    internal sealed class Client(World world, NetSession session, int slot)
    {
        public World World { get; } = world;
        public NetSession Session { get; } = session;
        public int Slot { get; } = slot;

        /// <summary>Steps what has arrived, sending one input for each tick simulated, as the game does.</summary>
        public void Play()
        {
            Session.Receive();
            while (Session.TryStep())
                Session.SendInput(NetSessionTests.ScriptedInput(Slot, World.AdvanceCount));
        }

        /// <summary>A fresh snapshot of the world and the bots: equal bytes are equal worlds, all of them.</summary>
        public byte[] Save() => WorldSnapshot.Save(World, Session.Bots);
    }

    /// <summary>A match after a join: the players' clients and then the joiner's, all settled at the same tick.</summary>
    internal sealed record JoinedMatch(InMemoryRelay Relay, List<Client> Clients, LateJoin Join, int HeldAt);

    /// <summary>
    /// Two players in slots 0 and 1 and bots in 2 and 3 play until <paramref name="joinAt"/> ticks are closed; a fifth
    /// client then builds the world, joins, and all play <paramref name="playedAfter"/> ticks more, and settle.
    /// </summary>
    internal static JoinedMatch PlayAndJoin(int relaySeed, int joinAt, int playedAfter)
    {
        var relay = new InMemoryRelay(Slots, relaySeed, maxDelay: 2);
        relay.SeatBot(2);
        relay.SeatBot(3);
        var clients = new List<Client>();
        for (int slot = 0; slot < 2; slot++)
        {
            World world = MatchRecording.BuildWorld(Config);
            var session = new NetSession(relay.LinkFor(slot), world);
            session.StartSeats(MatchSeed, Players);
            clients.Add(new Client(world, session, slot));
        }
        PlayUntil(relay, clients, joinAt);

        World joinerWorld = MatchRecording.BuildWorld(Config);
        var join = new LateJoin(relay.AddJoiner(), joinerWorld, MatchSeed);
        join.Begin();
        int heldAt = -1;
        for (int i = 0; i < 200 && join.Current is not (LateJoin.Stage.Seated or LateJoin.Stage.Failed); i++)
        {
            relay.Advance();
            if (heldAt < 0 && relay.Hold is { } hold)
                heldAt = hold.Tick;
            PlayAll(clients);
            join.Receive();
        }
        Assert.Null(join.Failure);
        Assert.Equal(LateJoin.Stage.Seated, join.Current);
        clients.Add(new Client(joinerWorld, join.Session!, join.Slot));

        PlayUntil(relay, clients, heldAt + playedAfter);
        for (int i = 0; i < 20; i++)
        {
            relay.Advance(closeTick: false);
            PlayAll(clients);
        }
        return new JoinedMatch(relay, clients, join, heldAt);
    }

    /// <summary>
    /// Lets every client step what has arrived. The clients share nothing but the relay's clock, which stands still
    /// meanwhile, so they step side by side.
    /// </summary>
    private static void PlayAll(List<Client> clients) => Parallel.ForEach(clients, client => client.Play());

    private static void PlayUntil(InMemoryRelay relay, List<Client> clients, int ticksClosed)
    {
        while (relay.Room.NextTick < ticksClosed)
        {
            relay.Advance();
            PlayAll(clients);
        }
    }

    /// <summary>The recording, through its text, replayed: the world and bots it rebuilds, saved.</summary>
    internal static byte[] Rebuild(MatchRecording recording)
    {
        using var text = new StringWriter();
        recording.Save(text);
        var (world, bots) = MatchRecording.Load(new StringReader(text.ToString())).Replay();
        return WorldSnapshot.Save(world, bots);
    }

    /// <summary>
    /// Every client is at the last tick closed with the same world, all of it, and the seat changed hands everywhere at
    /// the tick the match was held at.
    /// </summary>
    internal static void AssertEveryWorldTheSame(JoinedMatch match, int expectedSlot = 2)
    {
        var (relay, clients, join, heldAt) = match;
        Assert.Equal(expectedSlot, join.Slot);
        int humanMask = Players | (1 << expectedSlot);
        // Nothing moved during the hold: the joiner starts where the snapshot was saved, which is where the match held.
        Assert.Equal(heldAt, join.Session!.StartTick);
        Assert.Equal(1, relay.OffersSent);
        Assert.Null(relay.Room.DesyncTick);
        Assert.Equal(humanMask, relay.Room.HumanMask);

        byte[] expected = clients[0].Save();
        foreach (var client in clients)
        {
            Assert.Equal(relay.Room.NextTick, client.World.AdvanceCount);
            Assert.Equal(expected, client.Save());
            Assert.Null(client.Session.Bots[expectedSlot]);
            Assert.NotNull(client.Session.Bots[expectedSlot == 2 ? 3 : 2]);
            Assert.Equal(humanMask, client.Session.HumanMask);
            Assert.False(client.World.TankBases.Bases[expectedSlot].IsBotControlled);
        }

        // The joiner's inputs drove its tank from the tick it was seated at.
        var log = relay.Room.Log;
        Assert.All(log.Take(heldAt), bundle => Assert.Equal(Players, bundle.HumanMask));
        Assert.All(log.Skip(heldAt), bundle => Assert.Equal(humanMask, bundle.HumanMask));
        Assert.Contains(log.Skip(heldAt), bundle => bundle.Inputs[expectedSlot] != LockstepRoom.NeutralInput);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void PlayerJoiningAtTick2400_TakesOverABot_AndEveryWorldStaysTheSame()
    {
        var match = PlayAndJoin(relaySeed: 3, joinAt: 2400, playedAfter: 240);

        Assert.True(match.HeldAt >= 2400, $"held at {match.HeldAt}");
        // Slots 0 and 1 are players; the joiner takes the first bot whose base still stands (LateJoin.PickSeat), even
        // while that bot's tank is respawning.
        AssertEveryWorldTheSame(match, expectedSlot: 2);
        // The joiner's recording starts from its snapshot and rebuilds the same world. (A player's, from tick 0 with
        // the seat changing in it, is checked on a shorter match in NetSessionTests: replaying 2400 ticks costs.)
        Assert.Equal(match.Clients[0].Save(), Rebuild(match.Join.Session!.Recording(Config)));
    }
}
