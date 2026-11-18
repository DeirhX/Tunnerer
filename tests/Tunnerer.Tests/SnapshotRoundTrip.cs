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
namespace Tunnerer.Tests;

/// <summary>
/// The check every snapshot round-trip test makes: a world saved mid-match and loaded into a freshly built one plays
/// on exactly as the original does (docs/late-join-design.md §10.3, item 2). Every tick after the load both worlds
/// give the same sync digest (which hashes every cell's heat and air temperature bits); every
/// <see cref="FingerprintEvery"/> ticks and at the end they also give the same full fingerprint (adding the ledger),
/// and at the end they save to the same bytes.
/// </summary>
internal static class SnapshotRoundTrip
{
    internal delegate ControllerOutput Script(int frame, int tank);

    /// <summary>
    /// How often the full fingerprint is compared beside the digest. It hashes both temperature fields again, which
    /// the digest already does, so comparing it every tick doubled the cost of these tests for nothing new.
    /// </summary>
    internal const int FingerprintEvery = 20;

    /// <summary>
    /// Saves <paramref name="original"/>, loads it into <paramref name="build"/>'s world, checks the loaded world
    /// saves to the same bytes, then steps both <paramref name="ticks"/> times, comparing every tick.
    /// <paramref name="before"/> does the same to both worlds before each tick.
    /// </summary>
    internal static World AssertPlaysOnAlike(World original, BotTankAI?[] bots, Func<World> build, int ticks, Script script,
        int firstFrame = 0, Action<World, int>? before = null)
    {
        byte[] saved = WorldSnapshot.Save(original, bots);
        var loaded = build();
        var loadedBots = WorldSnapshot.Load(loaded, saved);
        Assert.Equal(saved, WorldSnapshot.Save(loaded, loadedBots));
        AssertSame(original, bots, loaded, loadedBots, "on load", fingerprint: true);

        for (int t = 0; t < ticks; t++)
        {
            before?.Invoke(original, t);
            before?.Invoke(loaded, t);
            Run(original, bots, firstFrame + t, 1, script);
            Run(loaded, loadedBots, firstFrame + t, 1, script);
            AssertSame(original, bots, loaded, loadedBots, $"{t + 1} ticks after the load",
                fingerprint: (t + 1) % FingerprintEvery == 0 || t + 1 == ticks);
        }
        Assert.True(WorldSnapshot.Save(original, bots).AsSpan().SequenceEqual(WorldSnapshot.Save(loaded, loadedBots)),
            $"the worlds save to different bytes {ticks} ticks after the load");
        return loaded;
    }

    internal static void AssertSame(World a, BotTankAI?[] botsA, World b, BotTankAI?[] botsB, string when, bool fingerprint)
    {
        if (fingerprint)
        {
            ulong fa = SimulationFingerprintTests.Fingerprint(a), fb = SimulationFingerprintTests.Fingerprint(b);
            Assert.True(fa == fb, $"Fingerprints differ {when}: 0x{fa:X16} against 0x{fb:X16}.");
        }
        SyncDigest da = WorldStateHash.ComputeSync(a, botsA), db = WorldStateHash.ComputeSync(b, botsB);
        Assert.True(da == db, $"Sync digests differ {when}: {da} against {db}.");
    }

    internal static void Run(World world, BotTankAI?[] bots, int firstFrame, int frames, Script script) =>
        SimulationFingerprintTests.Scenario.Play(world, bots, firstFrame, frames, (f, t) => script(f, t));

    internal static World BotWorld() =>
        TestHelpers.CreateSeededWorld(hotRockPocketCount: 2, frostRockPocketCount: 1, seedOre: true);
}
