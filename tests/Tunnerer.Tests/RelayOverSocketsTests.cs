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

/// <summary>
/// The real relay on a local port, with real WebSocket clients. These tests wait with generous limits, so they run
/// beside the rest; those that time a connection closely are in <see cref="TimingSensitive"/>.
/// </summary>
public class RelayOverSocketsTests
{
    private static readonly Size MapSize = new(320, 200);

    /// <summary>
    /// Short keep-alive times, so that a connection that went silent is found within a few seconds. The timeout is
    /// not shorter: on a loaded build machine a healthy client can take most of a second to answer a ping, and the
    /// relay would drop it too.
    /// </summary>
    private static readonly TimeSpan QuickInterval = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(2);

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


    private static readonly Size FourPlayerMap = new(480, 300);

    private static World CreateFourTankWorld(int seed)
    {
        var (terrain, spawns) = new Core.LevelGen.ToastGenerator().Generate(FourPlayerMap, seed, playerCount: 4);
        var world = new World(FourPlayerMap, simulationSeed: seed);
        world.Initialize(terrain, spawns, decorationSeed: seed + 1);
        return world;
    }

    /// <summary>
    /// The socket tests whose outcome hangs on a timer or a rate (keep-alive timeouts, the message-flood cut-off, a
    /// room's clock while it is empty). They run on their own, after the tests that run side by side, where a busy
    /// machine cannot make a healthy connection look dead (<see cref="TimedTestGroup"/>).
    /// </summary>
    [Collection(TimedTestGroup.Name)]
    [Trait("Category", "Slow")]
    public class TimingSensitive
    {

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

        /// <summary>
        /// Reports the match over from <paramref name="a"/> and returns once the relay has read the report: the pause
        /// sent after it has been announced, and a connection's messages are handled in order.
        /// </summary>
        private static void ReportMatchOver(RelayConnection a, RelayConnection b)
        {
            WaitFor(() => a.TryReceive(out TickBundle? _), a, b);
            a.SendMatchOver(1);
            a.SetPaused(true);
            WaitFor(() => a.IsPaused, a, b);
        }

        [Fact]
        public async Task FinishedMatch_GivesItsRoomUp_SoTheSameNamesCanPlayAgain()
        {
            var (app, play) = await StartRelayAsync();
            await using var _ = app;
            var hub = app.Services.GetRequiredService<RelayHub>();
            using var a = NetLaunch.Join(Options(play, "Ann"), Config(seed: 42));
            var b = NetLaunch.Join(Options(play, "Bob"), Config(seed: 42));
            a.SendReady(1);
            b.SendReady(1);
            WaitFor(() => a.Started && b.Started, a, b);

            // Bob has quit; Ann still looks at the result. Bob starting the game again must get a new lobby with his
            // own settings, not the finished match back.
            ReportMatchOver(a, b);
            b.Dispose();
            using var bobAgain = NetLaunch.Join(Options(play, "Bob"), Config(seed: 43));
            Assert.Equal(43, bobAgain.Config.Seed);

            Assert.Equal(0, bobAgain.Slot);
            Assert.False(bobAgain.Started);
            Assert.Equal(1, hub.RoomCount);
            Assert.Null(a.Failure);
        }

        [Fact]
        public async Task FinishedMatchEveryoneHasLeft_IsDeletedWithoutTheGrace()
        {
            var (app, play) = await StartRelayAsync();
            await using var _ = app;
            var hub = app.Services.GetRequiredService<RelayHub>();
            var a = NetLaunch.Join(Options(play, "Ann"), Config(seed: 42));
            var b = NetLaunch.Join(Options(play, "Bob"), Config(seed: 42));
            a.SendReady(1);
            b.SendReady(1);
            WaitFor(() => a.Started && b.Started, a, b);
            ReportMatchOver(a, b);

            a.Dispose();
            b.Dispose();

            WaitFor(() => hub.RoomCount == 0, []);
        }

        [Fact]
        public async Task PlayerComingBackTooEarly_KeepsTrying_UntilTheirSlotIsFree()
        {
            var (app, play) = await StartRelayAsync();
            await using var _ = app;
            using var a = NetLaunch.Join(Options(play, "Ann"), Config(seed: 42));
            var b = NetLaunch.Join(Options(play, "Bob"), Config(seed: 42));
            a.SendReady(1);
            b.SendReady(1);
            WaitFor(() => a.Started && b.Started, a, b);

            // Bob's old connection is still up when he comes back, as it is until the relay finds it dead.
            var returning = Task.Run(() => NetLaunch.Rejoin(
                Options(play, "Bob"), Config(seed: 43), TimeSpan.FromSeconds(10), between: TimeSpan.FromMilliseconds(50)));
            await Task.Delay(300);
            Assert.False(returning.IsCompleted);
            b.Dispose();

            using var back = await returning;
            WaitFor(() => back.Rejoined, a, back);
            Assert.Equal(1, back.Slot);
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
        public void NoRelayAtTheAddress_IsARelayException()
        {
            // A port that was free a moment ago and has no listener: the system hands one out, and it is closed again.
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var nowhere = new Uri($"ws://127.0.0.1:{closedPort}/play");

            Assert.Throws<RelayException>(() => NetLaunch.Join(Options(nowhere, "Ann"), Config(seed: 42)));
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

            // The relay may end the connection while the flood is still being sent or its answers read; either way
            // the socket fails or closes, and that is the cut-off.
            byte[] ping = MessageCodec.Encode(new Ping(1));
            int pongs = 0;
            bool cutOff = false;
            try
            {
                for (int i = 0; i < RelayHub.MaxMessagesPerSecond + 500; i++)
                    await SendRawAsync(socket, ping);
                while (await ReadRawAsync(socket) is { } message)
                    if (message is Pong)
                        pongs++;
                cutOff = true;
            }
            catch (WebSocketException)
            {
                cutOff = true;
            }

            Assert.True(cutOff);
            Assert.True(pongs <= RelayHub.MaxMessagesPerSecond);
        }

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

            WaitFor(() => a.PlayersHere == 1 || a.Failure != null, a);
            Assert.Null(a.Failure);

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
            WaitFor(() => { other.Pump(); return until.Elapsed > TimeSpan.FromSeconds(4); }, other);

            busy.Pump();
            Assert.Null(busy.Failure);
            Assert.Equal(2, other.PlayersHere);
            Assert.Null(other.Failure);
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
    }

