namespace Tunnerer.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using Tunnerer.Browser.Client;
using Tunnerer.Core;
using Tunnerer.Core.Input;
using Tunnerer.Core.LevelGen;
using Tunnerer.Core.Types;
using Tunnerer.Desktop;
using Tunnerer.Desktop.Net;
using Tunnerer.Protocol;
using Tunnerer.Relay;

/// <summary>
/// The browser's part in a network match. It must build the desktop's world from the room's settings and step it
/// from the relay's bundles as the desktop does, so that a browser and a desktop play in one room.
/// </summary>
public class BrowserNetMatchTests
{
    private static readonly TimeSpan Now = TimeSpan.Zero;

    [Theory]
    [InlineData(2, 320, 200, LevelGenMode.Deterministic, 1)]
    [InlineData(4, 480, 300, LevelGenMode.Deterministic, 3)]
    [InlineData(3, 480, 300, LevelGenMode.Optimized, 2)]
    public void NetworkWorld_IsTheWorldTheDesktopBuilds(int players, int width, int height, LevelGenMode genMode, int slot)
    {
        var config = NetMatch.ProposeConfig(1234, new Size(width, height), genMode, players);
        var desktop = WorldFactory.Build(new WorldLaunchOptions(
            false, new Size(width, height), genMode, config.Seed, Players: players, CoreTapWins: true)).World;
        var browser = BrowserSession.ForNetwork(config, slot);

        Assert.Equal(WorldStateHash.ComputeReady(desktop), WorldStateHash.ComputeReady(browser.World));
        Assert.Equal(slot, browser.LocalSlot);
        Assert.Same(browser.World.TankList.Tanks[slot], browser.Player);
    }

    [Fact]
    public void Propose_UsesTheDesktopsMapForThePlayerCount()
    {
        Assert.Equal(Desktop.Config.NetworkTweaks.LargeMatchMapSize, Size(BrowserNetMatch.Propose(7, 4)));
        Assert.Equal(Desktop.Config.DesktopScreenTweaks.RenderSurfaceSize, Size(BrowserNetMatch.Propose(7, 2)));
        Assert.Equal(new Size(500, 400), Size(BrowserNetMatch.Propose(7, 2, new Size(500, 400))));
        Assert.Equal(Core.Config.Tweaks.World.MaxPlayers, BrowserNetMatch.Propose(7, 99).Slots);

        static Size Size(MatchConfig config) => new(config.MapWidth, config.MapHeight);
    }

    /// <summary>The relay's side of a short exchange, by hand: what the browser sends back to each message.</summary>
    [Fact]
    public void Lobby_SaysHelloThenReadyAndStepsOnlyFromBundles()
    {
        var config = NetMatch.ProposeConfig(42, new Size(480, 300), LevelGenMode.Deterministic, 3);
        var match = new BrowserNetMatch("room", "Ann", "secret", config);
        var hello = Assert.IsType<Hello>(Single(match.TakeOutgoing()));
        Assert.Equal(("room", "Ann", "secret", NetMatch.BuildId), (hello.Room, hello.Name, hello.Secret, hello.BuildId));
        Assert.Null(match.Session);
        Assert.Equal("Connecting to the relay...", match.Status().Notice);

        Feed(match, new Welcome(1, config), new Roster(["Bob", "Ann", ""]));
        Assert.Equal(1, match.Session!.LocalSlot);
        var ready = Assert.IsType<Ready>(Single(match.TakeOutgoing()));
        Assert.Equal(WorldStateHash.ComputeReady(match.Session.World), ready.WorldHash);
        var lobby = match.Status();
        Assert.False(lobby.Started);
        Assert.False(lobby.IsHost);
        Assert.Equal("Waiting for Bob to start the match.", lobby.LobbyHint);
        Assert.Equal("Bob,Ann,", string.Join(',', lobby.Roster.Select(s => s.Name)));
        Assert.True(lobby.Roster[1].You);

        // No bundle, no step, whatever the clock says.
        Assert.Equal(0, match.Advance(default, dueByClock: 1, Now));
        Assert.Equal(0, match.Session.World.AdvanceCount);

        Feed(match, new Start(0b11), Tick(0, [0u, 0u, 0u], 3), Tick(1, [0u, 0u, 0u], 7));
        match.TakeOutgoing();
        var press = new ControllerOutput { BuildPrimary = true };
        Assert.Equal(2, match.Advance(press, dueByClock: 1, Now));
        Assert.Equal(2, match.Session.World.AdvanceCount);
        Assert.Equal(0b111, match.Net!.HumanMask);
        Assert.Null(match.Net.Bots[2]);
        // A press goes out with one tick, and the next tick releases it, so Core sees one press.
        var inputs = match.TakeOutgoing().Select(b => MessageCodec.Decode(b)).OfType<Input>()
            .Select(i => ControllerOutputCodec.Decode(i.Encoded)).ToArray();
        Assert.Equal("True,False", string.Join(',', inputs.Select(i => i.BuildPrimary)));
    }

