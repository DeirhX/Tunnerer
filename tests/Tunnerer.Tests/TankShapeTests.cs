using System.Reflection;
using Tunnerer.Core.Match;
using Tunnerer.Core;
using Tunnerer.Core.Collision;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

/// <summary>
/// A tank has one shape, <see cref="TankSpriteShape"/>. Terrain contact, bullet hits and machine placement all read
/// it; these tests fail if one of them grows its own copy again.
/// </summary>
public class TankShapeTests
{
    // The eight directions a tank can face (index 4 is unused) and the move that turns it that way.
    public static TheoryData<int, int, int> Directions => new()
    {
        { 0, 1, -1 }, { 1, 0, -1 }, { 2, -1, -1 }, { 3, 1, 0 },
        { 5, -1, 0 }, { 6, 1, 1 }, { 7, 0, 1 }, { 8, -1, 1 },
    };

    [Theory]
    [MemberData(nameof(Directions))]
    public void BulletsHitExactlyTheTankShape(int dir, int dx, int dy)
    {
        _ = (dx, dy);
        var terrain = new TerrainGrid(new Size(60, 60));
        for (int i = 0; i < terrain.Size.Area; i++) terrain[i] = TerrainPixel.Blank;
        var solver = new CollisionSolver(terrain);
        var bases = new TankBases();
        bases.AddBase(new Position(30, 30), 0);
        var tanks = new TankList();
        var tank = tanks.AddTank(0, bases.GetSpawn(0)!);
        tank.Direction = dir;
        solver.Update(tanks, new MachineList());

        int reach = TankSpriteShape.Radius + 1;
        int covered = 0;
        for (int y = -reach; y <= reach; y++)
            for (int x = -reach; x <= reach; x++)
            {
                var point = new Position(tank.Position.X + x, tank.Position.Y + y);
                bool shape = TankSpriteShape.Contains(dir, tank.Position, point);
                bool hit = solver.TestPoint(point, onTank: _ => true);
                Assert.True(shape == hit, $"direction {dir}, offset ({x},{y}): shape {shape}, bullet hit {hit}");
                if (shape) covered++;
            }
        Assert.True(covered > 0);
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void TerrainBlocksExactlyTheTankShape(int dir, int dx, int dy)
    {
        var size = new Size(100, 100);
        var world = new World(size, simulationSeed: TestHelpers.DefaultSeed, new MatchRules { TerrainRegrowth = false });
        for (int i = 0; i < size.Area; i++) world.Terrain[i] = TerrainPixel.Blank;
        world.TankBases.AddBase(new Position(20, 20), 0);
        var tank = world.TankList.AddTank(0, world.TankBases.GetSpawn(0)!);
        var start = new Position(65, 65);
        var target = new Position(start.X + dx, start.Y + dy);

        int reach = TankSpriteShape.Radius + 1;
        for (int y = -reach; y <= reach; y++)
            for (int x = -reach; x <= reach; x++)
            {
                var rock = new Position(target.X + x, target.Y + y);
                world.Terrain.SetPixel(rock, TerrainPixel.Rock);
                tank.Position = start;

                world.Advance(i => i == 0 ? TestSimulation.Move(dx, dy) : TestSimulation.Idle);

                bool shape = TankSpriteShape.Contains(dir, target, rock);
                bool blocked = tank.Position == start;
                Assert.True(shape == blocked, $"direction {dir}, rock at ({x},{y}): shape {shape}, blocked {blocked}");
                world.Terrain.SetPixel(rock, TerrainPixel.Blank);
            }
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void TheDugTunnelFitsTheTankShape(int dir, int dx, int dy)
    {
        _ = (dx, dy);
        int r = Tweaks.Tank.DigRadius;
        var origin = new Position(0, 0);
        TankSpriteShape.ForEachPixel(dir, origin, (_, cell) =>
        {
            bool corner = Math.Abs(cell.X) == r && Math.Abs(cell.Y) == r;
            Assert.True(Math.Abs(cell.X) <= r && Math.Abs(cell.Y) <= r && !corner,
                $"direction {dir}: cell ({cell.X},{cell.Y}) lies outside the tunnel a tank digs");
            return true;
        });
    }

    [Fact]
    public void NoOtherTankShapeExists()
    {
        var copies = typeof(World).Assembly.GetTypes()
            .Where(t => t.Name.Contains("CollisionMask", StringComparison.Ordinal) ||
                        t.Name.Contains("Footprint", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();
        Assert.True(copies.Count == 0, $"tanks have one shape, TankSpriteShape; found {string.Join(", ", copies)}");

        var footprintFields = typeof(World).GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)
            .Where(f => f.Name.Contains("Tank", StringComparison.Ordinal))
            .Select(f => f.Name)
            .ToList();
        Assert.True(footprintFields.Count == 0, $"World keeps its own tank size: {string.Join(", ", footprintFields)}");

        // Only the shape reads the sprite bitmap, and every user of a tank's extent goes through the shape.
        var readers = RepositoryFiles.CSharpFiles("src/Tunnerer.Core")
            .Where(path => File.ReadAllText(path).Contains("TankSprites.Sprites", StringComparison.Ordinal))
            .Select(RepositoryFiles.Relative)
            .ToList();
        Assert.Equal(["src/Tunnerer.Core/Entities/Tanks/TankSpriteShape.cs"], readers);

        foreach (string user in new[]
                 {
                     "src/Tunnerer.Core/Collision/CollisionSolver.cs",
                     "src/Tunnerer.Core/Entities/Tanks/TankDigger.cs",
                     "src/Tunnerer.Core/World.cs",
                 })
        {
            string source = File.ReadAllText(Path.Combine(RepositoryFiles.Root, user));
            Assert.True(source.Contains("TankSpriteShape.", StringComparison.Ordinal),
                $"{user} should ask TankSpriteShape where a tank is");
        }
    }
}
