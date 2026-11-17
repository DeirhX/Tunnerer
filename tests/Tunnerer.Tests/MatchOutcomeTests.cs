using Tunnerer.Core.Match;
using Tunnerer.Core;
using Tunnerer.Core.Types;
using Tunnerer.Core.Entities.Tanks;

namespace Tunnerer.Tests;

public class MatchOutcomeTests
{
    private static World CreateWorld() => TestHelpers.CreateSeededWorld(new Size(320, 200));

    [Fact]
    public void FreshMatch_IsInProgressForEveryone()
    {
        var world = CreateWorld();

        Assert.False(world.IsMatchOver);
        foreach (var tank in world.TankList.Tanks)
            Assert.Equal(MatchOutcome.InProgress, world.MatchOutcomeFor(tank));
    }

    [Fact]
    public void DeathWithLivesLeft_DoesNotEndTheMatch()
    {
        var world = CreateWorld();
        var player = world.TankList.Tanks[0];
        var enemy = world.TankList.Tanks[1];
        enemy.LivesLeft = 2;
        enemy.Die();

        Assert.False(world.IsMatchOver);
        Assert.Equal(MatchOutcome.InProgress, world.MatchOutcomeFor(player));
        Assert.Equal(MatchOutcome.InProgress, world.MatchOutcomeFor(enemy));
    }

    [Fact]
    public void EnemyOutOfLives_IsVictoryForTheLivingPlayerAndDefeatForTheEnemy()
    {
        var world = CreateWorld();
        var player = world.TankList.Tanks[0];
        var enemy = world.TankList.Tanks[1];
        enemy.LivesLeft = 1;
        enemy.Die();

        Assert.True(world.IsMatchOver);
        Assert.Equal(MatchOutcome.Victory, world.MatchOutcomeFor(player));
        Assert.Equal(MatchOutcome.Defeat, world.MatchOutcomeFor(enemy));
    }

    [Fact]
    public void PlayerRespawning_WhenEnemyIsOut_IsNotYetVictory()
    {
        var world = CreateWorld();
        var player = world.TankList.Tanks[0];
        var enemy = world.TankList.Tanks[1];
        enemy.LivesLeft = 1;
        enemy.Die();
        player.LivesLeft = 2;
        player.Die();

        Assert.True(world.IsMatchOver);
        Assert.Equal(MatchOutcome.InProgress, world.MatchOutcomeFor(player));
    }

    private static World CreateFourTankWorld(bool coreTapWins = false)
    {
        var size = new Size(480, 300);
        var (terrain, spawns) = new Core.LevelGen.ToastGenerator().Generate(size, seed: 42, playerCount: 4);
        var world = new World(size, simulationSeed: 42, new MatchRules { HotRockPockets = 3, CoreTapWins = coreTapWins });
        world.Initialize(terrain, spawns, decorationSeed: 43);
        return world;
    }

    private static void Eliminate(Tank tank)
    {
        tank.LivesLeft = 1;
        tank.Die();
    }

    [Fact]
    public void WithFourTanks_OneOutOfLivesDoesNotEndTheMatch()
    {
        var world = CreateFourTankWorld();
        var tanks = world.TankList.Tanks;
        Eliminate(tanks[3]);

        Assert.False(world.IsMatchOver);
        Assert.Null(world.MatchWinner);
        Assert.Equal(MatchOutcome.Defeat, world.MatchOutcomeFor(tanks[3]));
        Assert.Equal(MatchOutcome.InProgress, world.MatchOutcomeFor(tanks[0]));
    }

    [Fact]
    public void WithFourTanks_TheLastOneStandingWins()
    {
        var world = CreateFourTankWorld();
        var tanks = world.TankList.Tanks;
        Eliminate(tanks[0]);
        Eliminate(tanks[1]);
        Eliminate(tanks[3]);

        Assert.True(world.IsMatchOver);
        Assert.Equal((tanks[2], MatchWinReason.LastTankStanding), world.MatchWinner);
        Assert.Equal(MatchOutcome.Victory, world.MatchOutcomeFor(tanks[2]));
        Assert.Equal(MatchOutcome.Defeat, world.MatchOutcomeFor(tanks[0]));
    }

