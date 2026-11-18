using Tunnerer.Core;
using Tunnerer.Core.Bots;
using Tunnerer.Core.Snapshots;

namespace Tunnerer.Tests;

/// <summary>
/// What a snapshot leaves out is always a reviewed decision (docs/late-join-design.md §10.3, item 1). Every field
/// reachable from the world is saved unless marked <see cref="NotSavedAttribute"/>, and a field wrongly marked shows
/// up in <see cref="SnapshotRoundTripTests"/> only when its live value differs from a freshly built world's (a turret
/// cooldown that is back to 0 at every save passes them). So every mark is listed here with its reason, as a ratchet:
/// a new mark fails until it is added, and a removed one until its entry is deleted.
/// <para>
/// The writer itself throws on a value it cannot save, and the round-trip tests save the fingerprint scenarios
/// halfway and at the end; the walk here also catches a field that is null in every one of them.
/// </para>
/// </summary>
public class SnapshotCoverageTests
{
    /// <summary>
    /// Every <see cref="NotSavedAttribute"/> in Core, "Type.field: reason". Mark a field only when it is rebuilt on
    /// load, fixed for the match by the settings, output only, or scratch that a step fills before it reads it.
    /// </summary>
    private static readonly string[] Marks =
    [
        "Config.SimulationSettings: fixed for the match: built from the match settings",
        "Entities.Machines.MachineList._buckets: rebuilt on load from the machine list (OnSnapshotLoaded); looked up by key only",
        "Match.MatchRules: fixed for the match: the world is built with the same rules",
        "Network.LinkMap._linksByTile: rebuilt on load from the link list (OnSnapshotLoaded); looked up by key only",
        "Regrowth.RegrowthBlockerIndex._lookup: scratch: filled and read within Update",
        "Regrowth.RegrowthSchedule._spareTiles: a pool of all-zero arrays: a tile takes a new array when the pool is empty",
        "Sensing.SideSensing: output only: vision and radar, which nothing in the simulation reads",
        "SimulationProfile: diagnostics: wall-clock timings, which differ between machines",
        "StepEventLog: output only: what happened in the last step, for sound and the HUD; cleared before each step",
        "Terrain.TerrainGrid.LastCoolDownProfile: diagnostics: wall-clock timings of the last heat step",
        "Terrain.TerrainGrid._applyTileStep: fixed: a delegate to this grid's own method",
        "Terrain.TerrainGrid._changeList: output only: cells the renderer has yet to upload; a loaded world is uploaded whole",
        "Terrain.TerrainGrid._changesSeen: output only: how much of the change list the renderer has read",
        "Terrain.TerrainGrid._computeTileDelta: fixed: a delegate to this grid's own method",
        "Terrain.TerrainGrid._tileAirDelta: scratch: a heat step writes each tile's deltas before it reads them",
        "Terrain.TerrainGrid._tileDelta: scratch: a heat step writes each tile's deltas before it reads them",
        "Terrain.TerrainGrid._tileThreads: fixed for the match: the worker count, from the settings",
        "Thermal.TankHeatEngine: fixed for the match: built from the settings",
        "Thermal.TerrainHeatEngine: fixed for the match: the material tables, built from the settings",
        "World._machineContext: fixed for the match: closures over this world",
        "World._systems: fixed for the match: the step's systems, chosen by the rules",
        "World._tankContext: fixed for the match: closures over this world",
    ];

    [Fact]
    public void EveryFieldASnapshotReaches_CanBeSaved()
    {
        var problems = SnapshotCoverage.UnsavableFields([typeof(World), typeof(BotTankAI)]);

        Assert.True(problems.Count == 0,
            "A snapshot cannot save these fields. Hold the state in a type it can (fields, arrays, List<T>), or, if it " +
            "is rebuilt on load, fixed for the match, output only or scratch, mark it [NotSaved(\"reason\")] and list " +
            "it in SnapshotCoverageTests.Marks:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void EveryMark_SaysWhy()
    {
        var bare = SnapshotCoverage.Marks().Where(m => string.IsNullOrWhiteSpace(m.Reason)).Select(m => m.Name).ToList();

        Assert.True(bare.Count == 0, "Say why each of these is left out of a snapshot:\n" + string.Join("\n", bare));
    }

    [Fact]
    public void EveryMark_IsListed()
    {
        var found = SnapshotCoverage.Marks().Select(m => $"{m.Name}: {m.Reason}").ToHashSet(StringComparer.Ordinal);
        var listed = Marks.ToHashSet(StringComparer.Ordinal);

        var problems = found.Except(listed).Order(StringComparer.Ordinal)
            .Select(m => $"New or changed: \"{m}\". Check it is rebuilt on load, fixed for the match, output only or " +
                "scratch, then add it to SnapshotCoverageTests.Marks.")
            .Concat(listed.Except(found).Order(StringComparer.Ordinal)
                .Select(m => $"Gone: \"{m}\". Delete its entry from SnapshotCoverageTests.Marks."))
            .ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
