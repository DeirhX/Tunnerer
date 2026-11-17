using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

/// <summary>The machine list files its machines by place, so questions about a place look only at what is near it.</summary>
public class MachineListTests
{
    private static Machine Add(MachineList list, int x, int y, MachineType type = MachineType.Harvester) =>
        list.Add(new Machine(new Position(x, y), type, ownerColor: 0));

    private static List<Machine> Near(MachineList list, int x, int y, int reach)
    {
        var found = new List<Machine>();
        list.CollectNear(new Position(x, y), reach, found);
        return found;
    }

    [Fact]
    public void CollectNear_FindsTheMachinesInTheSquare_InListOrder_WhereverTheyAreFiled()
    {
        var list = new MachineList();
        // Around a corner where four filing squares meet, added out of positional order, and some out of reach.
        Machine a = Add(list, 70, 70), far = Add(list, 200, 64), b = Add(list, 60, 60), c = Add(list, 66, 58);
        Machine edge = Add(list, 64 + 12, 64), beyond = Add(list, 64 + 13, 64), d = Add(list, 58, 69);

        Assert.Equal([a, b, c, edge, d], Near(list, 64, 64, reach: 12));
        Assert.DoesNotContain(far, Near(list, 64, 64, reach: 12));
        Assert.DoesNotContain(beyond, Near(list, 64, 64, reach: 12));
        Assert.Empty(Near(list, 500, 500, reach: 12));
    }

    [Fact]
    public void CollectNear_GivesWhatWalkingTheWholeListGives()
    {
        var list = new MachineList();
        var rng = new Random(5);
        for (int i = 0; i < 400; i++)
            Add(list, rng.Next(0, 300), rng.Next(0, 300));

        for (int i = 0; i < 200; i++)
        {
            var center = new Position(rng.Next(-20, 320), rng.Next(-20, 320));
            int reach = rng.Next(0, 45);
            var expected = list.Machines
                .Where(m => Math.Abs(m.Position.X - center.X) <= reach && Math.Abs(m.Position.Y - center.Y) <= reach)
                .ToList();
            Assert.Equal(expected, Near(list, center.X, center.Y, reach));
        }
    }

    [Fact]
    public void AMachineThatMoves_IsFoundWhereItIsNow()
    {
        var list = new MachineList();
        Machine harvester = Add(list, 30, 30);

        harvester.Position = new Position(31, 30);
        Assert.Equal([harvester], Near(list, 31, 30, reach: 0));

        // Across a filing square's edge, and far away.
        harvester.Position = new Position(33, 30);
        Assert.Equal([harvester], Near(list, 33, 30, reach: 2));
        harvester.Position = new Position(400, 90);
        Assert.Empty(Near(list, 33, 30, reach: 20));
        Assert.Equal([harvester], Near(list, 395, 95, reach: 5));
    }

    [Fact]
    public void ADestroyedMachine_LeavesTheListAndItsFiling_AndTheOthersKeepTheirOrder()
    {
        var terrain = new TerrainGrid(new Size(100, 100));
        terrain.Fill(TerrainPixel.Dirt);
        var list = new MachineList();
        Machine first = Add(list, 40, 40, MachineType.Pylon), doomed = Add(list, 42, 40, MachineType.Pylon), last = Add(list, 44, 40);
        doomed.IsAlive = false;

        list.Advance(new MachineContext(terrain, (_, _) => false, list, _ => false), TimeSpan.Zero);

        Assert.Equal([first, last], list.Machines);
        Assert.Equal([first, last], Near(list, 42, 40, reach: 5));
        Assert.Equal([first], list.Chargers);
        Assert.True(first.Sequence < last.Sequence);
    }
}
