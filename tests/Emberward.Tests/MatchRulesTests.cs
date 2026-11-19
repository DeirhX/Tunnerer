namespace Emberward.Tests;

using Emberward.Core;
using Emberward.Core.Match;
using Emberward.Core.Types;

public class MatchRulesTests
{
    private static World Build(MatchRules rules)
    {
        var size = new Size(320, 200);
        var (terrain, spawns) = new Core.LevelGen.ToastGenerator().Generate(size, seed: 42);
        var world = new World(size, simulationSeed: 42, rules);
        world.Initialize(terrain, spawns, decorationSeed: 43);
        return world;
    }

    [Fact]
    public void RulesTheStateCannotShow_StillChangeTheReadyHash()
    {
        // Whether the core ends the match leaves no trace in a freshly built world, so only the rules can tell.
        var race = Build(new MatchRules { HotRockPockets = 3, CoreTapWins = true });
        var goal = Build(new MatchRules { HotRockPockets = 3 });

        Assert.Equal(WorldStateHash.Compute(race), WorldStateHash.Compute(goal));
        Assert.NotEqual(WorldStateHash.ComputeReady(race), WorldStateHash.ComputeReady(goal));
    }

    [Fact]
    public void EqualRules_GiveEqualFingerprints()
    {
        Assert.Equal(MatchRules.Standard.Fingerprint(), (MatchRules.Standard with { }).Fingerprint());
        Assert.NotEqual(MatchRules.Standard.Fingerprint(), new MatchRules().Fingerprint());
    }

    [Fact]
    public void AWorldWithoutHotRock_HasNoSectorGoal()
    {
        Assert.Null(Build(new MatchRules()).CorePocket);
        Assert.NotNull(Build(new MatchRules { HotRockPockets = 3 }).CorePocket);
    }

    [Fact]
    public void NegativePocketCounts_AreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchRules { HotRockPockets = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchRules { FrostRockPockets = -1 });
    }
}
