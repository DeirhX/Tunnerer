using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities.Emberites;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Rendering;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

namespace Emberward.Tests;

/// <summary>
/// What a dying emberite drops of its nest's deposit (<see cref="EmberiteSwarm.Kill"/>): on every
/// <see cref="Tweaks.Emberites.DropEveryKills"/>-th death of a nest, the plain ore cell farthest from the nest's centre
/// leaves the deposit and a loose one lands where the creature died. A unit is moved, never made: the deposit is its
/// cells, and nothing else keeps count.
/// </summary>
public class EmberiteDropTests
{
    // Clear of both bases of the 320 x 200 test map, which stand at (130, 21) and (185, 178).
    private static readonly Position Centre = new(60, 100);
    private static readonly Position NestCentre = Centre + new Offset(30, 0);
    private const int NestRadius = 4;

    /// <summary>
    /// An open arena with a hot tank at its centre and, 30 cells off, a nest of <paramref name="creatures"/> in a deposit:
    /// a core cell, and plain ore out to <paramref name="oreRadius"/> cells around it (0 for a core alone).
    /// </summary>
    private static (World World, Tank Tank, Nest Nest) Arena(int creatures, int oreRadius)
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false, emberites: true);
        for (int y = -30; y <= 30; y++)
            for (int x = -60; x <= 60; x++)
                world.Terrain.SetPixel(Centre + new Offset(x, y), TerrainPixel.Blank);
        for (int y = -oreRadius; y <= oreRadius; y++)
            for (int x = -oreRadius; x <= oreRadius; x++)
                if (x * x + y * y <= oreRadius * oreRadius)
                    world.Terrain.SetPixel(NestCentre + new Offset(x, y), TerrainPixel.Pyrite);
        world.Terrain.SetPixel(NestCentre, TerrainPixel.PyriteCore);
        var tank = world.TankList.Tanks[0];
        tank.Position = Centre;
        return (world, tank, world.Emberites!.AddNest(NestCentre, NestRadius, creatures));
    }

    private static void Step(World world, Tank tank)
    {
        tank.Heat = 60f;
        world.Advance(_ => default);
    }

    /// <summary>
    /// Kills the creature that has run furthest from its nest once one is <paramref name="clearOfNest"/> cells from it
    /// (0: the first one out, where it stands), and returns the cell it died in.
    /// </summary>
    private static Position KillOne(World world, Tank tank, int clearOfNest = 14)
    {
        var swarm = world.Emberites!;
        var states = new EmberiteRenderState[64];
        for (int t = 0; t < 400; t++)
        {
            int count = world.CopyEmberiteRenderStates(states);
            for (int i = 0; i < count; i++)
            {
                var cell = (Position)states[i].Position;
                if (Position.DistanceSquared(cell, NestCentre) < clearOfNest * clearOfNest)
                    continue;
                swarm.Kill(i);
                // The dead creature leaves the list on the swarm's next step, so the next call sees the living only.
                Step(world, tank);
                return cell;
            }
            Step(world, tank);
        }
        throw new Xunit.Sdk.XunitException("no creature came out to be killed");
    }

    private static List<Position> Cells(World world, Func<Position, bool> where)
    {
        var cells = new List<Position>();
        for (int y = 0; y < world.Terrain.Height; y++)
            for (int x = 0; x < world.Terrain.Width; x++)
                if (world.Terrain.GetPixelRaw(new Position(x, y)) == TerrainPixel.Pyrite && where(new Position(x, y)))
                    cells.Add(new Position(x, y));
        return cells;
    }

    private static bool InNest(Position cell) => Position.DistanceSquared(cell, NestCentre) <= NestRadius * NestRadius;

    private static List<Position> Deposit(World world) => Cells(world, InNest);

    private static List<Position> Loose(World world) => Cells(world, cell => !InNest(cell));

    [Fact]
    public void EveryFourthDeath_MovesAUnitFromTheDepositsRim_ToWhereTheCreatureDied()
    {
        Assert.Equal(4, Tweaks.Emberites.DropEveryKills);
        var (world, tank, nest) = Arena(creatures: 8, oreRadius: 3);
        List<Position> deposit = Deposit(world);
        Assert.Empty(Loose(world));
        // The cell that goes first: the farthest from the centre, the first in row order among equals.
        Position rim = deposit.OrderByDescending(c => Position.DistanceSquared(c, NestCentre)).ThenBy(c => c.Y).ThenBy(c => c.X).First();

        // Three deaths leave the deposit as it is.
        for (int death = 1; death <= 3; death++)
            KillOne(world, tank);
        Assert.Equal(5, nest.Alive);
        Assert.Equal(deposit, Deposit(world));
        Assert.Empty(Loose(world));

        // The fourth drops a unit where it dies, and the deposit is a cell smaller, at its rim.
        Position died = KillOne(world, tank);
        Assert.Equal(4, nest.Alive);
        Assert.Equal(deposit.Where(c => c != rim), Deposit(world));
        Assert.Equal(TerrainPixel.Blank, world.Terrain.GetPixelRaw(rim));
        Position dropped = Assert.Single(Loose(world));
        Assert.True(Position.DistanceSquared(dropped, died) <= Tweaks.Ore.DeathDropRadius * Tweaks.Ore.DeathDropRadius,
            $"the unit should lie where the creature died: {dropped} against {died}");
        Assert.False(world.IsOreGuarded(dropped), "loose ore outside the nest's ground is free to harvest");
        Assert.Equal(TerrainPixel.PyriteCore, world.Terrain.GetPixelRaw(NestCentre));

        // And the eighth, the nest's last, another.
        for (int death = 5; death <= 8; death++)
            KillOne(world, tank);
        Assert.True(nest.IsCleared);
        Assert.Equal(deposit.Count - 2, Deposit(world).Count);
        Assert.Equal(2, Loose(world).Count);
    }

    [Fact]
    public void ADepositWithNoPlainOreLeft_DropsNothing()
    {
        var (world, tank, nest) = Arena(creatures: 4, oreRadius: 0);
        for (int death = 1; death <= 4; death++)
            KillOne(world, tank);

        Assert.True(nest.IsCleared);
        Assert.Empty(Deposit(world));
        Assert.Empty(Loose(world));
        Assert.Equal(TerrainPixel.PyriteCore, world.Terrain.GetPixelRaw(NestCentre));
    }

    [Fact]
    public void WithNoRoomWhereItDies_TheDepositKeepsItsOre()
    {
        // Ore reaches well past the nest's ground, so a creature killed as it comes out has only ore around it: a
        // dropped unit would find no open cell, and the deposit must not lose one for nothing.
        var (world, tank, nest) = Arena(creatures: 4, oreRadius: NestRadius + Tweaks.Ore.DeathDropRadius + 3);
        int before = Cells(world, _ => true).Count;
        List<Position> deposit = Deposit(world);

        // All four deaths happen, the fourth the one that would drop; the total alone cannot tell a move from no drop
        // (a moved unit is still a cell of ore), so the nest's own cells must be the same ones too.
        for (int death = 1; death <= 4; death++)
            KillOne(world, tank, clearOfNest: 0);

        Assert.True(nest.IsCleared);
        Assert.Equal(deposit, Deposit(world));
        Assert.Equal(before, Cells(world, _ => true).Count);
    }
}
