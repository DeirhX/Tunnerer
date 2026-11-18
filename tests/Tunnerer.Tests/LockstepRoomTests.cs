namespace Tunnerer.Tests;

using Tunnerer.Protocol;

public class LockstepRoomTests
{
    [Fact]
    public void QueuedInputs_ComeOutOnePerTickInOrder()
    {
        var room = new LockstepRoom(slots: 2);
        room.Submit(0, 11);
        room.Submit(0, 12);
        room.Submit(1, 21);

        Assert.Equal([11u, 21u], room.CloseTick().Inputs.ToArray());
        Assert.Equal([12u, 21u], room.CloseTick().Inputs.ToArray());
    }

    [Fact]
    public void SlotWithNothingQueued_RepeatsItsLastInput()
    {
        var room = new LockstepRoom(slots: 1);
        Assert.Equal([LockstepRoom.NeutralInput], room.CloseTick().Inputs.ToArray());

        room.Submit(0, 7);
        room.CloseTick();

        Assert.Equal([7u], room.CloseTick().Inputs.ToArray());
    }

    [Fact]
    public void SilentSlot_StopsRepeatingAfterTheLimit_AndPlaysAgainWhenItSends()
    {
        var room = new LockstepRoom(slots: 2, silentTicks: 3);
        room.Submit(0, 7);
        room.Submit(1, 9);
        room.CloseTick();

        // Slot 1 keeps sending; slot 0 has gone quiet with its key down.
        var played = new List<uint>();
        for (int tick = 0; tick < 5; tick++)
        {
            room.Submit(1, 9);
            var bundle = room.CloseTick();
            played.Add(bundle.Inputs[0]);
            Assert.Equal(9u, bundle.Inputs[1]);
        }
        Assert.Equal([7u, 7u, LockstepRoom.NeutralInput, LockstepRoom.NeutralInput, LockstepRoom.NeutralInput], played);

        room.Submit(0, 5);
        Assert.Equal(5u, room.CloseTick().Inputs[0]);
        Assert.Equal(5u, room.CloseTick().Inputs[0]);
    }

    [Fact]
    public void FullQueue_DropsTheOldestAndCountsIt()
    {
        var room = new LockstepRoom(slots: 1);
        for (uint i = 1; i <= LockstepRoom.MaxQueuedInputs + 2; i++)
            room.Submit(0, i);

        Assert.Equal(2, room.DroppedInputs(0));
        Assert.Equal([3u], room.CloseTick().Inputs.ToArray());
    }

    [Fact]
    public void HashForATickLongPast_IsStillComparedWithTheFirstReport()
    {
        var room = new LockstepRoom(slots: 2);
        for (int i = 0; i < 5000; i++)
            room.CloseTick();
        room.SubmitHash(24, 5);

        // A client replaying the match to catch up reports a tick the others passed minutes ago.
        room.SubmitHash(24, 5);
        Assert.Null(room.DesyncTick);
        room.SubmitHash(24, 6);

        Assert.Equal(24, room.DesyncTick);
    }

    [Fact]
    public void HashForATickNotClosedYet_IsIgnored()
    {
        var room = new LockstepRoom(slots: 2);
        for (int i = 0; i < 10; i++)
            room.CloseTick();

        room.SubmitHash(11, 1);
        room.SubmitHash(11, 2);
        room.SubmitHash(-1, 1);
        room.SubmitHash(-1, 2);
        Assert.Null(room.DesyncTick);

        // The tick that is next to close has been simulated by every client that is up to date.
        room.SubmitHash(10, 1);
        room.SubmitHash(10, 2);
        Assert.Equal(10, room.DesyncTick);
    }

    [Fact]
    public void FullQueue_KeepsABurstsKeyPressAndRelease()
    {
        var room = new LockstepRoom(slots: 1, keyMask: 0xF);
        // Released, aim moving, then a press, then a release, then aim moving: more than the queue holds.
        foreach (uint input in new uint[] { 0x10, 0x20, 0x32, 0x40, 0x50, 0x60 })
            room.Submit(0, input);

        Assert.Equal(LockstepRoom.MaxQueuedInputs, room.QueuedInputs(0));
        Assert.Equal(0, room.DroppedInputs(0));
        var keys = Enumerable.Range(0, 3).Select(_ => room.CloseTick().Inputs[0] & 0xFu).ToArray();
        Assert.Equal([0x0u, 0x2u, 0x0u], keys);
    }

    // Bits 0-3 stand for keys, the rest for the aim.
    private const uint Keys = 0xF;

