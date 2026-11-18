namespace Tunnerer.Tests;

using Tunnerer.Core;
using Tunnerer.Core.Input;
using Tunnerer.Core.LevelGen;
using Tunnerer.Core.Snapshots;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Net;
using Tunnerer.Protocol;

/// <summary>
/// Several clients, each with its own world, play one match through an in-memory relay. Whatever the connections
/// do, every world must end in the same state.
/// </summary>
public class NetSessionTests
{
    private const int Slots = 4;
    private static readonly Size MapSize = new(480, 300);
    private static World CreateWorld(int seed = 42)
    {
        var (terrain, spawns) = new ToastGenerator().Generate(MapSize, seed: seed, playerCount: Slots);
        var world = new World(MapSize, simulationSeed: seed);
        world.Initialize(terrain, spawns, decorationSeed: seed + 1);
        return world;
    }


    /// <summary>What a player might press: runs of driving, with shots, a turning aim and the build keys now and then.</summary>
    internal static ControllerOutput ScriptedInput(int slot, int step)
    {
        int phase = (step + slot * 37) / 20;
        var random = new Random(slot * 7919 + phase);
        return new ControllerOutput
        {
            MoveSpeed = new Offset(random.Next(-1, 2), random.Next(-1, 2)),
            ShootPrimary = random.Next(3) == 0,
            BuildPrimary = random.Next(8) == 0,
            BuildSecondary = random.Next(8) == 0,
            BuildTertiary = random.Next(8) == 0,
            Upgrade = random.Next(10) == 0 ? random.Next(1, 5) : 0,
            AimDirection = random.Next(4) == 0 ? default : DirectionF.FromAngle(step * 0.07f + slot),
        };
    }

    private static void StepAll(NetSession session)
    {
        session.Receive();
        while (session.TryStep())
        {
        }
    }