    [Fact]
    public void EveryTankOutOfLives_IsOverWithNoWinner()
    {
        var world = CreateWorld();
        foreach (var tank in world.TankList.Tanks)
            Eliminate(tank);

        Assert.True(world.IsMatchOver);
        Assert.Null(world.MatchWinner);
    }

    [Fact]
    public void InARace_TheFirstBaseToTapTheCoreWins_EvenWithEveryTankAlive()
    {
        var tanks = CreateFourTankWorld().TankList.Tanks;

        var winner = MatchReferee.Winner(tanks, firstToTapCore: tanks[1].Base);

        Assert.Equal((tanks[1], MatchWinReason.TappedTheCore), winner);
        Assert.Equal(MatchOutcome.Victory, MatchReferee.OutcomeFor(tanks[1], winner?.Tank));
        Assert.Equal(MatchOutcome.Defeat, MatchReferee.OutcomeFor(tanks[0], winner?.Tank));
    }

    [Fact]
    public void OutsideARace_ATappedCoreDecidesNothing()
    {
        var world = CreateFourTankWorld(coreTapWins: false);

        Assert.Null(MatchReferee.Winner(world.TankList.Tanks, firstToTapCore: null));
        Assert.Null(world.MatchWinner);
        Assert.False(world.IsMatchOver);
    }

    [Fact]
    public void OnceDecided_TheWinnerDoesNotChange()
    {
        var world = CreateFourTankWorld();
        var tanks = world.TankList.Tanks;
        Eliminate(tanks[0]);
        Eliminate(tanks[1]);
        Eliminate(tanks[3]);
        TestSimulation.AdvanceIdle(world, frames: 1);
        Assert.Equal((tanks[2], MatchWinReason.LastTankStanding), world.MatchWinner);

        // The winner is lost afterwards: nobody is left, but the match was already won, and stays won for that tank.
        Eliminate(tanks[2]);
        TestSimulation.AdvanceIdle(world, frames: 3);

        Assert.Equal((tanks[2], MatchWinReason.LastTankStanding), world.MatchWinner);
        Assert.True(world.IsMatchOver);
        Assert.Equal(MatchOutcome.Victory, world.MatchOutcomeFor(tanks[2]));
        Assert.Equal(MatchOutcome.Defeat, world.MatchOutcomeFor(tanks[0]));
    }

    [Fact]
    public void AMatchThatEndedWithNobodyLeft_StaysWithoutAWinner()
    {
        var world = CreateFourTankWorld();
        var tanks = world.TankList.Tanks;
        foreach (var tank in tanks)
            Eliminate(tank);
        TestSimulation.AdvanceIdle(world, frames: 1);

        Assert.True(world.IsMatchOver);
        Assert.Null(world.MatchWinner);
    }

    /// <summary>Gives the tank's base a hold already over the goal, which finishes on the next tick whatever the tap is.</summary>
    private static void FinishHold(Tank tank) =>
        typeof(Core.Entities.TankBase).GetProperty(nameof(Core.Entities.TankBase.CoreHoldSeconds))!
            .SetValue(tank.Base, Core.Config.Tweaks.Goal.GoalHoldSeconds + 10);

    [Fact]
    public void ABaseWhoseTankIsOutOfLives_CannotWinTheRaceByFinishingItsHold()
    {
        var world = CreateFourTankWorld(coreTapWins: true);
        var tanks = world.TankList.Tanks;
        Assert.NotNull(world.CorePocket);
        Eliminate(tanks[1]);
        FinishHold(tanks[1]);

        TestSimulation.AdvanceIdle(world, frames: 1);

        Assert.True(tanks[1].Base!.HasTappedCore);
        Assert.Null(world.MatchWinner);
        Assert.False(world.IsMatchOver);

        FinishHold(tanks[2]);
        TestSimulation.AdvanceIdle(world, frames: 1);

        Assert.Equal((tanks[2], MatchWinReason.TappedTheCore), world.MatchWinner);
        Assert.Equal(MatchOutcome.Defeat, world.MatchOutcomeFor(tanks[1]));
        Assert.Equal(MatchOutcome.Victory, world.MatchOutcomeFor(tanks[2]));
    }
}
