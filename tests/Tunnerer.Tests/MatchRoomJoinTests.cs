namespace Tunnerer.Tests;

using Tunnerer.Protocol;
using Tunnerer.Relay;

public class MatchRoomJoinTests
{
    private sealed class Peer : IPeer
    {
        public List<Message> Received { get; } = [];
        public bool Closed { get; private set; }
        public void Send(Message message) => Received.Add(message);
        public void Close() => Closed = true;
    }

    private static readonly MatchConfig Config = NetSessionJoinTests.Config;
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private static (MatchRoom Room, Peer[] Players, Peer Watcher) Ready(int count = 2)
    {
        var room = new MatchRoom("r", "b", Config);
        var players = Enumerable.Range(0, count).Select(_ => new Peer()).ToArray();
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i, room.Join(players[i], $"P{i}", "b", out _, SnapshotCapabilities.Upload));
            room.Handle(i, new Ready(1));
        }
        room.Handle(0, new StartRequest());
        room.AdvanceClock(Second);
        var watcher = new Peer();
        Assert.Equal(-1, room.Join(watcher, "Cat", "b", out string refusal, SnapshotCapabilities.Join));
        Assert.Empty(refusal);
        return (room, players, watcher);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TimeoutReleasesOnlyTheJoinHold(bool pauseDuringJoin)
    {
        var (room, players, watcher) = Ready();
        room.AdvanceClock(Second);
        int heldAt = room.TicksClosed;
        Assert.Equal(Config.TicksPerSecond * 2, heldAt);
        room.HandleWatcher(watcher, new ReadyToLoad());
        room.HandleWatcher(watcher, new TakeSeat(2));
        Assert.DoesNotContain(watcher.Received, m => m is Seated);
        room.AdvanceClock(Second);
        room.HandleWatcher(watcher, new ReadyToLoad()); // does not extend the deadline
        if (pauseDuringJoin)
            room.Handle(1, new SetPaused(true));
        room.AdvanceClock(MatchRoom.JoinHoldLimit - Second);
        Assert.True(watcher.Closed);
        Assert.Equal(heldAt, room.TicksClosed);
        Assert.Equal([new JoinHoldState("Cat"), new JoinHoldState("")], players[0].Received.OfType<JoinHoldState>());
        room.AdvanceClock(Second);
        Assert.Equal(heldAt + (pauseDuringJoin ? 0 : Config.TicksPerSecond), room.TicksClosed);
    }

    [Fact]
    public void DisconnectedUploaderIsReplacedAtTheSameTickWithinTheOriginalDeadline()
    {
        var (room, players, watcher) = Ready();
        int tick = room.TicksClosed;
        room.HandleWatcher(watcher, new ReadyToLoad());
        room.Handle(0, new SnapshotChunk(tick, 0, [1]));
        room.Leave(0);
        Assert.Equal(new SnapshotRequest(tick, true), players[1].Received.OfType<SnapshotRequest>().Last());
        room.Handle(1, new SnapshotChunk(tick, 0, [2]));
        room.Handle(1, new SnapshotDone(tick, 1, 2, 3));
        Assert.Equal(new SnapshotChunk(tick, 0, [2]), Assert.Single(watcher.Received.OfType<SnapshotChunk>()));
        Assert.Equal(2, room.HandleWatcher(watcher, new TakeSeat(2)));
        room.Leave(2);
        var back = new Peer();
        Assert.Equal(2, room.Join(back, "Cat", "b", out _));
        Assert.Contains(new Rejoined(3), back.Received); // replay starts with the original mask
    }

    [Fact]
    public void VerifierMismatchClosesTheWatcherAndReportsTheDesyncToPlayers()
    {
        var (room, players, watcher) = Ready();
        int tick = room.TicksClosed;
        room.HandleWatcher(watcher, new ReadyToLoad());
        room.Handle(0, new SnapshotChunk(tick, 0, [1]));
        room.Handle(0, new SnapshotDone(tick, 1, 2, 3));
        room.Handle(1, new SnapshotDone(tick, 0, 9, 3));
        Assert.True(watcher.Closed);
        Assert.DoesNotContain(watcher.Received, m => m is SnapshotOffer);
        Assert.All(players, p => Assert.Contains(new Desync(tick), p.Received));
    }

    [Fact]
    public void LosingTheOnlyUploaderFailsTheJoinAndTheNextPlayerCanRejoinByName()
    {
        var (room, _, watcher) = Ready(count: 1);
        room.HandleWatcher(watcher, new ReadyToLoad());
        room.Leave(0);
        Assert.True(watcher.Closed);
        Assert.Contains(watcher.Received, m => m is Refusal);
        int tick = room.TicksClosed;
        var back = new Peer();
        Assert.Equal(0, room.Join(back, "P0", "b", out _));
        room.AdvanceClock(Second);
        Assert.Equal(tick + Config.TicksPerSecond, room.TicksClosed);
    }

    [Fact]
    public void LosingVerifierPreservesPartialUploadAndRequestsTheRemainingPlayer()
    {
        var (room, players, watcher) = Ready(count: 3);
        int tick = room.TicksClosed;
        room.HandleWatcher(watcher, new ReadyToLoad());
        room.Handle(0, new SnapshotChunk(tick, 0, [1]));
        room.Leave(1);
        Assert.Equal(new SnapshotRequest(tick, false), Assert.Single(players[2].Received.OfType<SnapshotRequest>()));
        room.Handle(0, new SnapshotDone(tick, 1, 2, 3));
        Assert.DoesNotContain(watcher.Received, m => m is SnapshotOffer);
        room.Handle(2, new SnapshotDone(tick, 0, 2, 3));
        Assert.Contains(new SnapshotOffer(tick, 1, 2, 3), watcher.Received);
    }

    [Fact]
    public void WatcherDisconnectResumesTheClockAndFreesAdmission()
    {
        var (room, _, watcher) = Ready();
        room.HandleWatcher(watcher, new ReadyToLoad());
        room.LeaveWatcher(watcher);
        int tick = room.TicksClosed;
        room.AdvanceClock(Second);
        Assert.Equal(tick + Config.TicksPerSecond, room.TicksClosed);
        var next = new Peer();
        room.Join(next, "Cat", "b", out string refusal, SnapshotCapabilities.Join);
        Assert.Empty(refusal);
        room.LeaveWatcher(watcher); // the previous socket's delayed cleanup cannot remove its replacement
        room.HandleWatcher(next, new ReadyToLoad());
        room.AdvanceClock(Second);
        Assert.Equal(tick + Config.TicksPerSecond, room.TicksClosed);
    }
}
