namespace Tunnerer.Tests;

using Tunnerer.Core;
using Tunnerer.Core.Input;
using Tunnerer.Core.LevelGen;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Net;
using Tunnerer.Protocol;
using Tunnerer.Core.Bots;

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

    private static BotTankAI?[] NoBots => new BotTankAI?[Slots];

    /// <summary>What a player might press: runs of driving, with shots, a turning aim and the build keys now and then.</summary>
    private static ControllerOutput ScriptedInput(int slot, int step)
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
        foreach (TickBundle bundle in relay.Room.Log)
            NetSession.Step(rejoined, NoBots, bundle);
        Assert.Equal(expected, WorldStateHash.Compute(rejoined));
    }

    [Fact]
    public void BotsInEmptySlots_StayInStepOnEveryClient()
    {
        const int Steps = 300;
        var relay = new InMemoryRelay(Slots, seed: 9, maxDelay: 2);
        static BotTankAI?[] Bots() => [null, null, new BotTankAI(seed: 44), new BotTankAI(seed: 45)];
        var worlds = new[] { CreateWorld(), CreateWorld() };
        var sessions = worlds.Select((w, slot) => new NetSession(relay.LinkFor(slot), w) { Bots = Bots() }).ToArray();
        relay.Disconnect(2);
        relay.Disconnect(3);

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

        Assert.Throws<InvalidOperationException>(() => session.Bots = NoBots);
    }

    [Fact]
    public void DecidedMatch_IsReportedToTheRelayOnce_WithTheTickItWasDecidedAt()
    {
        var relay = new InMemoryRelay(Slots, seed: 1, maxDelay: 0);
        var world = CreateWorld();
        var session = new NetSession(relay.LinkFor(0), world) { Bots = NoBots };
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
}
