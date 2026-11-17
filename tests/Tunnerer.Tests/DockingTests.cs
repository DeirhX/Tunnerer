using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

public class DockingTests
{
    [Fact]
    public void IsDockedAtOwnBase_MatchesWhichBaseTheTankIsIn()
    {
        var world = TestHelpers.CreateSeededWorld(new Size(320, 200));
        var tank = world.TankList.Tanks[0];
        var own = world.TankBases.GetSpawn(0)!;
        var foreign = world.TankBases.GetSpawn(1)!;

        tank.Position = own.Position;
        Assert.True(tank.IsDockedAtOwnBase);

        tank.Position = foreign.Position;
        Assert.False(tank.IsDockedAtOwnBase);

        tank.Position = new Position(own.Position.X, own.Position.Y + own.BoundingBox.HalfHeight + 5);
        Assert.Null(world.TankBases.CheckBaseCollision(tank.Position));
        Assert.False(tank.IsDockedAtOwnBase);
    }

    [Fact]
    public void DeadTank_IsNotDocked()
    {
        var world = TestHelpers.CreateSeededWorld(new Size(320, 200));
        var tank = world.TankList.Tanks[0];
        tank.Position = world.TankBases.GetSpawn(0)!.Position;
        tank.LivesLeft = 2;
        tank.Die();

        Assert.False(tank.IsDockedAtOwnBase);
    }
}
