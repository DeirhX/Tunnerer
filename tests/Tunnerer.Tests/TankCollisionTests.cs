namespace Tunnerer.Tests;

using Tunnerer.Core;
using Tunnerer.Core.Collision;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Core.Input;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

/// <summary>
/// Tanks are solid to each other and to another side's planted machines (DESIGN.md §5.2), and
/// <see cref="World.ProbeTankStep"/> gives the answer the tank's own move gets.
/// </summary>
public class TankCollisionTests
{
    private static readonly Offset East = new(1, 0);
    private static readonly Offset West = new(-1, 0);

    /// <summary>A world with an open hall in the middle: the driver on its west side, the other tank parked far east.</summary>
    private sealed record Arena(World World, Tank Driver, Tank Other, Position Center);

    private static Arena CreateArena()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false);
        var center = new Position(world.Terrain.Width / 2, world.Terrain.Height / 2);
        Fill(world, center, halfWidth: 45, halfHeight: 12, TerrainPixel.Blank);
        var driver = world.TankList.Tanks[0];
        var other = world.TankList.Tanks[1];
        driver.Position = center + new Offset(-25, 0);
        other.Position = center + new Offset(40, 0);
        return new Arena(world, driver, other, center);
    }

    private static void Fill(World world, Position center, int halfWidth, int halfHeight, TerrainPixel pixel)
    {
        for (int y = center.Y - halfHeight; y <= center.Y + halfHeight; y++)
            for (int x = center.X - halfWidth; x <= center.X + halfWidth; x++)
                world.Terrain.SetPixel(new Position(x, y), pixel);
    }

    private static void Drive(Arena a, Offset move, int ticks, bool torch = false)
    {
        var input = new ControllerOutput
        {
            MoveSpeed = move,
            ShootPrimary = torch,
            AimDirection = new DirectionF(move.X, move.Y),
        };
        for (int i = 0; i < ticks; i++)
            a.World.Advance(tank => tank == 0 ? input : default);
    }

    private static Machine Plant(Arena a, Position at, Tank owner)
    {
        var machine = new Machine(at, MachineType.Relay, owner.Color);
        a.World.Machines.Add(machine);
        a.World.PlantMachine(machine, owner.Base);
        return machine;
    }

    private static bool HullsOverlap(Tank a, Tank b) =>
        TankSpriteShape.Overlaps(a.Direction, a.Position, b.Direction, b.Position);

    [Fact]
    public void ATank_StopsAgainstAnotherTank()
    {
        var a = CreateArena();
        a.Other.Position = a.Center;

        Drive(a, East, ticks: 60);

        Assert.False(HullsOverlap(a.Driver, a.Other), "the tanks must not end up inside each other");
        Assert.InRange(a.Driver.Position.X, a.Center.X - TankSpriteShape.Size, a.Center.X - 1);
        Assert.Equal(StepObstacle.Entity, a.World.ProbeTankStep(a.Driver, East));
    }

    [Fact]
    public void ADeadTank_BlocksNothing()
    {
        var a = CreateArena();
        a.Other.Position = a.Center;
        a.Other.Die(a.World.Projectiles, a.World.Terrain);

        Drive(a, East, ticks: 12);

        Assert.Equal(a.Center.X - 25 + 12, a.Driver.Position.X);
    }

    [Fact]
    public void OverlappingTanks_CanPart_AndThenCollide()
    {
        var a = CreateArena();
        a.Other.Position = a.Center;
        a.Driver.Position = a.Center; // as after a respawn onto an intruder

        Drive(a, West, ticks: 12);
        Assert.Equal(a.Center.X - 12, a.Driver.Position.X);

        Drive(a, East, ticks: 30);
        Assert.False(HullsOverlap(a.Driver, a.Other), "once apart, the tanks are solid to each other again");
    }

    [Fact]
    public void OverlappingTanks_DoNotLetOneDriveThroughTheOther()
    {
        var a = CreateArena();
        a.Other.Position = a.Center;
        a.Driver.Position = a.Center + new Offset(-3, 0); // inside the other hull, on its west side

        Drive(a, East, ticks: 30);

        Assert.True(a.Driver.Position.X < a.Center.X,
            $"driving into the tank it overlaps must not carry the driver past it; it reached x={a.Driver.Position.X}");
    }

    [Fact]
    public void ATank_StopsAgainstAnEnemyMachine_ButNotItsOwn()
    {
        var blocked = CreateArena();
        var enemyRelay = Plant(blocked, blocked.Center, owner: blocked.Other);
        Drive(blocked, East, ticks: 60);
        int half = enemyRelay.BoundingBox.HalfWidth;
        Assert.False(
            TankSpriteShape.OverlapsSquare(blocked.Driver.Direction, blocked.Driver.Position, enemyRelay.Position, half),
            "the tank must not drive into another side's machine");
        Assert.True(blocked.Driver.Position.X < blocked.Center.X);
        Assert.Equal(StepObstacle.Entity, blocked.World.ProbeTankStep(blocked.Driver, East));

        var free = CreateArena();
        Plant(free, free.Center, owner: free.Driver);
        Drive(free, East, ticks: 60);
        Assert.True(free.Driver.Position.X > free.Center.X + TankSpriteShape.Size,
            $"the tank should drive on over its own machine; it stopped at x={free.Driver.Position.X}");
    }

    [Fact]
    public void AMachineTheTankCarries_BlocksNoOne()
    {
        var a = CreateArena();
        a.World.Machines.Add(new Machine(a.Center, MachineType.Relay, a.Other.Color) { State = MachineState.Transporting });

        Drive(a, East, ticks: 40);

        Assert.Equal(a.Center.X - 25 + 40, a.Driver.Position.X);
    }

    [Theory]
    [InlineData(TerrainPixel.Blank, StepObstacle.None)]
    [InlineData(TerrainPixel.Dirt, StepObstacle.Dirt)]
    [InlineData(TerrainPixel.Rock, StepObstacle.Hard)]
    [InlineData(TerrainPixel.Emberite, StepObstacle.Hard)]
    [InlineData(TerrainPixel.HotRock, StepObstacle.Solid)]
    public void ProbeTankStep_NamesTheGroundTheHullMeets_NotJustTheCellAhead(TerrainPixel pixel, StepObstacle expected)
    {
        var a = CreateArena();
        var start = a.Driver.Position;

        // One cell on the edge of the hull's next position, off the line the tank's centre travels.
        var next = start + East;
        var edge = HullEdgeCell(TankSpriteShape.DirectionOf(East), next);
        Assert.NotEqual(next.Y, edge.Y);
        a.World.Terrain.SetPixel(edge, pixel);

        Assert.Equal(expected, a.World.ProbeTankStep(a.Driver, East));

        // The move agrees with the probe: only open ground lets the first step through without digging.
        Drive(a, East, ticks: 1);
        Assert.Equal(expected == StepObstacle.None, a.Driver.Position != start);
    }

    [Fact]
    public void ProbeTankStep_SaysSolid_AtTheMapEdge()
    {
        var a = CreateArena();
        a.Driver.Position = new Position(TankSpriteShape.Radius, a.Center.Y);

        Assert.Equal(StepObstacle.Solid, a.World.ProbeTankStep(a.Driver, West));
    }

    /// <summary>The hull cell furthest along +X that is not on the centre row.</summary>
    private static Position HullEdgeCell(int dir, Position origin)
    {
        Position? best = null;
        TankSpriteShape.ForEachPixel(dir, origin, (_, cell) =>
        {
            if (cell.Y != origin.Y && (best is not Position b || cell.X > b.X))
                best = cell;
            return true;
        });
        return best ?? throw new InvalidOperationException("the tank shape has no cell off its centre row");
    }
}
