namespace Tunnerer.Tests;

using Tunnerer.Protocol;
using Tunnerer.Relay;

public class MatchRoomTests
{
    private const string Build = "build-1";
    private static readonly MatchConfig Config = new(
        Seed: 7, MapWidth: 320, MapHeight: 200, GenMode: 0, Slots: 2, TicksPerSecond: 24, InputKeyMask: 0xFFF);

    private sealed class FakePeer : IPeer
    {
        public List<Message> Received { get; } = [];

        public void Send(Message message) => Received.Add(message);

        public IEnumerable<T> Of<T>() => Received.OfType<T>();
    }

    private static (MatchRoom Room, FakePeer A, FakePeer B) RunningRoom()
    {
        var room = new MatchRoom("r", Build, Config);
        var a = new FakePeer();
        var b = new FakePeer();
        room.Join(a, "Ann", Build, out _);
        room.Join(b, "Bob", Build, out _);
        room.Handle(0, new Ready(99));
        room.Handle(1, new Ready(99));
        return (room, a, b);
    }

    [Fact]
    public void Joining_GivesASlotTheConfigAndTheRoster()
    {
        var room = new MatchRoom("r", Build, Config);
        var a = new FakePeer();
        var b = new FakePeer();

        Assert.Equal(0, room.Join(a, "Ann", Build, out _));
        Assert.Equal(1, room.Join(b, "", Build, out _));

        Assert.Equal(new Welcome(1, Config), b.Received[0]);
        Assert.Equal(new Roster(["Ann", "Player 2"]), a.Received[^1]);
    }

    [Fact]
    public void AnotherBuild_AFullRoom_AndARunningMatch_AreRefused()
    {
        var room = new MatchRoom("r", Build, Config);
        Assert.Equal(-1, room.Join(new FakePeer(), "Eve", "build-2", out string otherBuild));
        Assert.Contains("build-2", otherBuild, StringComparison.Ordinal);

        room.Join(new FakePeer(), "Ann", Build, out _);
        room.Join(new FakePeer(), "Bob", Build, out _);
        Assert.Equal(-1, room.Join(new FakePeer(), "Cat", Build, out string full));
        Assert.Contains("full", full, StringComparison.Ordinal);

        var (running, _, _) = RunningRoom();
        running.Leave(1);
        Assert.Equal(-1, running.Join(new FakePeer(), "Dan", Build, out string started));
        Assert.Contains("started", started, StringComparison.Ordinal);
    }

    [Fact]
    public void Match_StartsOnlyWhenEverySlotIsReady()
    {
        var room = new MatchRoom("r", Build, Config);
        var a = new FakePeer();
        room.Join(a, "Ann", Build, out _);
        room.Handle(0, new Ready(99));
        Assert.False(room.IsRunning);

        room.Join(new FakePeer(), "Bob", Build, out _);
        room.Handle(1, new Ready(99));

        Assert.True(room.IsRunning);
        Assert.Equal(new Start(HumanMask: 0b11), Assert.Single(a.Of<Start>()));
    }

    private static readonly MatchConfig FourSlots = Config with { Slots = 4 };

    [Fact]
    public void Host_CanStartWithThePlayersWhoAreThere_AndBotsGetTheRest()
    {
        var room = new MatchRoom("r", Build, FourSlots);
        var a = new FakePeer();
        var b = new FakePeer();
        room.Join(a, "Ann", Build, out _);
        room.Join(b, "Bob", Build, out _);
        room.Handle(0, new Ready(99));
        room.Handle(1, new Ready(99));
        Assert.False(room.IsRunning);

        // Only the host, the lowest slot, may start early.
        room.Handle(1, new StartRequest());
        Assert.False(room.IsRunning);
        room.Handle(0, new StartRequest());

        Assert.True(room.IsRunning);
        Assert.Equal(new Start(HumanMask: 0b0011), Assert.Single(b.Of<Start>()));
        room.CloseTicksUpTo(1);
        Assert.Equal(4, a.Of<Tick>().Single().Bundle.Inputs.Length);
    }

    [Fact]
    public void EarlyStart_WaitsForAPlayerWhoIsStillBuildingTheWorld()
    {
        var room = new MatchRoom("r", Build, FourSlots);
        var a = new FakePeer();
        room.Join(a, "Ann", Build, out _);
        room.Join(new FakePeer(), "Bob", Build, out _);
        room.Handle(0, new Ready(99));
        room.Handle(0, new StartRequest());
        Assert.False(room.IsRunning);

        room.Handle(1, new Ready(99));

        Assert.True(room.IsRunning);
    }

    [Fact]
    public void WhenTheHostLeaves_TheNextPlayerIsHostAndDecidesAgain()
    {
        var room = new MatchRoom("r", Build, FourSlots);
        room.Join(new FakePeer(), "Ann", Build, out _);
        var b = new FakePeer();
        room.Join(b, "Bob", Build, out _);
        room.Handle(0, new StartRequest());
        room.Handle(1, new Ready(99));

        room.Leave(0);
        Assert.False(room.IsRunning);
        room.Handle(1, new StartRequest());

        Assert.True(room.IsRunning);
        Assert.Equal(new Start(HumanMask: 0b0010), Assert.Single(b.Of<Start>()));
    }

