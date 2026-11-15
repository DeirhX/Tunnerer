using Tunnerer.Core;
using Tunnerer.Core.Types;

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
}
