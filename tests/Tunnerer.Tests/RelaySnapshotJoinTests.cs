namespace Tunnerer.Tests;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Xunit.Abstractions;
using Tunnerer.Core;
using Tunnerer.Core.Input;
using Tunnerer.Core.Snapshots;
using Tunnerer.Desktop.Net;
using Tunnerer.Protocol;

[Collection(TimedTestGroup.Name)]
[Trait("Category", "Slow")]
public class RelaySnapshotJoinTests(ITestOutputHelper output)
{
    // Pace only snapshot bytes on their way to the real socket. The other client can verify and the relay's
    // clock can run while the uploader is waiting for its next chunk.
    private sealed class PacedUpload(RelayConnection link) : ILockstepLink
    {
        public const int BytesPerSecond = 512 * 1024;
        private readonly Stopwatch _clock = new();
        private readonly Queue<Message> _pending = new();
        public int BytesSent { get; private set; }

        public void SendJoin(Message message)
        {
            if (message is SnapshotChunk or SnapshotDone { Length: > 0 })
            {
                _clock.Start();
                _pending.Enqueue(message);
            }
            else
                link.SendJoin(message);
        }

        public void Flush()
        {
            while (_pending.TryPeek(out var next))
            {
                if (next is SnapshotChunk chunk)
                {
                    if (BytesSent + chunk.Bytes.Length > _clock.Elapsed.TotalSeconds * BytesPerSecond)
                        return;
                    BytesSent += chunk.Bytes.Length;
                }
                link.SendJoin(_pending.Dequeue());
            }
        }

        public void SendInput(uint input) => link.SendInput(input);
        public void SendHash(int tick, ulong hash) => link.SendHash(tick, hash);
        public void SendMatchOver(int tick) => link.SendMatchOver(tick);
        public bool TryReceive([NotNullWhen(true)] out TickBundle? bundle) => link.TryReceive(out bundle);
        public bool TryReceiveJoin([NotNullWhen(true)] out Message? message) => link.TryReceiveJoin(out message);
    }

    [Fact]
    public async Task ClosingWatcherSocketReleasesHoldAndAcceptsAnotherJoiner()
    {
        var (app, address) = await RelayOverSocketsTests.StartRelayAsync();
        await using var _ = app;
        var config = NetSessionJoinTests.Config;
        var hello = new Hello(MessageCodec.ProtocolVersion, NetMatch.BuildId, "Ann", "cancel", config,
            Snapshots: SnapshotCapabilities.Upload);
        using var player = RelayConnection.Connect(address, hello, TimeSpan.FromSeconds(5));
        player.SendReady(123);
        player.RequestStart();
        RelayOverSocketsTests.WaitFor(() => player.Started, player);
        var newcomer = hello with { Name = "Cat", Snapshots = SnapshotCapabilities.Join };
        using (var watcher = RelayConnection.Connect(address, newcomer, TimeSpan.FromSeconds(5)))
        {
            watcher.SendJoin(new ReadyToLoad());
            RelayOverSocketsTests.WaitFor(() => player.IsHeld, player);
        }
        RelayOverSocketsTests.WaitFor(() => !player.IsHeld, player);
        using var replacement = RelayConnection.Connect(address, newcomer, TimeSpan.FromSeconds(5));
        Assert.Equal(-1, replacement.Slot);
        Assert.Null(player.Failure);
    }

    [Fact]
    public async Task WatcherTakesBotSeatOverWebSocketAndPlaysTheSameWorld()
    {
        var (app, address) = await RelayOverSocketsTests.StartRelayAsync();
        await using var _ = app;
        const int seed = 42;
        var config = NetSessionJoinTests.Config with { Seed = seed, TicksPerSecond = 60 };
        RelayConnection Connect(string name, SnapshotCapabilities capabilities) =>
            RelayConnection.Connect(address, new Hello(MessageCodec.ProtocolVersion, NetMatch.BuildId, name, "join",
                config, Snapshots: capabilities), TimeSpan.FromSeconds(5));

        using var a = Connect("Ann", SnapshotCapabilities.Upload);
        using var b = Connect("Bob", SnapshotCapabilities.Upload);
        var seated = new[] { a, b };
        var worlds = seated.Select(_ => MatchRecording.BuildWorld(config)).ToArray();
        var links = seated.Select(c => new PacedUpload(c)).ToArray();
        var sessions = links.Select((link, i) => new NetSession(link, worlds[i])).ToArray();
        for (int i = 0; i < seated.Length; i++)
            seated[i].SendReady(WorldStateHash.ComputeReady(worlds[i]));
        a.RequestStart();
        RelayOverSocketsTests.WaitFor(() => a.Started && b.Started, a, b);
        foreach (var session in sessions)
            session.StartSeats(seed, a.HumanMask);

        static void Play(NetSession session, World world, int limit)
        {
            session.Receive();
            while (world.AdvanceCount < limit && session.TryStep()) { }
        }
        void PlaySeated(int limit)
        {
            for (int i = 0; i < seated.Length; i++)
            {
                Play(sessions[i], worlds[i], limit);
                links[i].Flush();
            }
        }

        RelayOverSocketsTests.WaitFor(() =>
        {
            PlaySeated(12);
            return worlds.All(w => w.AdvanceCount == 12);
        }, a, b);
        using var watcher = Connect("Cat", SnapshotCapabilities.Join | SnapshotCapabilities.Upload);
        Assert.Equal(-1, watcher.Slot);
        var joiningWorld = MatchRecording.BuildWorld(watcher.Config);
        var join = new LateJoin(watcher, joiningWorld, seed);
        var duration = Stopwatch.StartNew();
        join.Begin();
        RelayOverSocketsTests.WaitFor(() =>
        {
            PlaySeated(int.MaxValue);
            join.Receive();
            Assert.Null(watcher.Failure);
            return join.Session != null;
        }, a, b, watcher);
        Assert.Equal((2, 2), (join.Slot, watcher.Slot));
        RelayOverSocketsTests.WaitFor(() =>
        {
            Play(join.Session!, joiningWorld, join.Session!.StartTick + 1);
            return joiningWorld.AdvanceCount > join.Session.StartTick;
        }, watcher);
        duration.Stop();
        int bytes = links.Sum(link => link.BytesSent);
        Assert.InRange(bytes, SnapshotChunk.MaxBytes + 1, MessageCodec.MaxSnapshotBytes);
        Assert.Equal(1, links.Count(link => link.BytesSent > 0));
        output.WriteLine($"Ready to first resumed tick: {duration.Elapsed.TotalSeconds:F3} s, {bytes} bytes at 512 KiB/s.");
        Assert.Equal("Cat", watcher.AssignedName);
        int target = Math.Max(worlds.Max(w => w.AdvanceCount), joiningWorld.AdvanceCount) + 24;
        join.Session!.SendInput(new ControllerOutput { ShootPrimary = true });
        RelayOverSocketsTests.WaitFor(() =>
        {
            PlaySeated(target);
            Play(join.Session, joiningWorld, target);
            return worlds.All(w => w.AdvanceCount == target) && joiningWorld.AdvanceCount == target;
        }, a, b, watcher);

        Assert.Equal(0b0111, join.Session.HumanMask);
        Assert.Contains(join.Session.Played, tick => tick.Inputs[2] != 0);
        byte[] expected = WorldSnapshot.Save(joiningWorld, join.Session.Bots);
        for (int i = 0; i < seated.Length; i++)
        {
            Assert.Equal(expected, WorldSnapshot.Save(worlds[i], sessions[i].Bots));
            Assert.Null(seated[i].DesyncTick);
        }
    }
}
