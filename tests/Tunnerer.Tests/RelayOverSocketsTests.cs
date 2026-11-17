namespace Tunnerer.Tests;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Short keep-alive times, so that a connection that went silent is found within a second.</summary>
    private static readonly TimeSpan QuickInterval = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan QuickTimeout = TimeSpan.FromMilliseconds(600);

    private static async Task<(WebApplication App, Uri Play)> StartRelayAsync(string secret = "", bool quickKeepAlive = false)
    {
        var app = RelayApp.Build(
            ["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default=Warning", $"--{RelayHub.SecretKey}={secret}"],
            quickKeepAlive ? QuickInterval : null, quickKeepAlive ? QuickTimeout : null);
        await app.StartAsync();
        var http = new Uri(app.Urls.Single());
        return (app, new Uri($"ws://127.0.0.1:{http.Port}/play"));
    }

    private static NetLaunchOptions Options(Uri play, string name) => new(play, "test-room", name, Players: 2);

    private static MatchConfig Config(int seed) =>
        NetLaunch.ProposeConfig(seed, MapSize, Core.LevelGen.LevelGenMode.Deterministic, players: 2);

    private static void WaitFor(Func<bool> condition, params RelayConnection[] pump) =>
        WaitFor(condition, pump, TimeSpan.FromSeconds(10));

    private static void WaitFor(Func<bool> condition, RelayConnection[] pump, TimeSpan timeout)
    {
        var limit = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(limit.Elapsed < timeout, "Timed out waiting for the relay.");
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

        var otherProtocol = otherBuild with { BuildId = NetLaunch.BuildId, Protocol = MessageCodec.ProtocolVersion + 1 };
        var outdated = Assert.Throws<RelayException>(
            () => RelayConnection.Connect(play, otherProtocol, TimeSpan.FromSeconds(5)));
        Assert.Contains("protocol", outdated.Message, StringComparison.Ordinal);
        Assert.Contains("Update the game", outdated.Message, StringComparison.Ordinal);
    }

    private static async Task<ClientWebSocket> OpenRawAsync(Uri play)
    {
        var socket = new ClientWebSocket();
        // No pings of its own: what the relay does about a silent peer must come from the relay.
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        await socket.ConnectAsync(play, CancellationToken.None);
        return socket;
    }

    private static Task SendRawAsync(ClientWebSocket socket, byte[] bytes) =>
        socket.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None);

    private static async Task<Message?> ReadRawAsync(ClientWebSocket socket)
    {
        var buffer = new byte[MessageCodec.MaxMessageBytes];
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(buffer, limit.Token);
        return result.MessageType == WebSocketMessageType.Close ? null : MessageCodec.Decode(buffer.AsSpan(0, result.Count));
    }

    [Fact]
    public async Task HelloOfAnotherProtocol_IsRefusedWhateverTheRestOfItLooksLike()
    {
        var (app, play) = await StartRelayAsync();
        await using var _ = app;

        // The kind byte and the protocol number, then bytes of some other layout, and a hello cut off after them.
        byte[] prefix = [1, .. BitConverter.GetBytes(MessageCodec.ProtocolVersion - 1)];
        foreach (byte[] hello in new[] { [.. prefix, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9], prefix })
        {
            using var socket = await OpenRawAsync(play);
            await SendRawAsync(socket, hello);

            var refusal = Assert.IsType<Refusal>(await ReadRawAsync(socket));

            Assert.Contains($"you speak {MessageCodec.ProtocolVersion - 1}", refusal.Reason, StringComparison.Ordinal);
            Assert.Contains("Update the game", refusal.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ClientThatSendsTooManyMessages_IsCutOff()
    {
        var (app, play) = await StartRelayAsync();
        await using var _ = app;
        using var socket = await OpenRawAsync(play);
        await SendRawAsync(socket, MessageCodec.Encode(
            new Hello(MessageCodec.ProtocolVersion, NetLaunch.BuildId, "Eve", "flood", Config(1))));
        Assert.IsType<Welcome>(await ReadRawAsync(socket));

        byte[] ping = MessageCodec.Encode(new Ping(1));
        for (int i = 0; i < RelayHub.MaxMessagesPerSecond + 500; i++)
            await SendRawAsync(socket, ping);

        int pongs = 0;
        while (await ReadRawAsync(socket) is { } message)
            if (message is Pong)
                pongs++;
        Assert.True(pongs <= RelayHub.MaxMessagesPerSecond);
    }

    [Fact]
    public async Task SecretLongerThanTheProtocolCarries_StopsTheRelayFromStarting()
    {
        var tooLong = new string('k', MessageCodec.MaxSecretLength + 1);

        var failure = Assert.Throws<InvalidOperationException>(() => RelayApp.Build(
            ["--urls", "http://127.0.0.1:0", $"--{RelayHub.SecretKey}={tooLong}"]));

        Assert.Contains(RelayHub.SecretKey, failure.Message, StringComparison.Ordinal);
        Assert.Contains(MessageCodec.MaxSecretLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            failure.Message, StringComparison.Ordinal);

        var longest = new string('k', MessageCodec.MaxSecretLength);
        var (app, play) = await StartRelayAsync(secret: longest);
        await using var _ = app;
        using var client = NetLaunch.Join(Options(play, "Ann") with { Secret = longest }, Config(seed: 42));
        Assert.Equal(0, client.Slot);
    }

    [Fact]
    public async Task RoomWithAnUnknownLevelGenerator_IsRefusedByTheClient()
    {
        var (app, play) = await StartRelayAsync();
        await using var _ = app;
        var odd = Config(seed: 1) with { GenMode = 99 };

        var refused = Assert.Throws<RelayException>(() => NetLaunch.Join(Options(play, "Eve") with { Room = "odd" }, odd));

        Assert.Contains("level generator", refused.Message, StringComparison.Ordinal);
    }

    private static Hello HelloFor(string name) =>
        new(MessageCodec.ProtocolVersion, NetLaunch.BuildId, name, "test-room", Config(seed: 42));

    [Fact]
    public async Task PeerThatStopsAnswering_LosesItsSlot_AndCanRejoinUnderItsName()
    {
        var (app, play) = await StartRelayAsync(quickKeepAlive: true);
        await using var _ = app;
        using var a = NetLaunch.Join(Options(play, "Ann"), Config(seed: 42));
        // Bob connects and then never reads again, so nothing answers the relay's pings: a vanished client.
        using var frozen = await OpenRawAsync(play);
        await SendRawAsync(frozen, MessageCodec.Encode(HelloFor("Bob")));
        Assert.IsType<Welcome>(await ReadRawAsync(frozen));
        await SendRawAsync(frozen, MessageCodec.Encode(new Ready(1)));
        a.SendReady(1);
        WaitFor(() => a.Started, a);
        Assert.Equal(2, a.PlayersHere);

        WaitFor(() => a.PlayersHere == 1, a);

        using var back = NetLaunch.Join(Options(play, "Bob"), Config(seed: 1));
        WaitFor(() => back.Rejoined, back, a);
        Assert.Equal(1, back.Slot);
    }

    [Fact]
    public async Task ClientWhoseGameLoopIsBusy_IsNotDropped()
    {
        var (app, play) = await StartRelayAsync(quickKeepAlive: true);
        await using var _ = app;
        var keepAlive = new RelayConnection.KeepAlive(QuickInterval, QuickTimeout);
        using var busy = RelayConnection.Connect(play, HelloFor("Ann"), TimeSpan.FromSeconds(5), keepAlive);
        using var other = RelayConnection.Connect(play, HelloFor("Bob"), TimeSpan.FromSeconds(5), keepAlive);

        // Several keep-alive periods in a lobby with nothing to say, during which the busy client never pumps, as
        // when its game loop is building a world.
        var until = Stopwatch.StartNew();
        WaitFor(() => { other.Pump(); return until.Elapsed > TimeSpan.FromSeconds(2.5); }, other);

        busy.Pump();
        Assert.Null(busy.Failure);
        Assert.Equal(2, other.PlayersHere);
        Assert.Null(other.Failure);
    }

    /// <summary>
    /// A relay that completes the handshake and says welcome, then never says anything again and does not answer
    /// a ping: its TCP connection stays up, as behind a proxy.
    /// </summary>
    private static async Task<(TcpListener Listener, Uri Play, Task Serving)> StartSilentRelayAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var play = new Uri($"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/play");
        var serving = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            var stream = tcp.GetStream();
            var request = new StringBuilder();
            var one = new byte[1];
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                await stream.ReadExactlyAsync(one);
                request.Append((char)one[0]);
            }
            string key = Regex.Match(request.ToString(), "Sec-WebSocket-Key: (.+)\r\n", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));

            // The client's hello: a masked frame, short enough for a one-byte length.
            var header = new byte[2];
            await stream.ReadExactlyAsync(header);
            await stream.ReadExactlyAsync(new byte[4 + (header[1] & 0x7F)]);
            byte[] welcome = MessageCodec.Encode(new Welcome(0, Config(seed: 42)));
            await stream.WriteAsync(new byte[] { 0x82, (byte)welcome.Length }.Concat(welcome).ToArray());
            // Then nothing is ever sent back: read and ignore, until the client gives up and closes.
            try
            {
                var sink = new byte[256];
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (await stream.ReadAsync(sink, limit.Token) > 0)
                {
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException)
            {
            }
        });
        return (listener, play, serving);
    }

    [Fact]
    public async Task RelayThatGoesSilent_ShowsAsAFailure()
    {
        var (listener, play, serving) = await StartSilentRelayAsync();
        try
        {
            using var client = RelayConnection.Connect(
                play, HelloFor("Ann"), TimeSpan.FromSeconds(5), new RelayConnection.KeepAlive(QuickInterval, QuickTimeout));
            Assert.Null(client.Failure);

            WaitFor(() => client.Failure != null, client);

            Assert.Contains("lost the connection to the relay", client.Failure, StringComparison.Ordinal);
            Assert.StartsWith("Disconnected", client.Status, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    [Fact]
    public async Task StartedMatch_SurvivesBeingEmpty_AndTheClockWaitsForTheReturn()
    {
        var (app, play) = await StartRelayAsync();
        await using var _ = app;
        var hub = app.Services.GetRequiredService<RelayHub>();
        var a = NetLaunch.Join(Options(play, "Ann"), Config(seed: 42));
        var b = NetLaunch.Join(Options(play, "Bob"), Config(seed: 42));
        a.SendReady(1);
        b.SendReady(1);
        WaitFor(() => a.Started && b.Started, a, b);
        int seen = 0;
        WaitFor(() =>
        {
            while (a.TryReceive(out TickBundle? tickA))
                seen++;
            return seen >= 10;
        }, a);
        a.Dispose();
        b.Dispose();
        // The room has no one in it: a lobby would be gone by now, and a match that went on would be 14 ticks on.
        Thread.Sleep(600);
        Assert.Equal(1, hub.RoomCount);

        using var back = NetLaunch.Join(Options(play, "Bob"), Config(seed: 1));
        int caughtUp = 0;
        WaitFor(() =>
        {
            while (back.TryReceive(out TickBundle? tickBack))
                caughtUp++;
            return back.Rejoined && caughtUp > 0;
        }, back);

        // Everything so far, and at most the few ticks of the moments since: no stall to make up, no jump.
        Assert.InRange(caughtUp, seen, seen + 8);
        Assert.Null(back.Failure);
    }

    [Fact]
    public async Task StartedMatchNobodyReturnsTo_IsDeletedAfterTheGrace()
    {
        var (app, play) = await StartRelayAsync();
        await using var _ = app;
        var hub = app.Services.GetRequiredService<RelayHub>();
        hub.EmptyRoomGrace = TimeSpan.FromMilliseconds(300);
        var a = NetLaunch.Join(Options(play, "Ann"), Config(seed: 42));
        var b = NetLaunch.Join(Options(play, "Bob"), Config(seed: 42));
        a.SendReady(1);
        b.SendReady(1);
        WaitFor(() => a.Started && b.Started, a, b);
        a.Dispose();
        b.Dispose();

        WaitFor(() => hub.RoomCount == 0, []);
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

    private static readonly Size FourPlayerMap = new(480, 300);

    private static World CreateFourTankWorld(int seed)
    {
        var (terrain, spawns) = new Core.LevelGen.ToastGenerator().Generate(FourPlayerMap, seed, playerCount: 4);
        var world = new World(FourPlayerMap, simulationSeed: seed);
        world.Initialize(terrain, spawns, decorationSeed: seed + 1);
        return world;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task PlayerWhoDropsOut_RejoinsAndCatchesUpToTheSameWorld()
    {
        var (app, play) = await StartRelayAsync();
        await using var _ = app;
        // Two players and two bots: the bots must run alike on the client that stayed and the one that came back.
        var config = NetLaunch.ProposeConfig(42, FourPlayerMap, Core.LevelGen.LevelGenMode.Deterministic, players: 4);
        using var a = NetLaunch.Join(Options(play, "Ann"), config);
        var b = NetLaunch.Join(Options(play, "Bob"), config);
        var worldA = CreateFourTankWorld(42);
        var worldB = CreateFourTankWorld(42);
        var sessionA = new NetSession(a, worldA);
        var sessionB = new NetSession(b, worldB);
        a.SendReady(WorldStateHash.Compute(worldA));
        b.SendReady(WorldStateHash.Compute(worldB));
        a.RequestStart();

        // Steps what has arrived, up to a tick; the bots are chosen when the relay says the match has started.
        void Play(NetSession session, RelayConnection connection, World world, int limit = int.MaxValue)
        {
            session.Receive();
            if (connection.Started && world.AdvanceCount == 0 && session.Bots.Count == 0)
                session.Bots = NetSession.CreateBots(world, connection.Config.Seed, connection.HumanMask);
            while (world.AdvanceCount < limit && session.TryStep())
                session.SendInput(new ControllerOutput
                {
                    MoveSpeed = new Offset(connection.Slot == 0 ? 1 : -1, world.AdvanceCount / 15 % 2),
                    ShootPrimary = true,
                });
        }

        WaitFor(() =>
        {
            Play(sessionA, a, worldA);
            Play(sessionB, b, worldB);
            return worldB.AdvanceCount >= 30;
        }, a, b);
        b.Dispose();

        // Bob starts the game again: a fresh world, the same room and name.
        WaitFor(() => { Play(sessionA, a, worldA); return a.PlayersHere == 1; }, a);
        using var back = NetLaunch.Join(Options(play, "Bob"), config with { Seed = 1 });
        var worldBack = CreateFourTankWorld(back.Config.Seed);
        var sessionBack = new NetSession(back, worldBack);
        const int Compared = 120;
        WaitFor(() =>
        {
            Play(sessionA, a, worldA, Compared);
            Play(sessionBack, back, worldBack, Compared);
            return worldBack.AdvanceCount == Compared && worldA.AdvanceCount == Compared;
        }, a, back);

        Assert.True(back.Rejoined);
        Assert.Equal(1, back.Slot);
        Assert.Equal(0b0011, back.HumanMask);
        // The rejoining client's own world, stepped from the relay's log, is the one the client that stayed has.
        Assert.Equal(WorldStateHash.ComputeSync(worldA), WorldStateHash.ComputeSync(worldBack));
        // And the match is not an idle one: the bots and the players moved their tanks.
        var untouched = CreateFourTankWorld(42);
        for (int slot = 0; slot < 4; slot++)
            Assert.NotEqual(untouched.TankList.Tanks[slot].Position, worldBack.TankList.Tanks[slot].Position);
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