    [Fact]
    public void Backlog_OfInputsThatOnlyAimDifferently_IsGoneInOneTick()
    {
        var room = new LockstepRoom(slots: 1, keyMask: Keys);
        foreach (uint aim in new uint[] { 0x10, 0x20, 0x30, 0x40 })
            room.Submit(0, 0x1 | aim);

        Assert.Equal([0x41u], room.CloseTick().Inputs.ToArray());
        Assert.Equal(0, room.QueuedInputs(0));
    }

    [Fact]
    public void Backlog_KeepsEveryKeyPressAndRelease()
    {
        var room = new LockstepRoom(slots: 1, keyMask: Keys);
        foreach (uint input in new uint[] { 0x0, 0x2, 0x0, 0x0 })
            room.Submit(0, input);

        var played = Enumerable.Range(0, 3).Select(_ => room.CloseTick().Inputs[0]).ToArray();

        Assert.Equal([0x0u, 0x2u, 0x0u], played);
        Assert.Equal(0, room.QueuedInputs(0));
    }

    /// <summary>After a burst, a client sending one input a tick must not be left playing late for good.</summary>
    [Fact]
    public void AfterABurst_ASteadyClientIsBackToNoBacklog()
    {
        var room = new LockstepRoom(slots: 1, keyMask: Keys);
        for (uint i = 0; i < 4; i++)
            room.Submit(0, 0x1 | i << 4);

        for (uint tick = 0; tick < 5; tick++)
        {
            room.CloseTick();
            room.Submit(0, 0x1 | (10 + tick) << 4);
        }

        Assert.Equal(1, room.QueuedInputs(0));
        Assert.Equal(0, room.DroppedInputs(0));
    }

    [Fact]
    public void DisconnectedSlot_PlaysNeutralUntilItReconnects()
    {
        var room = new LockstepRoom(slots: 2);
        room.Submit(0, 5);
        room.Submit(0, 6);
        room.CloseTick();

        room.Disconnect(0);
        room.Submit(0, 9);
        Assert.Equal(LockstepRoom.NeutralInput, room.CloseTick().Inputs[0]);

        room.Reconnect(0);
        room.Submit(0, 9);
        Assert.Equal(9u, room.CloseTick().Inputs[0]);
    }

    [Fact]
    public void Ticks_AreNumberedWithoutGapsAndLogged()
    {
        var room = new LockstepRoom(slots: 1);
        for (int i = 0; i < 3; i++)
            Assert.Equal(i, room.CloseTick().Tick);

        Assert.Equal(3, room.NextTick);
        Assert.Equal([0, 1, 2], room.Log.Select(b => b.Tick));
    }

    [Fact]
    public void DifferentHashesForOneTick_AreADesync()
    {
        var room = new LockstepRoom(slots: 3);
        for (int i = 0; i < 100; i++)
            room.CloseTick();
        room.SubmitHash(24, 100);
        room.SubmitHash(24, 100);
        room.SubmitHash(48, 200);
        Assert.Null(room.DesyncTick);

        room.SubmitHash(48, 201);
        room.SubmitHash(72, 300);
        room.SubmitHash(72, 301);

        Assert.Equal(48, room.DesyncTick);
    }

    [Fact]
    public void EveryBundle_CarriesTheHumanMask_AndASeatChangesFromTheNextTick()
    {
        var room = new LockstepRoom(slots: 3);
        Assert.Equal(0b111, room.CloseTick().HumanMask);
        room.SeatBot(2);
        room.Submit(2, 9);
        Assert.Equal(0b011, room.CloseTick().HumanMask);
        Assert.True(room.IsBotSeat(2));
        Assert.False(room.Seat(1)); // A player has it.

        Assert.True(room.Seat(2));
        Assert.False(room.Seat(2)); // Taken: a second joiner asking for it is refused.
        TickBundle first = room.CloseTick();
        room.Submit(2, 7);
        TickBundle second = room.CloseTick();

        Assert.Equal(0b111, first.HumanMask);
        // The bot's time left nothing behind: the slot plays the neutral input until the joiner's first arrives.
        Assert.Equal(LockstepRoom.NeutralInput, first.Inputs[2]);
        Assert.Equal(7u, second.Inputs[2]);
    }

    [Fact]
    public void SnapshotDigestsThatDiffer_AreADesync_AtTheirTick()
    {
        var room = new LockstepRoom(slots: 2);
        for (int i = 0; i < 50; i++)
            room.CloseTick();
        room.SubmitSnapshotDigest(50, 0xAB);
        room.SubmitSnapshotDigest(50, 0xAB);
        // A hash and a snapshot digest of one tick are not compared with each other.
        room.SubmitHash(50, 0xCD);
        Assert.Null(room.DesyncTick);

        room.SubmitSnapshotDigest(50, 0xAC);

        Assert.Equal(50, room.DesyncTick);
    }
}

