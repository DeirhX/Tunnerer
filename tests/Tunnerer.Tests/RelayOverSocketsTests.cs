namespace Tunnerer.Tests;

using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Tunnerer.Core;
using Tunnerer.Core.Input;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Net;
using Tunnerer.Protocol;
using Tunnerer.Relay;

/// <summary>The real relay on a local port, with real WebSocket clients.</summary>
public class RelayOverSocketsTests
{
    private static readonly Size MapSize = new(320, 200);

    private static async Task<(WebApplication App, Uri Play)> StartRelayAsync(string secret = "")
    {
        var app = RelayApp.Build([
            "--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default=Warning", $"--{RelayHub.SecretKey}={secret}"]);
        await app.StartAsync();
        var http = new Uri(app.Urls.Single());
        return (app, new Uri($"ws://127.0.0.1:{http.Port}/play"));
    }

    private static NetLaunchOptions Options(Uri play, string name) => new(play, "test-room", name, Players: 2);

    private static MatchConfig Config(int seed) =>
        NetLaunch.ProposeConfig(seed, MapSize, Core.LevelGen.LevelGenMode.Deterministic, players: 2);

    private static void WaitFor(Func<bool> condition, params RelayConnection[] pump)
    {
        var limit = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(limit.Elapsed < TimeSpan.FromSeconds(10), "Timed out waiting for the relay.");
            Thread.Sleep(5);
            foreach (var connection in pump)
                connection.Pump();
        }
    }

    [Fact]
    public async Task TwoClients_PlayTheSameMatchThroughTheRelay()
    {
        var (app, play) = await StartRelayAsync();
        await using var _ = app;
        // The second client proposes another seed; the room's settings, from the first, win.
        using var a = NetLaunch.Join(Options(play, "Ann"), Config(seed: 42));
        using var b = NetLaunch.Join(Options(play, "Bob"), Config(seed: 77));
        Assert.Equal((0, 1), (a.Slot, b.Slot));
        Assert.Equal(42, b.Config.Seed);

        var connections = new[] { a, b };
        var worlds = connections.Select(c => TestHelpers.CreateSeededWorld(MapSize, c.Config.Seed)).ToArray();
        var sessions = connections.Select((c, i) => new NetSession(c, worlds[i])).ToArray();
        WaitFor(() => a.Roster.Count(n => n.Length > 0) == 2, a);
        Assert.Equal("Waiting for players: 2 of 2 here.", a.Status);
        foreach (var (c, i) in connections.Select((c, i) => (c, i)))
            c.SendReady(WorldStateHash.Compute(worlds[i]));
        WaitFor(() => a.Started && b.Started, a, b);
        Assert.Null(a.Status);

        // Each client drives its tank and steps whatever ticks have arrived, like the game loop does.
        const int Ticks = 60;
        WaitFor(() =>
        {
            for (int i = 0; i < sessions.Length; i++)
            {
                sessions[i].Receive();
                while (worlds[i].AdvanceCount < Ticks && sessions[i].TryStep())
                    sessions[i].SendInput(new ControllerOutput
                    {
                        MoveSpeed = new Offset(i == 0 ? 1 : -1, worlds[i].AdvanceCount / 10 % 2),
                        ShootPrimary = worlds[i].AdvanceCount % 7 == 0,
                    });
            }
            return worlds.All(w => w.AdvanceCount == Ticks);
        }, a, b);

        Assert.Equal(WorldStateHash.Compute(worlds[0]), WorldStateHash.Compute(worlds[1]));
        // The inputs went round: the tanks are not where an idle match would have left them.
        var idle = TestHelpers.CreateSeededWorld(MapSize, 42);
        TestSimulation.AdvanceIdle(idle, Ticks);
        Assert.NotEqual(WorldStateHash.Compute(idle), WorldStateHash.Compute(worlds[0]));
        Assert.Null(a.DesyncTick);
        Assert.Null(a.Failure);
    }

    [Fact]
    public async Task ClientOfAnotherBuildOrProtocol_IsRefusedWithAReason()
    {
        var (app, play) = await StartRelayAsync();
        await using var _ = app;
        using var first = NetLaunch.Join(Options(play, "Ann"), Config(seed: 42));

        var otherBuild = new Hello(MessageCodec.ProtocolVersion, "some-other-build", "Eve", "test-room", Config(1));
        var refused = Assert.Throws<RelayException>(
            () => RelayConnection.Connect(play, otherBuild, TimeSpan.FromSeconds(5)));
        Assert.Contains("some-other-build", refused.Message, StringComparison.Ordinal);

        var otherProtocol = otherBuild with { Protocol = MessageCodec.ProtocolVersion + 1 };
        Assert.Throws<RelayException>(() => RelayConnection.Connect(play, otherProtocol, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task LosingTheRelay_ShowsAsAFailure()
    {
        var (app, play) = await StartRelayAsync();
        using var client = NetLaunch.Join(Options(play, "Ann"), Config(seed: 42));

        await app.StopAsync();
        await app.DisposeAsync();

        WaitFor(() => client.Failure != null, client);
        Assert.StartsWith("Disconnected", client.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelayWithASecret_LetsInOnlyClientsThatKnowIt()
    {
        var (app, play) = await StartRelayAsync(secret: "open sesame");
        await using var _ = app;

        var wrong = Assert.Throws<RelayException>(
            () => NetLaunch.Join(Options(play, "Eve") with { Secret = "guess" }, Config(seed: 42)));
        Assert.Contains("secret", wrong.Message, StringComparison.Ordinal);
        Assert.Throws<RelayException>(() => NetLaunch.Join(Options(play, "Eve"), Config(seed: 42)));

        using var right = NetLaunch.Join(Options(play, "Ann") with { Secret = "open sesame" }, Config(seed: 42));
        Assert.Equal(0, right.Slot);
    }

    [Fact]
    public async Task PlayerWhoDropsOut_RejoinsAndCatchesUpToTheSameWorld()
    {
        var (app, play) = await StartRelayAsync();
        await using var _ = app;
        using var a = NetLaunch.Join(Options(play, "Ann"), Config(seed: 42));
        var b = NetLaunch.Join(Options(play, "Bob"), Config(seed: 42));
        var worldA = TestHelpers.CreateSeededWorld(MapSize, 42);
        var worldB = TestHelpers.CreateSeededWorld(MapSize, 42);
        var sessionA = new NetSession(a, worldA);
        var sessionB = new NetSession(b, worldB);
        a.SendReady(WorldStateHash.Compute(worldA));
        b.SendReady(WorldStateHash.Compute(worldB));

        void Play(NetSession session, World world, int slot)
        {
            session.Receive();
            while (session.TryStep())
                session.SendInput(new ControllerOutput { MoveSpeed = new Offset(slot == 0 ? 1 : -1, 0), ShootPrimary = true });
        }

        WaitFor(() =>
        {
            Play(sessionA, worldA, 0);
            Play(sessionB, worldB, 1);
            return worldB.AdvanceCount >= 30;
        }, a, b);
        b.Dispose();

        // Bob starts the game again: a fresh world, the same room and name.
        WaitFor(() => { Play(sessionA, worldA, 0); return a.PlayersHere == 1; }, a);
        using var back = NetLaunch.Join(Options(play, "Bob"), Config(seed: 1));
        var worldBack = TestHelpers.CreateSeededWorld(MapSize, back.Config.Seed);
        var sessionBack = new NetSession(back, worldBack);
        WaitFor(() =>
        {
            Play(sessionA, worldA, 0);
            Play(sessionBack, worldBack, 1);
            return worldBack.AdvanceCount >= 80 && worldA.AdvanceCount >= 80;
        }, a, back);

        Assert.True(back.Rejoined);
        Assert.Equal(1, back.Slot);
        // Compare at a tick both have reached: replay one from the other's record of the match.
        int tick = Math.Min(worldA.AdvanceCount, worldBack.AdvanceCount);
        var fromA = TestHelpers.CreateSeededWorld(MapSize, 42);
        var fromBack = TestHelpers.CreateSeededWorld(MapSize, 42);
        for (int i = 0; i < tick; i++)
        {
            NetSession.Step(fromA, [], sessionA.Played[i]);
            NetSession.Step(fromBack, [], sessionBack.Played[i]);
        }
        Assert.Equal(WorldStateHash.ComputeSync(fromA), WorldStateHash.ComputeSync(fromBack));
        Assert.Null(a.DesyncTick);
        Assert.NotNull(back.LatencyMs);
    }

    [Fact]
    public void NoRelayAtTheAddress_IsARelayException()
    {
        var nowhere = new Uri("ws://127.0.0.1:9/play");

        Assert.Throws<RelayException>(() => NetLaunch.Join(Options(nowhere, "Ann"), Config(seed: 42)));
    }
}
