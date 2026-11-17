using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

/// <summary>
/// A harvester goes for ore it can get within reach of, by the path its footprint can crawl, and says so when the ore
/// left in its work area is out of its reach (DESIGN.md §6.4).
/// </summary>
public class HarvesterReachTests
{
    private const int Side = 26;

    private static int ActionTicks(int actions) =>
        (int)Math.Ceiling(actions * Tweaks.Machine.HarvesterIntervalMs / 1000.0 * Tweaks.Perf.TargetFps) + 2;

    private static Position At(RelayHeatTests.Scenario s, int dx, int dy) => s.RelayPos + new Offset(dx, dy);

    /// <summary>
    /// The scenario's tunnel with plain dirt on both sides of the relay position, a linked relay there, and a harvester
    /// planted in a dug-out pocket 5 cells to the side (offset (-5, 0) for <see cref="At"/>). The relay sees along the
    /// harvester's row, so the harvester stays on the network as it tunnels along it.
    /// </summary>
    private static (RelayHeatTests.Scenario S, Machine Harvester) InDirt()
    {
        var s = RelayHeatTests.CreateScenario();
        for (int dy = -Side; dy <= Side; dy++)
            for (int dx = -Side; dx <= Side; dx++)
                if (Math.Abs(dx) >= 3)
                    s.World.Terrain.SetPixel(At(s, dx, dy), TerrainPixel.Dirt);
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -7; dx <= -3; dx++)
                s.World.Terrain.SetPixel(At(s, dx, dy), TerrainPixel.Blank);
        RelayHeatTests.PlantRelay(s);
        var harvester = new Machine(At(s, -5, 0), MachineType.Harvester, s.Tank.Color);
        s.World.Machines.Add(harvester);
        s.World.PlantMachine(harvester, s.Base);
        return (s, harvester);
    }

    /// <summary>
    /// One plain ore cell in the middle of a 7x7 block of rock, 8 cells from the harvester: nearer than any other ore
    /// in these tests, and nowhere its footprint can stand is within reach of it.
    /// </summary>
    private static Position SealOre(RelayHeatTests.Scenario s)
    {
        var ore = At(s, -7, -8);
        for (int y = -3; y <= 3; y++)
            for (int x = -3; x <= 3; x++)
                s.World.Terrain.SetPixel(ore + new Offset(x, y), TerrainPixel.Rock);
        s.World.Terrain.SetPixel(ore, TerrainPixel.Emberite);
        return ore;
    }

    private static void Advance(RelayHeatTests.Scenario s, int ticks)
    {
        for (int i = 0; i < ticks; i++)
            s.World.Advance(_ => default);
    }

    [Fact]
    public void Harvester_PassesOverSealedOre_ForFartherOreItCanReach()
    {
        var (s, harvester) = InDirt();
        var sealedOre = SealOre(s);
        var open = new[] { At(s, -17, -1), At(s, -17, 0), At(s, -17, 1) };
        foreach (var cell in open)
            s.World.Terrain.SetPixel(cell, TerrainPixel.Emberite);
        int storeBefore = s.Base.Store.Ore;

        Advance(s, ActionTicks(12 + open.Length * Tweaks.Ore.HarvesterPlainWork + 4));

        Assert.Equal(storeBefore + open.Length, s.Base.Store.Ore);
        Assert.All(open, cell => Assert.Equal(TerrainPixel.Blank, s.World.Terrain.GetPixelRaw(cell)));
        Assert.Equal(TerrainPixel.Emberite, s.World.Terrain.GetPixelRaw(sealedOre));
        // Only the sealed cell is left: the harvester is idle and says it is out of ore.
        Assert.Equal(HarvesterState.NoOre, MachineBehaviors.HarvesterStateOf(harvester));
        Assert.Equal(0, s.World.GetNetworkStatus(s.Base).HarvestersWorking);
    }

    [Fact]
    public void Harvester_WithOnlySealedOre_StaysPut_SpendsNothing_AndReportsNoOre()
    {
        var (s, harvester) = InDirt();
        SealOre(s);
        double charge = harvester.Charge;

        Advance(s, ActionTicks(10));

        Assert.Equal(harvester.Anchor, harvester.Position);
        Assert.Equal(charge, harvester.Charge);
        Assert.Equal(HarvesterState.NoOre, MachineBehaviors.HarvesterStateOf(harvester));
    }

    [Fact]
    public void Harvester_GoesRoundRock_ToOreBehindIt()
    {
        var (s, harvester) = InDirt();
        // A rock in the harvester's row with ore behind it: the footprint has to swing four cells wide to pass.
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -12; dx <= -10; dx++)
                s.World.Terrain.SetPixel(At(s, dx, dy), TerrainPixel.Rock);
        var ore = At(s, -17, 0);
        s.World.Terrain.SetPixel(ore, TerrainPixel.Emberite);
        int storeBefore = s.Base.Store.Ore;

        Advance(s, ActionTicks(40));

        Assert.Equal(storeBefore + 1, s.Base.Store.Ore);
        Assert.Equal(TerrainPixel.Blank, s.World.Terrain.GetPixelRaw(ore));
        Assert.True(Position.DistanceSquared(harvester.Position, harvester.Anchor) <=
            Tweaks.Machine.HarvestRange * Tweaks.Machine.HarvestRange);
    }
}
