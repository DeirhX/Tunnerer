namespace Emberward.Tests;

using Emberward.Protocol;
using Emberward.Relay;
using static Emberward.Tests.MatchRoomJoinTests;

public class MatchRoomClosedTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private static string TryJoin(MatchRoom room, out Peer newcomer)
    {
        newcomer = new Peer();
        room.Join(newcomer, "Dog", "b", out string refusal, SnapshotCapabilities.Join);
        return refusal;
    }

    [Fact]
    public void AClosedMatchRefusesNewcomersAndAnOpenedOneAdmitsThemAgain()
    {
        var (room, _, watcher) = Ready();
        room.LeaveWatcher(watcher);
        room.Handle(0, new SetClosedToNewcomers(true));
        Assert.Equal("This match is closed to newcomers.", TryJoin(room, out _));
        room.Handle(0, new SetClosedToNewcomers(false));
        Assert.Empty(TryJoin(room, out var admitted));
        Assert.Contains(admitted.Received, m => m is Welcome);
    }

    [Fact]
    public void ClosedIsSaidBeforeAFailedJoinsWait()
    {
        var (room, _, watcher) = Ready();
        room.HandleWatcher(watcher, new ReadyToLoad());
        room.AdvanceClock(MatchRoom.JoinHoldLimit);
        Assert.Contains("Try again", TryJoin(room, out _));
        room.Handle(0, new SetClosedToNewcomers(true));
        Assert.Equal("This match is closed to newcomers.", TryJoin(room, out _));
    }

    [Fact]
    public void OnlyTheHostCanChangeItAndOnlyWhileTheMatchRuns()
    {
        var (room, players, watcher) = Ready();
        room.LeaveWatcher(watcher);
        room.Handle(1, new SetClosedToNewcomers(true));
        Assert.Empty(TryJoin(room, out var admitted));
        room.LeaveWatcher(admitted);
        Assert.All(players, p => Assert.DoesNotContain(p.Received, m => m is NewcomersState));

        room.Leave(0); // the lowest occupied slot is now the host
        room.Handle(1, new SetClosedToNewcomers(true));
        Assert.NotEmpty(TryJoin(room, out _));

        var lobby = new MatchRoom("l", "b", NetSessionJoinTests.Config);
        var guest = new Peer();
        Assert.Equal(0, lobby.Join(guest, "A", "b", out _));
        lobby.Handle(0, new SetClosedToNewcomers(true));
        Assert.DoesNotContain(guest.Received, m => m is NewcomersState);
    }

    [Fact]
    public void ClosingRefusesAWatcherWhoIsPreparingAndTheClockRuns()
    {
        var (room, _, watcher) = Ready();
        room.Handle(0, new SetClosedToNewcomers(true));
        Assert.True(watcher.Closed);
        Assert.Contains(watcher.Received, m => m is Refusal { Reason: "This match is closed to newcomers." });
        int tick = room.TicksClosed;
        room.AdvanceClock(Second);
        Assert.True(room.TicksClosed > tick);
    }

    [Fact]
    public void ClosingDuringAHoldReleasesItAndTheClockRuns()
    {
        var (room, players, watcher) = Ready();
        room.HandleWatcher(watcher, new ReadyToLoad());
        int tick = room.TicksClosed;
        room.AdvanceClock(Second);
        Assert.Equal(tick, room.TicksClosed); // held
        room.Handle(0, new SetClosedToNewcomers(true));
        Assert.True(watcher.Closed);
        Assert.Equal(new JoinHoldState(""), players[1].Received.OfType<JoinHoldState>().Last());
        room.AdvanceClock(Second);
        Assert.True(room.TicksClosed > tick);
        room.Handle(0, new SetClosedToNewcomers(false));
        Assert.Empty(TryJoin(room, out _)); // closing it was the host's choice: no wait follows
    }

    [Fact]
    public void AReturningPlayerStillGetsTheirSeatAndIsToldTheMatchIsClosed()
    {
        var (room, _, watcher) = Ready();
        room.LeaveWatcher(watcher);
        room.Leave(1);
        room.Handle(0, new SetClosedToNewcomers(true));
        var back = new Peer();
        Assert.Equal(1, room.Join(back, "P1", "b", out string refusal));
        Assert.Empty(refusal);
        Assert.Contains(new NewcomersState(true), back.Received);
        Assert.NotEmpty(TryJoin(room, out _));
    }

    [Fact]
    public void EveryChangeIsBroadcastToTheSeatedPlayersAndNoRepeatIsSent()
    {
        var (room, players, _) = Ready();
        room.Handle(0, new SetClosedToNewcomers(true));
        room.Handle(0, new SetClosedToNewcomers(true));
        room.Handle(0, new SetClosedToNewcomers(false));
        foreach (var player in players)
            Assert.Equal([new NewcomersState(true), new NewcomersState(false)], player.Received.OfType<NewcomersState>());
    }
}