    [Fact]
    public void Press_WaitsForAStepWhenAFrameHasNone()
    {
        var config = NetMatch.ProposeConfig(42, new Size(320, 200), LevelGenMode.Deterministic, 2);
        var match = new BrowserNetMatch("room", "Ann", "", config);
        Feed(match, new Welcome(0, config), new Roster(["Ann", ""]), new Start(0b01));
        match.TakeOutgoing();

        Assert.Equal(0, match.Advance(new ControllerOutput { Upgrade = 2 }, 1, Now));
        Feed(match, Tick(0, [0u, 0u], 1), Tick(1, [0u, 0u], 1), Tick(2, [0u, 0u], 1));
        Assert.Equal(3, match.Advance(default, 1, Now));

        var upgrades = match.TakeOutgoing().Select(b => MessageCodec.Decode(b)).OfType<Input>()
            .Select(i => ControllerOutputCodec.Decode(i.Encoded).Upgrade).ToArray();
        Assert.Equal("2,0,0", string.Join(',', upgrades));
        // The slot nobody took is a bot's.
        Assert.Equal("Ann,Bot", string.Join(',', match.Status().Roster.Select(s => s.Name)));
    }

    [Fact]
    public void Refusal_BeforeTheWelcome_IsTheFailure()
    {
        var match = new BrowserNetMatch("room", "Ann", "", BrowserNetMatch.Propose(1, 2));
        Feed(match, new Refusal("The relay wants a secret."));
        Assert.Equal("The relay wants a secret.", match.Failure);
        Assert.Null(match.Session);
        Assert.True(match.Status().Bad);
    }

    [Fact]
    public void LostConnection_AfterTheStart_TellsHowToComeBack()
    {
        var config = NetMatch.ProposeConfig(42, new Size(320, 200), LevelGenMode.Deterministic, 2);
        var match = new BrowserNetMatch("room", "Ann", "", config);
        Feed(match, new Welcome(0, config), new Roster(["Ann", "Bob"]), new Start(0b11), Tick(0, [0u, 0u], 3));
        match.Advance(default, 1, Now);
        match.Lose("lost the connection to the relay.");

        var status = match.Status();
        Assert.True(status.Bad);
        Assert.StartsWith("Disconnected: lost the connection to the relay. Join again", status.Notice, StringComparison.Ordinal);
        Feed(match, Tick(1, [0u, 0u], 3));
        Assert.Equal(0, match.Advance(default, 1, Now));
    }

    private static Tick Tick(int tick, System.Collections.Immutable.ImmutableArray<uint> inputs, int mask) =>
        new(new TickBundle(tick, inputs, mask));

    private static void Feed(BrowserNetMatch match, params Message[] messages)
    {
        foreach (var message in messages)
            match.Receive(MessageCodec.Encode(message), Now);
    }

    private static Message Single(IReadOnlyList<byte[]> sent) => MessageCodec.Decode(Assert.Single(sent));

    /// <summary>A browser and a desktop client in one room of the real relay, over real sockets.</summary>
    [Collection(TimedTestGroup.Name)]
    [Trait("Category", "Slow")]
    public class CrossPlayTests
    {
        [Fact]
        public async Task BrowserAndDesktop_PlayOneMatchToTheSameWorld()
        {
            var app = RelayApp.Build(["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default=Warning"]);
            await app.StartAsync();
            await using var _ = app;
            var play = new Uri($"ws://127.0.0.1:{new Uri(app.Urls.Single()).Port}/play");
            const int Ticks = 120;

            // The browser opens the room, so the desktop builds the browser's proposal.
            var proposal = BrowserNetMatch.Propose(4242, 2, new Size(320, 200)) with
            {
                TicksPerSecond = MessageCodec.MaxTicksPerSecond,
            };
            var browser = new BrowserNetMatch("cross", "Ann", "", proposal);
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(play, CancellationToken.None);
            var inbox = new ConcurrentQueue<byte[]>();
            using var closing = new CancellationTokenSource();
            var reader = Task.Run(async () =>
            {
                byte[] buffer = new byte[MessageCodec.MaxMessageBytes];
                while (await MessageFraming.ReadFrameAsync(socket, buffer, closing.Token) is { } frame)
                    inbox.Enqueue(frame.ToArray());
            });
            var clock = Stopwatch.StartNew();
            async Task PumpBrowser()
            {
                while (inbox.TryDequeue(out var frame))
                    browser.Receive(frame, clock.Elapsed);
                foreach (var bytes in browser.TakeOutgoing())
                    await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None);
            }

