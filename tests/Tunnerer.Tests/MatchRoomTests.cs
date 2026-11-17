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

        public bool IsClosed { get; private set; }

        public void Send(Message message) => Received.Add(message);

        public void Close() => IsClosed = true;

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
    public void DifferentWorldsBeforeTheFirstTick_CloseEveryConnection_SoTheRoomEmpties()
    {
        var room = new MatchRoom("r", Build, Config);
        var a = new FakePeer();
        var b = new FakePeer();
        room.Join(a, "Ann", Build, out _);
        room.Join(b, "Bob", Build, out _);

        room.Handle(0, new Ready(1));
        room.Handle(1, new Ready(2));

        Assert.True(a.IsClosed);
        Assert.True(b.IsClosed);
        // The hub's serve loops see the close and leave, which is what empties the room.
        room.Leave(0);
        room.Leave(1);
        Assert.True(room.IsEmpty);
        Assert.False(room.IsRunning);
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
        room.CloseTicksUpTo(60);

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

    [Fact]
    public void ARejoinerOnAPausedMatch_IsToldWhoPausedIt()
    {
        var (room, _, _) = RunningRoom();
        room.Handle(0, new SetPaused(true));
        room.Leave(1);

        var back = new FakePeer();
        room.Join(back, "Bob", Build, out _);

        Assert.Equal(new PauseState(true, BySlot: 0), back.Of<PauseState>().Single());
    }

    // Just over n ticks, so that rounding in the time cannot leave the last one a hair short.
    private static TimeSpan Ticks(double n) => TimeSpan.FromSeconds((n + 0.01) / 24);

    [Fact]
    public void Clock_ClosesTheTicksThatAreDue_AndMakesUpALateWakeUp()
    {
        var (room, a, _) = RunningRoom();

        room.AdvanceClock(Ticks(3.5));
        room.AdvanceClock(Ticks(2));

        Assert.Equal(5, room.TicksClosed);
        Assert.Equal([0, 1, 2, 3, 4], a.Of<Tick>().Select(t => t.Bundle.Tick));
    }

    [Fact]
    public void Clock_DoesNotCountPausedTime()
    {
        var (room, _, _) = RunningRoom();
        room.AdvanceClock(Ticks(2));

        room.Handle(0, new SetPaused(true));
        room.AdvanceClock(TimeSpan.FromMinutes(5));
        Assert.Equal(2, room.TicksClosed);

        room.Handle(1, new SetPaused(false));
        room.AdvanceClock(Ticks(3));

        // Two before the pause and three after: the minutes held did not become a burst of ticks.
        Assert.Equal(5, room.TicksClosed);
    }

    [Fact]
    public void Clock_WaitsInAnEmptyRoom_AndTheMatchPicksUpWhereItWas()
    {
        var (room, _, _) = RunningRoom();
        room.AdvanceClock(Ticks(10));
        room.Leave(0);
        room.Leave(1);

        room.AdvanceClock(TimeSpan.FromMinutes(2));
        Assert.Equal(10, room.TicksClosed);
        Assert.Equal(TimeSpan.FromMinutes(2), room.EmptyFor);

        var back = new FakePeer();
        Assert.Equal(1, room.Join(back, "Bob", Build, out _));
        Assert.Equal(TimeSpan.Zero, room.EmptyFor);
        room.AdvanceClock(Ticks(4));

        // No stall and no jump: the next tick is the one after the last, with the whole log sent first.
        Assert.Equal(14, room.TicksClosed);
        Assert.Equal(10, back.Of<Log>().Sum(l => l.Inputs.Length) / Config.Slots);
        Assert.Equal([10, 11, 12, 13], back.Of<Tick>().Select(t => t.Bundle.Tick));
        Assert.Contains(back.Received, m => m is Rejoined);
    }

    [Fact]
    public void Clock_OfARoomStillInTheLobby_CountsNothing()
    {
        var room = new MatchRoom("r", Build, Config);
        room.Join(new FakePeer(), "Ann", Build, out _);

        room.AdvanceClock(TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.Zero, room.EmptyFor);
        Assert.Equal(0, room.TicksClosed);
    }

    [Fact]
    public void PlayerWhoseClientGoesQuiet_IdlesAfterHalfASecond()
    {
        var (room, a, _) = RunningRoom();
        room.Handle(0, new Input(7));
        int silentTicks = (int)(Config.TicksPerSecond * MatchRoom.SilentSeconds);

        room.CloseTicksUpTo(silentTicks + 2);

        uint[] slot0 = [.. a.Of<Tick>().Select(t => t.Bundle.Inputs[0])];
        Assert.All(slot0[..silentTicks], input => Assert.Equal(7u, input));
        Assert.All(slot0[silentTicks..], input => Assert.Equal(LockstepRoom.NeutralInput, input));
    }

    [Fact]
    public void MatchOver_FinishesTheRoom_OnlyForATickThatWasPlayed()
    {
        var (room, _, _) = RunningRoom();
        room.CloseTicksUpTo(10);

        room.Handle(0, new MatchOver(11));
        room.Handle(0, new MatchOver(0));
        Assert.False(room.IsOver);

        room.Handle(1, new MatchOver(10));
        Assert.True(room.IsOver);
    }

    [Fact]
    public void MatchOver_BeforeTheStart_IsIgnored()
    {
        var room = new MatchRoom("r", Build, Config);
        room.Join(new FakePeer(), "Ann", Build, out _);

        room.Handle(0, new MatchOver(1));

        Assert.False(room.IsOver);
    }

    [Fact]
    public void FinishedMatch_PlaysOnForTheResultScreens_ThenItsClockStands()
    {
        var (room, _, _) = RunningRoom();
        room.AdvanceClock(Ticks(10));
        room.Handle(0, new MatchOver(10));
        int afterTheEnd = MatchRoom.SecondsPlayedAfterTheEnd * Config.TicksPerSecond;

        room.AdvanceClock(Ticks(afterTheEnd - 1));
        Assert.Equal(10 + afterTheEnd - 1, room.TicksClosed);

        room.AdvanceClock(Ticks(1));
        room.AdvanceClock(Ticks(500));
        Assert.Equal(10 + afterTheEnd, room.TicksClosed);
    }

    [Fact]
    public void PlayersWithTheSameName_GetANumber_SoEachCanComeBackAsThemself()
    {
        var room = new MatchRoom("r", Build, Config with { Slots = 3 });
        var a = new FakePeer();
        room.Join(a, "Ann", Build, out _);
        room.Join(new FakePeer(), "ann", Build, out _);
        room.Join(new FakePeer(), new string('x', MessageCodec.MaxTextLength), Build, out _);

        Assert.Equal(new Roster(["Ann", "ann (2)", new string('x', MessageCodec.MaxTextLength)]), a.Of<Roster>().Last());

        room.Leave(2);
        room.Join(new FakePeer(), "Ann", Build, out _);
        Assert.Equal("Ann (3)", a.Of<Roster>().Last().Names[2]);
    }
}