public class SnapshotHoldTests
{
    private static SnapshotChunk Chunk(int tick, int index, int length) => new(tick, index, [.. new byte[length]]);

    [Fact]
    public void Upload_IsOfferedOnceEveryUploaderHasFinished_FromTheFirstToFinish()
    {
        var hold = new SnapshotHold(tick: 100, uploaders: [0, 1], maxBytes: 1000);
        Assert.True(hold.AddChunk(1, Chunk(100, 0, 300)));
        Assert.True(hold.AddChunk(1, Chunk(100, 1, 200)));
        Assert.True(hold.AddChunk(0, Chunk(100, 0, 500)));
        Assert.Null(hold.Offer);

        Assert.True(hold.Finish(1, new SnapshotDone(100, 500, Digest: 7, SyncHash: 8)));
        Assert.False(hold.IsComplete);
        Assert.True(hold.Finish(0, new SnapshotDone(100, 500, Digest: 7, SyncHash: 8)));

        Assert.True(hold.IsComplete);
        Assert.Equal(new SnapshotOffer(100, 500, 7, 8), hold.Offer);
        Assert.Equal([0, 1], hold.Chunks.Select(c => c.Index));
    }

    [Fact]
    public void ChunksAndEnds_ThatDoNotFit_AreRefused()
    {
        var hold = new SnapshotHold(tick: 100, uploaders: [0], maxBytes: 1000);
        Assert.False(hold.AddChunk(1, Chunk(100, 0, 10))); // Not asked.
        Assert.False(hold.AddChunk(0, Chunk(99, 0, 10))); // Another tick.
        Assert.False(hold.AddChunk(0, Chunk(100, 1, 10))); // Out of order.
        Assert.False(hold.AddChunk(0, Chunk(100, 0, 1001))); // Too big.
        Assert.True(hold.AddChunk(0, Chunk(100, 0, 10)));
        Assert.False(hold.Finish(0, new SnapshotDone(100, 11, 0, 0))); // Not what arrived.
        Assert.False(hold.Finish(0, new SnapshotDone(101, 10, 0, 0)));
        Assert.True(hold.Finish(0, new SnapshotDone(100, 10, 0, 0)));
        Assert.False(hold.AddChunk(0, Chunk(100, 1, 10))); // After the end.
        Assert.Throws<ArgumentException>(() => new SnapshotHold(1, [0, 1, 2], 10));
    }

    [Fact]
    public void Chunks_SplitAndPutBackTogether_GiveTheSameBytes()
    {
        byte[] bytes = [.. Enumerable.Range(0, SnapshotChunk.MaxBytes * 2 + 17).Select(i => (byte)(i * 7))];
        var chunks = SnapshotChunk.Split(5, bytes).ToList();
        var assembly = new SnapshotAssembly(5, bytes.Length);
        Assert.All(chunks, chunk => Assert.True(assembly.Add(chunk)));

        Assert.Equal(3, chunks.Count);
        Assert.Equal(bytes, assembly.ToArray());
    }
}

public class BundleBufferTests
{
    private static TickBundle Bundle(int tick) => new(tick, [0u], HumanMask: 1);

    private static BundleBuffer BufferWith(int count)
    {
        var buffer = new BundleBuffer();
        for (int i = 0; i < count; i++)
            buffer.Add(Bundle(i));
        return buffer;
    }

    [Fact]
    public void Bundles_ComeOutInTickOrder()
    {
        var buffer = BufferWith(2);

        Assert.True(buffer.TryTake(out var first));
        Assert.True(buffer.TryTake(out var second));
        Assert.False(buffer.TryTake(out _));
        Assert.Equal((0, 1), (first.Tick, second.Tick));
    }

    [Fact]
    public void BundleForTheWrongTick_IsRejected()
    {
        var buffer = BufferWith(1);

        Assert.Throws<InvalidDataException>(() => buffer.Add(Bundle(2)));
    }

    [Theory]
    [InlineData(0, 1, 0)] // nothing arrived: the step waits
    [InlineData(1, 1, 1)] // level: one due, one there
    [InlineData(2, 1, 2)] // one arrived late: both are simulated now, none is kept back
    [InlineData(3, 1, 3)] // behind: caught up in one clock step
    [InlineData(1, 0, 1)] // nothing due by the clock, but one there: simulated, nothing kept back
    [InlineData(3, 0, 3)] // far behind steps early
    [InlineData(40, 1, 4)] // a rejoin catches up at the cap
    public void StepsToTake_FollowsTheClockAndCatchesUp(int buffered, int dueByClock, int expected)
    {
        Assert.Equal(expected, BufferWith(buffered).StepsToTake(dueByClock, maxSteps: 4));
    }
}