            var limit = Stopwatch.StartNew();
            while (browser.Session == null)
            {
                await PumpBrowser();
                Assert.True(limit.Elapsed < TimeSpan.FromSeconds(10), "The relay did not let the browser in.");
                await Task.Delay(5);
            }

            using var desktop = NetLaunch.Join(new NetLaunchOptions(play, "cross", "Bob", Players: 2),
                NetMatch.ProposeConfig(1, new Size(480, 300), LevelGenMode.Deterministic, 2));
            var desktopWorld = WorldFactory.Build(new WorldLaunchOptions(false,
                new Size(desktop.Config.MapWidth, desktop.Config.MapHeight), (LevelGenMode)desktop.Config.GenMode,
                desktop.Config.Seed, Players: desktop.Config.Slots, CoreTapWins: true)).World;
            var desktopSession = new NetSession(desktop, desktopWorld);
            desktop.SendReady(WorldStateHash.ComputeReady(desktopWorld));

            while (browser.Room!.PlayersHere < 2 || !browser.Room.IsHost)
            {
                await PumpBrowser();
                Assert.True(limit.Elapsed < TimeSpan.FromSeconds(20), "The desktop never appeared in the lobby.");
                await Task.Delay(5);
            }
            browser.RequestStart();

            var browserWorld = browser.Session.World;
            // A browser frame may step several ticks (more when catching up), so the browser can pass Ticks; the
            // desktop then steps to where the browser stopped, and the worlds are compared at the same tick.
            while (browserWorld.AdvanceCount < Ticks || desktopWorld.AdvanceCount < browserWorld.AdvanceCount)
            {
                await PumpBrowser();
                int tick = browserWorld.AdvanceCount;
                if (tick < Ticks)
                    browser.Advance(new ControllerOutput
                    {
                        MoveSpeed = new Offset(1, tick / 12 % 2),
                        ShootPrimary = tick % 5 == 0,
                        AimDirection = DirectionF.FromAngle(tick * 0.05f),
                        BuildPrimary = tick == 30,
                    }, dueByClock: 1, clock.Elapsed);

                desktop.Pump();
                desktopSession.Receive();
                if (desktop.Started && desktopWorld.AdvanceCount == 0 && desktopSession.HumanMask != desktop.HumanMask)
                    desktopSession.StartSeats(desktop.Config.Seed, desktop.HumanMask);
                while (desktopWorld.AdvanceCount < Math.Max(Ticks, browserWorld.AdvanceCount) && desktopSession.TryStep())
                    desktopSession.SendInput(new ControllerOutput
                    {
                        MoveSpeed = new Offset(-1, desktopWorld.AdvanceCount / 9 % 2),
                        ShootPrimary = true,
                    });
                Assert.Null(browser.Failure);
                Assert.Null(desktop.Failure);
                Assert.True(limit.Elapsed < TimeSpan.FromSeconds(60), "The match did not reach its last tick.");
                await Task.Delay(1);
            }

            Assert.Equal(desktop.Config, browser.Room.Config);
            Assert.Equal(browserWorld.AdvanceCount, desktopWorld.AdvanceCount);
            Assert.Equal(WorldStateHash.ComputeSync(desktopWorld), WorldStateHash.ComputeSync(browserWorld));
            // Both players moved: the tanks are not where an idle match leaves them.
            var idle = BrowserSession.ForNetwork(proposal, 0).World;
            for (int slot = 0; slot < 2; slot++)
                Assert.NotEqual(idle.TankList.Tanks[slot].Position, browserWorld.TankList.Tanks[slot].Position);
            Assert.Null(browser.Room.DesyncTick);
            Assert.Null(desktop.DesyncTick);

            closing.Cancel();
            socket.Abort();
            try
            {
                await reader;
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException)
            {
                // The socket was closed under the reader.
            }
        }
    }
}