    /// <summary>
    /// The tests that play a match through the relay. The relay paces a match by the clock, and with every core busy
    /// a client falls seconds behind it, so these run on their own too (<see cref="TimedTestGroup"/>). They play at
    /// the fastest tick rate the protocol allows, so that the ticks they need take a fraction of the time.
    /// </summary>
    [Collection(TimedTestGroup.Name)]
    [Trait("Category", "Slow")]
    public class MatchPlayTests
    {
        private static MatchConfig Fast(MatchConfig config) =>
            config with { TicksPerSecond = MessageCodec.MaxTicksPerSecond };

        [Fact]
        public async Task TwoClients_PlayTheSameMatchThroughTheRelay()
        {
            var (app, play) = await StartRelayAsync();
            await using var _ = app;
            // The second client proposes another seed; the room's settings, from the first, win.
            using var a = NetLaunch.Join(Options(play, "Ann"), Fast(Config(seed: 42)));
            using var b = NetLaunch.Join(Options(play, "Bob"), Fast(Config(seed: 77)));
            Assert.Equal((0, 1), (a.Slot, b.Slot));
            Assert.Equal(42, b.Config.Seed);

            var connections = new[] { a, b };
            var worlds = connections.Select(c => TestHelpers.CreateSeededWorld(MapSize, c.Config.Seed)).ToArray();
            var sessions = connections.Select((c, i) => new NetSession(c, worlds[i])).ToArray();
            WaitFor(() => a.Roster.Count(n => n.Length > 0) == 2, a);
            Assert.Equal("Waiting for players: 2 of 2 here.", a.Status);
            foreach (var (c, i) in connections.Select((c, i) => (c, i)))
                c.SendReady(WorldStateHash.ComputeReady(worlds[i]));
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
        [Trait("Category", "Slow")]
        public async Task PlayerWhoDropsOut_RejoinsAndCatchesUpToTheSameWorld()
        {
            var (app, play) = await StartRelayAsync();
            await using var _ = app;
            // Two players and two bots: the bots must run alike on the client that stayed and the one that came back.
            var config = Fast(NetLaunch.ProposeConfig(42, FourPlayerMap, Core.LevelGen.LevelGenMode.Deterministic, players: 4));
            using var a = NetLaunch.Join(Options(play, "Ann"), config);
            var b = NetLaunch.Join(Options(play, "Bob"), config);
            var worldA = CreateFourTankWorld(42);
            var worldB = CreateFourTankWorld(42);
            var sessionA = new NetSession(a, worldA);
            var sessionB = new NetSession(b, worldB);
            a.SendReady(WorldStateHash.ComputeReady(worldA));
            b.SendReady(WorldStateHash.ComputeReady(worldB));
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

            // Bob starts the game again: a fresh world, the same room and name. Ann's world stops at the tick the two are
            // compared at, however long the relay takes to see Bob go.
            const int Compared = 120;
            WaitFor(() => { Play(sessionA, a, worldA, Compared); return a.PlayersHere == 1; }, a);
            using var back = NetLaunch.Join(Options(play, "Bob"), config with { Seed = 1 });
            var worldBack = CreateFourTankWorld(back.Config.Seed);
            var sessionBack = new NetSession(back, worldBack);
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
            // And the match is not an idle one: the bots and the players moved their tanks. A bot may be back where it
            // started by now (it goes home to cool after torching its way out through rock), so for a bot having
            // driven out of its base counts too.
            var untouched = CreateFourTankWorld(42);
            for (int slot = 0; slot < 4; slot++)
            {
                var tank = worldBack.TankList.Tanks[slot];
                bool isBot = (back.HumanMask & 1 << slot) == 0;
                Assert.True(tank.Position != untouched.TankList.Tanks[slot].Position || (isBot && tank.HasLeftBase),
                    $"the tank in slot {slot} never moved");
            }
            Assert.Null(a.DesyncTick);
            // The match is short at this pace; the first latency sample comes with the first answered ping.
            WaitFor(() => back.LatencyMs != null, back);
            Assert.NotNull(back.LatencyMs);
        }
    }
}
