namespace Tunnerer.Tests;

using Tunnerer.Core;
using Tunnerer.Core.Input;
using Tunnerer.Core.LevelGen;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Net;
using Tunnerer.Protocol;

public class MatchRecordingTests
{
    private const int HumanMask = 0b01;
    private static readonly MatchConfig Config = NetMatch.ProposeConfig(
        seed: 5, new Size(320, 200), LevelGenMode.Optimized, players: 2);

    /// <summary>A short match of one player and one bot, played through a relay as the game would.</summary>
    private static (World World, NetSession Session) PlayMatch(int ticks)
    {
        var relay = new InMemoryRelay(Config.Slots, seed: 3, maxDelay: 1);
        relay.SeatBot(1);
        World world = MatchRecording.BuildWorld(Config);
        var session = new NetSession(relay.LinkFor(0), world);
        session.StartSeats(Config.Seed, HumanMask);
        while (world.AdvanceCount < ticks)
        {
            session.SendInput(new ControllerOutput
            {
                MoveSpeed = new Offset(1, world.AdvanceCount / 20 % 2),
                ShootPrimary = world.AdvanceCount % 5 == 0,
                AimDirection = DirectionF.FromAngle(world.AdvanceCount * 0.1f),
            });
            relay.Advance();
            session.Receive();
            while (world.AdvanceCount < ticks && session.TryStep())
            {
            }
        }
        return (world, session);
    }

    [Fact]
    public void SyncDigest_SeesADifferenceThatIsOnlyInTheHeatField()
    {
        var a = TestHelpers.CreateSeededWorld();
        var b = TestHelpers.CreateSeededWorld();
        Assert.Equal(WorldStateHash.ComputeSync(a), WorldStateHash.ComputeSync(b));

        b.Terrain.AddHeat(new Position(100, 100), 5);

        // The state hash, which the fingerprints use, does not read the heat field; the sync digest does.
        Assert.Equal(WorldStateHash.Compute(a), WorldStateHash.Compute(b));
        SyncDigest digestA = WorldStateHash.ComputeSync(a), digestB = WorldStateHash.ComputeSync(b);
        Assert.Equal(digestA.State, digestB.State);
        Assert.NotEqual(digestA.Heat, digestB.Heat);
        Assert.NotEqual(digestA.Combined, digestB.Combined);
    }

    [Fact]
    public void Recording_SavedAndLoaded_IsTheSameMatch()
    {
        var (_, session) = PlayMatch(ticks: 30);
        var recording = session.Recording(Config);
        using var text = new StringWriter();
        recording.Save(text);

        var loaded = MatchRecording.Load(new StringReader(text.ToString()));

        Assert.Equal(Config, loaded.Config);
        Assert.Equal(HumanMask, loaded.HumanMask);
        Assert.Equal(
            recording.Bundles.Select(b => (b.Tick, string.Join(',', b.Inputs))),
            loaded.Bundles.Select(b => (b.Tick, string.Join(',', b.Inputs))));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Replay_RebuildsTheWorldTheMatchEndedIn_BotsIncluded()
    {
        var (live, session) = PlayMatch(ticks: 72);
        var recording = session.Recording(Config);

        World replayed = recording.Replay().World;

        Assert.Equal(72, replayed.AdvanceCount);
        Assert.Equal(WorldStateHash.ComputeSync(live), WorldStateHash.ComputeSync(replayed));
        Assert.Equal(48, recording.Truncated(48).Replay().World.AdvanceCount);
    }

    [Fact]
    public void DesyncReport_ReadsAsARecording_AndNamesTheTickAndDigests()
    {
        var (world, session) = PlayMatch(ticks: 50);
        var recording = session.Recording(Config);
        using var text = new StringWriter();

        DesyncReport.Write(text, recording, desyncTick: 48, slot: 0, "build-x", session.RecentDigests, world);

        string report = text.ToString();
        Assert.Contains("differed at tick 48", report, StringComparison.Ordinal);
        Assert.Contains($"#   48 {session.RecentDigests.Last().Digest.State:X16}", report, StringComparison.Ordinal);
        var loaded = MatchRecording.Load(new StringReader(report));
        Assert.Equal(50, loaded.Bundles.Count);
        Assert.Contains("\n#   0 ", report.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("{", report, StringComparison.Ordinal);
        Assert.Equal(Config, loaded.Config);
    }

    [Fact]
    public void SeatChanges_AreSavedAsMaskLines_AndARecordingWithoutThemLoadsAsBefore()
    {
        TickBundle[] bundles =
        [
            new(0, [1u, 2u], HumanMask: 0b01),
            new(1, [3u, 4u], HumanMask: 0b11),
            new(2, [5u, 6u], HumanMask: 0b11),
        ];
        using var text = new StringWriter();
        new MatchRecording(Config, HumanMask: 0b01, bundles).Save(text);
        string saved = text.ToString().ReplaceLineEndings("\n");

        Assert.Contains("ticks:\n1 2\nmask=3\n3 4\n5 6\n", saved, StringComparison.Ordinal);
        Assert.Equal([0b01, 0b11, 0b11], MatchRecording.Load(new StringReader(saved)).Bundles.Select(b => b.HumanMask));
        // A recording made before seats could change has no mask lines: every tick has the mask of the header.
        var old = MatchRecording.Load(new StringReader(saved.Replace("mask=3\n", "", StringComparison.Ordinal)));
        Assert.Equal([0b01, 0b01, 0b01], old.Bundles.Select(b => b.HumanMask));
        Assert.Null(old.Snapshot);
    }

    [Fact]
    public void TextThatIsNotARecording_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => MatchRecording.Load(new StringReader("hello\nworld")));
        Assert.Throws<InvalidDataException>(() => MatchRecording.Load(new StringReader("seed=1\nticks:\nzz")));
    }
}