    /// <summary>Lets everything in flight arrive, without the relay closing more ticks.</summary>
    private static void Drain(InMemoryRelay relay, IReadOnlyList<NetSession?> sessions)
    {
        for (int i = 0; i < 50; i++)
        {
            relay.Advance(closeTick: false);
            foreach (var session in sessions)
                if (session != null)
                    StepAll(session);
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void FourClients_OnUnevenLinks_WithAStallAndADropout_EndInTheSameWorld()
    {
        // A 20-tick stall still overflows the relay's queue (LockstepRoom.MaxQueuedInputs is 4) many times over.
        const int Steps = 160, StallFrom = 40, StallUntil = 60, DropAt = 100, Dropped = 3, Staller = 1;
        var relay = new InMemoryRelay(Slots, seed: 5, maxDelay: 3);
        var worlds = Enumerable.Range(0, Slots).Select(_ => CreateWorld()).ToArray();
        var sessions = worlds.Select((w, slot) => (NetSession?)new NetSession(relay.LinkFor(slot), w)).ToArray();
        var held = new List<ControllerOutput>();
        var spawnHash = WorldStateHash.Compute(worlds[0]);

        for (int step = 0; step < Steps; step++)
        {
            if (step == DropAt)
            {
                relay.Disconnect(Dropped);
                sessions[Dropped] = null;
            }

            for (int slot = 0; slot < Slots; slot++)
            {
                if (sessions[slot] is not { } session)
                    continue;
                var input = ScriptedInput(slot, step);
                // The stalling client's machine hangs for a while, then sends everything it held at once.
                if (slot == Staller && step >= StallFrom && step < StallUntil)
                {
                    held.Add(input);
                    continue;
                }
                if (slot == Staller && held.Count > 0)
                {
                    foreach (var late in held)
                        session.SendInput(late);
                    held.Clear();
                }
                session.SendInput(input);
            }

            relay.Advance();
            foreach (var session in sessions)
                if (session != null && !(session == sessions[Staller] && step >= StallFrom && step < StallUntil))
                    StepAll(session);
        }
        Drain(relay, sessions);

        // The run exercised what it claims to: tanks did things, and the stall overflowed the relay's queue.
        Assert.NotEqual(spawnHash, WorldStateHash.Compute(worlds[0]));
        Assert.True(relay.Room.DroppedInputs(Staller) > 0);
        Assert.Equal(Steps, relay.Room.NextTick);

        ulong expected = WorldStateHash.Compute(worlds[0]);
        for (int slot = 0; slot < Slots; slot++)
        {
            if (slot == Dropped)
                continue;
            Assert.Equal(Steps, worlds[slot].AdvanceCount);
            Assert.Equal(expected, WorldStateHash.Compute(worlds[slot]));
        }
        Assert.Null(relay.Room.DesyncTick);

        // The client that dropped out comes back: a fresh world and the relay's log give the same state.
        var rejoined = CreateWorld();
        var seats = Seats.AllPlayers(Slots);
        foreach (TickBundle bundle in relay.Room.Log)
            seats.Step(rejoined, bundle);
        Assert.Equal(expected, WorldStateHash.Compute(rejoined));
    }

    [Fact]
    public void BotsInEmptySlots_StayInStepOnEveryClient()
    {
        const int Steps = 300;
        var relay = new InMemoryRelay(Slots, seed: 9, maxDelay: 2);
        var worlds = new[] { CreateWorld(), CreateWorld() };
        var sessions = worlds.Select((w, slot) => new NetSession(relay.LinkFor(slot), w)).ToArray();
        foreach (var session in sessions)
            session.StartSeats(matchSeed: 42, humanMask: 0b0011);
        relay.SeatBot(2);
        relay.SeatBot(3);

        for (int step = 0; step < Steps; step++)
        {
            for (int slot = 0; slot < sessions.Length; slot++)
                sessions[slot].SendInput(ScriptedInput(slot, step));
            relay.Advance();
            foreach (var session in sessions)
                StepAll(session);
        }
        Drain(relay, sessions);

        Assert.Equal(Steps, worlds[0].AdvanceCount);
        Assert.Equal(WorldStateHash.Compute(worlds[0]), WorldStateHash.Compute(worlds[1]));
        Assert.Null(relay.Room.DesyncTick);
        // The bots did drive: their tanks left the spawn.
        Assert.NotEqual(CreateWorld().TankList.Tanks[2].Position, worlds[0].TankList.Tanks[2].Position);
    }

    [Fact]
    public void ClientsWithDifferentWorlds_AreReportedAsADesync()
    {
        var relay = new InMemoryRelay(Slots, seed: 1, maxDelay: 0);
        var sessions = new[]
        {
            new NetSession(relay.LinkFor(0), CreateWorld(seed: 42)),
            new NetSession(relay.LinkFor(1), CreateWorld(seed: 43)),
        };

        for (int step = 0; step < NetSession.HashIntervalTicks + 2; step++)
        {
            relay.Advance();
            foreach (var session in sessions)
                StepAll(session);
        }

        Assert.Equal(NetSession.HashIntervalTicks, relay.Room.DesyncTick);
    }

    [Fact]
    public void SessionOnASteppedWorld_IsRefused()
    {
        var world = CreateWorld();
        world.Advance(_ => default);

        Assert.Throws<ArgumentException>(() => new NetSession(new InMemoryRelay(Slots, 1, 0).LinkFor(0), world));
    }

    [Fact]
    public void BotsCannotChange_OnceTheMatchHasStepped()
    {
        var relay = new InMemoryRelay(Slots, seed: 1, maxDelay: 0);
        var session = new NetSession(relay.LinkFor(0), CreateWorld());
        relay.Advance();
        StepAll(session);

        Assert.Throws<InvalidOperationException>(() => session.StartSeats(matchSeed: 42, humanMask: 0b0011));
    }

    [Fact]
    public void DecidedMatch_IsReportedToTheRelayOnce_WithTheTickItWasDecidedAt()
    {
        var relay = new InMemoryRelay(Slots, seed: 1, maxDelay: 0);
        var world = CreateWorld();
        var session = new NetSession(relay.LinkFor(0), world);
        relay.Advance();
        StepAll(session);
        Assert.Null(relay.LinkFor(0).MatchOverTick);

        // Every tank but the first loses its last life.
        foreach (var tank in world.TankList.Tanks.Skip(1))
        {
            tank.LivesLeft = 1;
            tank.Die();
        }
        relay.Advance();
        StepAll(session);
        Assert.Equal(2, relay.LinkFor(0).MatchOverTick);

        relay.Advance();
        StepAll(session);
        Assert.Equal(2, relay.LinkFor(0).MatchOverTick);
    }

    [Fact]
    public void SyncDigest_SeesABasesOreAndUpgrades()
    {
        var world = CreateWorld();
        SyncDigest before = WorldStateHash.ComputeSync(world);

        world.TankBases.Bases[0].Store.Ore += 1;

        SyncDigest after = WorldStateHash.ComputeSync(world);
        Assert.Equal(before.State, after.State);
        Assert.NotEqual(before.Economy, after.Economy);
        Assert.NotEqual(before.Combined, after.Combined);
    }

    // Joining a match in progress (docs/late-join-design.md). The full join, at tick 2400, is slow and in
    // NetSessionJoinTests.

    /// <summary>Two players and two bots, run for a while through the relay and settled: both worlds at the same tick.</summary>
    private static (InMemoryRelay Relay, World[] Worlds, NetSession[] Sessions) TwoPlayersAndTwoBots(int ticks)
    {
        var relay = new InMemoryRelay(Slots, seed: 3, maxDelay: 1);
        relay.SeatBot(2);
        relay.SeatBot(3);
        var worlds = new[] { CreateWorld(), CreateWorld() };
        var sessions = worlds.Select((w, slot) => new NetSession(relay.LinkFor(slot), w)).ToArray();
        foreach (var session in sessions)
            session.StartSeats(matchSeed: 42, humanMask: 0b0011);
        for (int step = 0; step < ticks; step++)
        {
            for (int slot = 0; slot < sessions.Length; slot++)
                sessions[slot].SendInput(ScriptedInput(slot, step));
            relay.Advance();
            foreach (var session in sessions)
                StepAll(session);
        }
        Drain(relay, sessions);
        return (relay, worlds, sessions);
    }

    [Fact]
    public void PlayerJoiningEarly_TakesOverABot_AndEveryRecordingRebuildsTheSameWorld()
    {
        var match = NetSessionJoinTests.PlayAndJoin(relaySeed: 4, joinAt: 48, playedAfter: 48);

        NetSessionJoinTests.AssertEveryWorldTheSame(match);
        byte[] expected = match.Clients[0].Save();
        // A player's recording runs from tick 0 with a mask line where the seat changed; the joiner's starts from
        // its snapshot.
        var recording = match.Clients[0].Session.Recording(NetSessionJoinTests.Config);
        using var text = new StringWriter();
        recording.Save(text);
        Assert.Contains($"{Environment.NewLine}mask=7{Environment.NewLine}", text.ToString(), StringComparison.Ordinal);
        Assert.Equal(expected, NetSessionJoinTests.Rebuild(recording));
        Assert.Equal(expected, NetSessionJoinTests.Rebuild(match.Join.Session!.Recording(NetSessionJoinTests.Config)));
    }

    [Fact]
    public void VerifierDisagreesWithUploader_ReportsDesync_AndWithholdsOffer()
    {
        var (relay, worlds, sessions) = TwoPlayersAndTwoBots(ticks: 48);
        int tick = relay.Room.NextTick;
        Assert.All(worlds, w => Assert.Equal(tick, w.AdvanceCount));
        // A difference only a snapshot sees: the sync digest does not read the thermal ledger, the snapshot does.
        SyncDigest before = WorldStateHash.ComputeSync(worlds[1], sessions[1].Bots);
        worlds[1].Terrain.Ledger.RecordInjected(1.0);
        Assert.Equal(before, WorldStateHash.ComputeSync(worlds[1], sessions[1].Bots));

        var join = new LateJoin(relay.AddJoiner(), CreateWorld(), matchSeed: 42);
        join.Begin();
        for (int i = 0; i < 20; i++)
        {
            relay.Advance();
            foreach (var session in sessions)
                StepAll(session);
            join.Receive();
        }

        Assert.Equal(tick, relay.Hold?.Tick);
        Assert.Null(relay.Hold!.Offer);
        Assert.Equal(tick, relay.Room.DesyncTick);
        Assert.Equal(0, relay.OffersSent);
        Assert.Equal(LateJoin.Stage.CopyingTheMatch, join.Current);
        // Held: nothing moved while the join was on.
        Assert.All(worlds, w => Assert.Equal(tick, w.AdvanceCount));
    }

    /// <summary>A link whose messages from the relay the test puts there, and which keeps what is sent.</summary>
    private sealed class ScriptedLink : ILockstepLink
    {
        public Queue<Message> FromRelay { get; } = new();
        public List<Message> Sent { get; } = [];

        public void SendInput(uint input) => Sent.Add(new Input(input));
        public void SendHash(int tick, ulong hash) => Sent.Add(new Hash(tick, hash));
        public void SendMatchOver(int tick) => Sent.Add(new MatchOver(tick));
        public void SendJoin(Message message) => Sent.Add(message);

        public bool TryReceive([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TickBundle? bundle)
        {
            bundle = FromRelay.TryPeek(out var next) && next is Tick tick ? tick.Bundle : null;
            if (bundle != null)
                FromRelay.Dequeue();
            return bundle != null;
        }

        public bool TryReceiveJoin([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Message? message)
        {
            message = FromRelay.TryPeek(out var next) && next is not Tick ? next : null;
            if (message != null)
                FromRelay.Dequeue();
            return message != null;
        }
    }

    [Fact]
    public void VerifierSendsOnlyTheHashesOfItsOwnSnapshot()
    {
        var link = new ScriptedLink();
        var world = CreateWorld();
        var session = new NetSession(link, world);
        link.FromRelay.Enqueue(new SnapshotRequest(0, SendChunks: false));

        session.Receive();

        var snapshot = WorldSnapshot.Save(world, session.Bots);
        Assert.Equal(new SnapshotDone(0, 0, WorldSnapshot.Digest(snapshot),
            WorldStateHash.ComputeSync(world, session.Bots).Combined), Assert.Single(link.Sent));
    }

    /// <summary>What an uploader sends for a match two players and two bots have played for 30 ticks.</summary>
    private static (SnapshotOffer Offer, SnapshotChunk[] Chunks) UploadOfAMatch()
    {
        var (_, worlds, sessions) = TwoPlayersAndTwoBots(ticks: 30);
        int tick = worlds[0].AdvanceCount;
        byte[] snapshot = WorldSnapshot.Save(worlds[0], sessions[0].Bots);
        byte[] compressed = WorldSnapshot.Compress(snapshot);
        var offer = new SnapshotOffer(tick, compressed.Length, WorldSnapshot.Digest(snapshot),
            WorldStateHash.ComputeSync(worlds[0], sessions[0].Bots).Combined);
        return (offer, SnapshotChunk.Split(tick, compressed).ToArray());
    }

    [Fact]
    public void Joiner_LoadsAndChecksTheSnapshot_AsksForTheBestBotSeat_AndAnotherWhenRefused()
    {
        var (offer, chunks) = UploadOfAMatch();
        var link = new ScriptedLink();
        var join = new LateJoin(link, CreateWorld(), matchSeed: 42);
        join.Begin();
        Assert.Equal(new ReadyToLoad(), Assert.Single(link.Sent));

        link.FromRelay.Enqueue(offer);
        foreach (var chunk in chunks)
            link.FromRelay.Enqueue(chunk);
        join.Receive();
        Assert.Equal(LateJoin.Stage.TakingASeat, join.Current);
        Assert.Equal(new TakeSeat(2), link.Sent[^1]);

        link.FromRelay.Enqueue(new SeatRefused("taken"));
        join.Receive();
        Assert.Equal(new TakeSeat(3), link.Sent[^1]);

        link.FromRelay.Enqueue(new Seated(3, offer.Tick));
        join.Receive();
        Assert.Equal(LateJoin.Stage.Seated, join.Current);
        Assert.Equal(3, join.Slot);
        Assert.Equal(offer.Tick, join.Session!.StartTick);
        Assert.Equal(0b0011, join.Session.HumanMask);

        // The next bundle hands slot 3 over: its bot goes, and the session steps on from the snapshot's tick.
        link.FromRelay.Enqueue(new Tick(new TickBundle(offer.Tick, [0u, 0u, 0u, 0u], HumanMask: 0b1011)));
        join.Session.Receive();
        Assert.True(join.Session.TryStep());
        Assert.Equal(0b1011, join.Session.HumanMask);
        Assert.Null(join.Session.Bots[3]);
        Assert.NotNull(join.Session.Bots[2]);
    }

    public static TheoryData<string> BrokenUploads => ["digest", "syncHash", "tick", "order"];

    [Theory]
    [MemberData(nameof(BrokenUploads))]
    public void Joiner_RefusesASnapshotThatFailsAnyCheck(string broken)
    {
        var (offer, chunks) = UploadOfAMatch();
        offer = broken switch
        {
            "digest" => offer with { Digest = offer.Digest + 1 },
            "syncHash" => offer with { SyncHash = offer.SyncHash + 1 },
            "tick" => offer with { Tick = offer.Tick + 1 },
            _ => offer,
        };
        var link = new ScriptedLink();
        var join = new LateJoin(link, CreateWorld(), matchSeed: 42);
        join.Begin();
        link.FromRelay.Enqueue(offer);
        // Chunks keep the tick they were cut for, so an offer of another tick does not take them either.
        if (broken == "order")
            link.FromRelay.Enqueue(chunks[0] with { Index = 1 });
        foreach (var chunk in chunks)
            link.FromRelay.Enqueue(chunk);
        join.Receive();

        Assert.Equal(LateJoin.Stage.Failed, join.Current);
        Assert.NotNull(join.Failure);
        Assert.IsType<JoinFailed>(link.Sent[^1]);
        Assert.DoesNotContain(link.Sent, m => m is TakeSeat);
    }

    [Fact]
    public void SeatPick_IsTheBotWithTheMostLives_LowestSlotFirst_NeverOneOutOfLives()
    {
        var world = CreateWorld();
        var seats = Seats.ForStart(world, matchSeed: 42, humanMask: 0b0001);
        var tanks = world.TankList.Tanks;

        Assert.Equal(1, LateJoin.PickSeat(world, seats, []));
        tanks[1].LivesLeft--;
        Assert.Equal(2, LateJoin.PickSeat(world, seats, []));
        Assert.Equal(3, LateJoin.PickSeat(world, seats, [2]));
        foreach (var tank in tanks.Skip(1))
        {
            tank.LivesLeft = 1;
            tank.Die();
        }
        Assert.Equal(-1, LateJoin.PickSeat(world, seats, []));
    }
}