    [Fact]
    public void DifferentWorldsBeforeTheFirstTick_StopTheStart()
    {
        var room = new MatchRoom("r", Build, Config);
        var a = new FakePeer();
        room.Join(a, "Ann", Build, out _);
        room.Join(new FakePeer(), "Bob", Build, out _);

        room.Handle(0, new Ready(1));
        room.Handle(1, new Ready(2));

        Assert.False(room.IsRunning);
        Assert.Single(a.Of<Refusal>());
        Assert.Empty(a.Of<Start>());
    }

    [Fact]
    public void Ticks_CarryEachSlotsInput_AndALeaverIdles()
    {
        var (room, a, _) = RunningRoom();
        room.Handle(0, new Input(5));
        room.Handle(1, new Input(6));
        room.CloseTicksUpTo(1);
        room.Leave(1);
        room.CloseTicksUpTo(3);

        var ticks = a.Of<Tick>().Select(t => t.Bundle).ToArray();
        Assert.Equal([0, 1, 2], ticks.Select(t => t.Tick));
        Assert.Equal([5u, 6u], ticks[0].Inputs.ToArray());
        Assert.Equal([5u, LockstepRoom.NeutralInput], ticks[2].Inputs.ToArray());
        Assert.Equal(new Roster(["Ann", ""]), a.Of<Roster>().Last());
    }

    [Fact]
    public void NoTicksBeforeTheStart()
    {
        var room = new MatchRoom("r", Build, Config);
        var a = new FakePeer();
        room.Join(a, "Ann", Build, out _);

        room.CloseTicksUpTo(5);

        Assert.Empty(a.Of<Tick>());
    }

    [Fact]
    public void ADesync_IsAnnouncedOnce()
    {
        var (room, a, _) = RunningRoom();

        room.Handle(0, new Hash(24, 1));
        room.Handle(1, new Hash(24, 2));
        room.Handle(0, new Hash(48, 3));
        room.Handle(1, new Hash(48, 4));

        Assert.Equal(new Desync(24), Assert.Single(a.Of<Desync>()));
    }

    [Fact]
    public void RoomIsEmpty_OnceEveryoneHasLeft()
    {
        var (room, _, _) = RunningRoom();
        Assert.False(room.IsEmpty);

        room.Leave(0);
        room.Leave(1);

        Assert.True(room.IsEmpty);
    }

    [Fact]
    public void PlayerWhoDroppedOut_ComesBackUnderTheirName_WithEveryTickSoFar()
    {
        var (room, a, _) = RunningRoom();
        room.Handle(1, new Input(6));
        room.CloseTicksUpTo(3);
        room.Leave(1);
        room.CloseTicksUpTo(150);

        var back = new FakePeer();
        Assert.Equal(1, room.Join(back, "bob", Build, out _));
        room.Handle(1, new Input(9));
        room.CloseTicksUpTo(151);

        Assert.Equal(new Welcome(1, Config), back.Received[0]);
        Assert.Equal(new Rejoined(HumanMask: 0b11), back.Received[1]);
        var logs = back.Of<Log>().ToArray();
        Assert.Equal([0, 64, 128], logs.Select(l => l.FirstTick));
        Assert.Equal(150 * Config.Slots, logs.Sum(l => l.Inputs.Length));
        Assert.Equal(6u, logs[0].Inputs[1]);
        // Then the live ticks, with the returning player's input counting again.
        Assert.Equal(9u, back.Of<Tick>().Single().Bundle.Inputs[1]);
        Assert.Equal(new Roster(["Ann", "Bob"]), a.Of<Roster>().Last());
    }

    [Fact]
    public void RunningMatch_TakesNobodyNew_AndNobodyIntoATakenSlot()
    {
        var (room, _, _) = RunningRoom();

        Assert.Equal(-1, room.Join(new FakePeer(), "Cat", Build, out string stranger));
        Assert.Contains("started", stranger, StringComparison.Ordinal);
        // Bob is still connected: his name does not open his slot.
        Assert.Equal(-1, room.Join(new FakePeer(), "Bob", Build, out _));
    }

    [Fact]
    public void Pause_HoldsTheTicksForEveryone_UntilSomeoneResumes()
    {
        var (room, a, b) = RunningRoom();
        room.CloseTicksUpTo(2);

        room.Handle(1, new SetPaused(true));
        room.CloseTicksUpTo(10);

        Assert.True(room.IsPaused);
        Assert.Equal(2, room.TicksClosed);
        Assert.Equal(new PauseState(true, BySlot: 1), a.Of<PauseState>().Single());

        room.Handle(0, new SetPaused(false));
        room.CloseTicksUpTo(3);

        Assert.Equal(3, room.TicksClosed);
        Assert.Equal(new PauseState(false, BySlot: 0), b.Of<PauseState>().Last());
    }

    [Fact]
    public void Ping_IsAnsweredToTheSenderOnly()
    {
        var (room, a, b) = RunningRoom();

        room.Handle(0, new Ping(5));

        Assert.Equal(new Pong(5), a.Of<Pong>().Single());
        Assert.Empty(b.Of<Pong>());
    }
}
