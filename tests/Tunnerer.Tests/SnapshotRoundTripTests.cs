using Tunnerer.Core;
using Tunnerer.Core.Bots;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Core.Input;
using Tunnerer.Core.Match;
using Tunnerer.Core.Entities.Projectiles;
using Tunnerer.Core.Snapshots;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;
using static Tunnerer.Tests.SnapshotRoundTrip;

namespace Tunnerer.Tests;

/// <summary>
/// A world saved mid-match and loaded into a freshly built one plays on exactly as the original does
/// (docs/late-join-design.md §10.3, item 2), checked by <see cref="SnapshotRoundTrip.AssertPlaysOnAlike"/>. Here: the
/// fingerprint scenarios saved halfway, and the snapshot bytes themselves. The scenarios built to reach state that
/// shows only later are in <see cref="SnapshotMachineRoundTripTests"/> and <see cref="SnapshotTankRoundTripTests"/>,
/// split so that xUnit runs them side by side.
/// </summary>
public class SnapshotRoundTripTests
{
    public static TheoryData<string> FingerprintScenarios =>
        ["PlayerDrivesAndShoots", "RelayPylonHarvesterNetwork", "BotVsBot", "EnemyMachinesShotToPieces"];

    [Theory]
    [MemberData(nameof(FingerprintScenarios))]
    public void FingerprintScenario_SavedHalfway_PlaysOnAlike(string name)
    {
        var run = FingerprintScenario(name);
        int half = run.Frames / 2;
        run.Play(0, half);

        AssertPlaysOnAlike(run.World, run.Bots, run.BuildBare, ticks: run.Frames - half, (f, t) => run.Input(f, t),
            firstFrame: half);
    }

    [Fact]
    public void Saving_IsCanonical_AndLoadingTwiceGivesTheSameWorld()
    {
        var world = BotWorld();
        BotTankAI?[] bots = [new BotTankAI(seed: 11), null];
        Run(world, bots, 0, 300, (_, _) => default);

        byte[] first = WorldSnapshot.Save(world, bots), second = WorldSnapshot.Save(world, bots);
        Assert.Equal(first, second);

        var once = BotWorld();
        var twice = BotWorld();
        var botsOnce = WorldSnapshot.Load(once, first);
        WorldSnapshot.Load(twice, WorldSnapshot.Save(once, botsOnce));
        Assert.Equal(first, WorldSnapshot.Save(twice, botsOnce));
        Assert.Null(botsOnce[1]);
    }

    [Fact]
    public void Compression_GivesBackTheSameBytes()
    {
        var world = BotWorld();
        BotTankAI?[] bots = [new BotTankAI(seed: 11), new BotTankAI(seed: 22)];
        Run(world, bots, 0, 200, (_, _) => default);
        byte[] raw = WorldSnapshot.Save(world, bots);

        byte[] compressed = WorldSnapshot.Compress(raw);

        Assert.True(compressed.Length < raw.Length / 2, $"{compressed.Length} of {raw.Length} bytes");
        Assert.Equal(raw, WorldSnapshot.Decompress(compressed));
        Assert.Throws<InvalidDataException>(() => WorldSnapshot.Decompress(compressed, maxBytes: raw.Length - 1));
    }

    [Fact]
    public void ASnapshotThatDoesNotFit_IsRefused()
    {
        var world = BotWorld();
        byte[] raw = WorldSnapshot.Save(world, [null, null]);

        Assert.Throws<InvalidDataException>(() => WorldSnapshot.Load(TestHelpers.CreateSeededWorld(size: new Size(160, 100)), raw));
        Assert.Throws<InvalidDataException>(() => WorldSnapshot.Load(BotWorld(), raw[..(raw.Length / 2)]));
        byte[] foreign = (byte[])raw.Clone();
        foreign[0] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => WorldSnapshot.Load(BotWorld(), foreign));
    }

    [Theory]
    [InlineData(typeof(DateOnly), "Unknown")]
    [InlineData(typeof(BotTankAI), "where a")]
    public void SnapshotRequiresAKnownTypeAssignableToItsRoot(Type wrong, string expectedError)
    {
        var world = BotWorld();
        byte[] snapshot = WorldSnapshot.Save(world, [null, null]);
        using var header = new MemoryStream();
        using var writer = new BinaryWriter(header);
        // Keep the map header, new-reference tag and first type index from the canonical snapshot.
        writer.Write(snapshot.AsSpan(0, 3 * sizeof(int) + 2));
        writer.Write(wrong.AssemblyQualifiedName!);
        var failure = Assert.Throws<InvalidDataException>(() => WorldSnapshot.Load(BotWorld(), header.ToArray()));
        Assert.Contains(expectedError, failure.Message, StringComparison.Ordinal);
    }

    private static SimulationFingerprintTests.Scenario FingerprintScenario(string name) => name switch
    {
        "PlayerDrivesAndShoots" => SimulationFingerprintTests.PlayerDrivesAndShoots(),
        "RelayPylonHarvesterNetwork" => SimulationFingerprintTests.RelayPylonHarvesterNetwork(),
        "BotVsBot" => SimulationFingerprintTests.BotVsBot(),
        "EnemyMachinesShotToPieces" => SimulationFingerprintTests.EnemyMachinesShotToPieces(),
        _ => throw new ArgumentException(name, nameof(name)),
    };
}
