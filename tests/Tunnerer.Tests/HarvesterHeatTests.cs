using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

/// <summary>
/// Harvesters mine emberite into their base's store while they stand in its network (DESIGN.md §6.4), and working
/// machines give off heat: a harvester that works dumps energy into the ground beside it.
/// </summary>
public class HarvesterHeatTests
{
    private static int HarvestTicks(int actions) =>
        (int)Math.Ceiling(actions * Tweaks.Machine.HarvesterIntervalMs / 1000.0 * Tweaks.Perf.TargetFps) + 2;

    /// <summary>
    /// Puts a block of plain ore in the dirt beside the scenario's tunnel, <paramref name="along"/> cells past the relay
    /// position, from the tunnel wall outward: <paramref name="depth"/> cells deep, <paramref name="length"/> long.
    /// </summary>
    internal static void SeedOre(RelayHeatTests.Scenario s, int along = 0, int depth = 4, int length = 7, bool core = false)
    {
        for (int d = 0; d < length; d++)
            for (int x = 3; x < 3 + depth; x++)
                s.World.Terrain.SetPixel(s.RelayPos + new Offset(x, s.DirY * (along + d - length / 2)),
                    core ? TerrainPixel.EmberiteCore : TerrainPixel.Emberite);
    }

    private static int OreAround(RelayHeatTests.Scenario s, int range)
    {
        int units = 0;
        var t = s.World.Terrain;
        for (int dy = -range; dy <= range; dy++)
            for (int dx = -range; dx <= range; dx++)
            {
                var pos = s.RelayPos + new Offset(dx, dy);
                if (dx * dx + dy * dy <= range * range && t.IsInside(pos))
                    units += Pixel.OreUnits(t.GetPixelRaw(pos));
            }
        return units;
    }

    private static Machine PlantHarvester(RelayHeatTests.Scenario s, Position? at = null)
    {
        var harvester = new Machine(at ?? s.RelayPos, MachineType.Harvester, s.Tank.Color);
        s.World.Machines.Add(harvester);
        s.World.PlantMachine(harvester, s.Base);
        return harvester;
    }

    /// <summary>
    /// A harvester beside ore, with a linked relay 15 cells back toward the base putting it in the network (and too far
    /// to pump away the heat it makes at once).
    /// </summary>
    private static (RelayHeatTests.Scenario S, Machine Harvester) Networked(bool harvester = true)
    {
        var s = RelayHeatTests.CreateScenario();
        SeedOre(s);
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * -15));
        return (s, harvester ? PlantHarvester(s) : null!);
    }

    private static void Advance(RelayHeatTests.Scenario s, int ticks)
    {
        for (int i = 0; i < ticks; i++)
            s.World.Advance(_ => default);
    }

    [Fact]
    public void Harvester_WhenItWorks_AddsHeatToTheGround()
    {
        var (working, _) = Networked();
        var (idle, _) = Networked(harvester: false);

        double startWorking = working.World.SumSystemThermalEnergy();
        double startIdle = idle.World.SumSystemThermalEnergy();
        for (int i = 0; i < HarvestTicks(1); i++)
        {
            working.World.Advance(_ => default);
            idle.World.Advance(_ => default);
        }

        double extra = (working.World.SumSystemThermalEnergy() - startWorking) - (idle.World.SumSystemThermalEnergy() - startIdle);
        Assert.True(extra > Tweaks.Machine.HarvesterHeatPerHarvest * 0.5,
            $"working ore should leave real heat in the ground. extra={extra:0.0}");
    }

    [Fact]
    public void Harvester_PutsTheOreItMinesIntoItsBaseStore_AFewActionsACell()
    {
        var (s, _) = Networked();
        int storeBefore = s.Base.Store.Ore;
        int oreBefore = OreAround(s, Tweaks.Machine.HarvestRange);

        Advance(s, HarvestTicks(Tweaks.Ore.HarvesterPlainWork - 1));
        Assert.Equal(oreBefore, OreAround(s, Tweaks.Machine.HarvestRange));

        Advance(s, HarvestTicks(Tweaks.Ore.HarvesterPlainWork * 4));
        int mined = oreBefore - OreAround(s, Tweaks.Machine.HarvestRange);
        Assert.True(mined >= 3, $"the harvester should have mined a few cells. mined={mined}");
        Assert.True(mined <= 6, $"no more than a cell per {Tweaks.Ore.HarvesterPlainWork} actions. mined={mined}");
        Assert.Equal(storeBefore + mined, s.Base.Store.Ore);
    }

    [Fact]
    public void Harvester_StartsACellFromNothing_WhenTheOneItWasWorkingIsTaken()
    {
        var (s, harvester) = Networked();
        int partial = Tweaks.Ore.HarvesterPlainWork - 1;
        for (int i = 0; i < HarvestTicks(partial) && harvester.OreWork < partial; i++)
            s.World.Advance(_ => default);
        Assert.Equal(partial, harvester.OreWork);
        var taken = Assert.NotNull(harvester.OreWorkCell);
        int storeBefore = s.Base.Store.Ore;

        s.World.Terrain.SetPixel(taken, TerrainPixel.Blank); // the tank, or another harvester, got there first
        for (int i = 0; i < HarvestTicks(1) && harvester.OreWorkCell == taken; i++)
            s.World.Advance(_ => default);

        Assert.NotEqual(taken, harvester.OreWorkCell);
        Assert.Equal(1, harvester.OreWork);
        Assert.Equal(storeBefore, s.Base.Store.Ore);
    }

    [Fact]
    public void Harvester_LeavesACoreCellToTheTank_AndSaysSo()
    {
        var s = RelayHeatTests.CreateScenario();
        SeedOre(s, depth: 1, length: 1, core: true);
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * 3));
        var harvester = PlantHarvester(s);
        int storeBefore = s.Base.Store.Ore;
        double charge = harvester.Charge;

        // Long enough for any mining to have finished.
        Advance(s, HarvestTicks(20));

        Assert.Equal(storeBefore, s.Base.Store.Ore);
        Assert.Equal(Tweaks.Ore.CoreCellUnits, OreAround(s, Tweaks.Machine.HarvestRange));
        // One core cell beside a linked relay is not hot enough to stall it: it idles for want of plain ore.
        Assert.Equal(HarvesterState.CoreOnly, MachineBehaviors.HarvesterStateOf(harvester));
        Assert.True(harvester.HasNoOre);
        Assert.Equal(1, s.World.GetNetworkStatus(s.Base).HarvestersCoreOnly);
        Assert.Equal(0, s.World.GetNetworkStatus(s.Base).HarvestersWorking);
        Assert.Equal(charge, harvester.Charge);
    }

    [Fact]
    public void Harvester_OffTheNetwork_IdlesAndSpendsNothing()
    {
        var s = RelayHeatTests.CreateScenario();
        SeedOre(s);
        var harvester = PlantHarvester(s); // no relay: the relay position is beyond the base's coverage
        int storeBefore = s.Base.Store.Ore;
        int oreBefore = OreAround(s, Tweaks.Machine.HarvestRange);
        double charge = harvester.Charge;

        Advance(s, HarvestTicks(10));

        Assert.True(harvester.IsOffNetwork);
        Assert.Equal(HarvesterState.OffNetwork, MachineBehaviors.HarvesterStateOf(harvester));
        Assert.Equal(oreBefore, OreAround(s, Tweaks.Machine.HarvestRange));
        Assert.Equal(storeBefore, s.Base.Store.Ore);
        Assert.Equal(charge, harvester.Charge);
        Assert.Equal(1, s.World.GetNetworkStatus(s.Base).HarvestersOffNetwork);
    }

    [Fact]
    public void Harvester_WithNoOreInReach_IdlesAndSpendsNothing_EvenAmongDirt()
    {
        var s = RelayHeatTests.CreateScenario();
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * 3));
        var harvester = PlantHarvester(s);
        double charge = harvester.Charge;

        Advance(s, HarvestTicks(10));

        Assert.False(harvester.IsOffNetwork);
        Assert.True(harvester.HasNoOre);
        Assert.Equal(HarvesterState.NoOre, MachineBehaviors.HarvesterStateOf(harvester));
        Assert.Equal(charge, harvester.Charge);
        Assert.Equal(harvester.Anchor, harvester.Position);
        Assert.Equal(1, s.World.GetNetworkStatus(s.Base).HarvestersNoOre);
    }

    [Fact]
    public void Harvester_CrawlsToTheOre_ButStaysInItsWorkArea()
    {
        var s = RelayHeatTests.CreateScenario();
        SeedOre(s, along: 12, length: 3);
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * 3));
        var harvester = PlantHarvester(s);
        int storeBefore = s.Base.Store.Ore;

        int maxDist2 = 0;
        for (int i = 0; i < Tweaks.Perf.TargetFps * 20; i++)
        {
            s.World.Advance(_ => default);
            maxDist2 = Math.Max(maxDist2, Position.DistanceSquared(harvester.Position, harvester.Anchor));
        }

        Assert.True(maxDist2 > 4, $"the harvester should have crawled away from where it was planted. max dist^2={maxDist2}");
        Assert.True(maxDist2 <= Tweaks.Machine.HarvestRange * Tweaks.Machine.HarvestRange, "it never leaves its work area");
        Assert.True(s.Base.Store.Ore > storeBefore, "and mined the ore it crawled to");
        Assert.Null(harvester.LinkPoint);
    }

    [Fact]
    public void Harvester_IdlesWhileItsBaseStoreIsFull()
    {
        var (s, harvester) = Networked();
        s.Base.Store.Ore = s.Base.Store.Capacity;
        int oreBefore = OreAround(s, Tweaks.Machine.HarvestRange);
        double charge = harvester.Charge;

        Advance(s, HarvestTicks(10));

        Assert.Equal(oreBefore, OreAround(s, Tweaks.Machine.HarvestRange));
        Assert.Equal(charge, harvester.Charge);
    }

    /// <summary>
    /// Runs a networked harvester among plenty of ore for a minute of game time, its node a relay (which pumps heat
    /// away) or a pylon (which does not), and returns (ore mined, heat left in the ground).
    /// </summary>
    private static (int Mined, double HeatLeft) RunHarvester(MachineType node)
    {
        var s = RelayHeatTests.CreateScenario();
        SeedOre(s, depth: 8, length: 21);
        var nodeMachine = new Machine(s.RelayPos + new Offset(0, s.DirY * 3), node, s.Tank.Color);
        s.World.Machines.Add(nodeMachine);
        s.World.PlantMachine(nodeMachine, s.Base);
        PlantHarvester(s);

        int storeBefore = s.Base.Store.Ore;
        double heatBefore = s.World.SumSystemThermalEnergy();
        Advance(s, Tweaks.Perf.TargetFps * 60);
        return (s.Base.Store.Ore - storeBefore, s.World.SumSystemThermalEnergy() - heatBefore);
    }

    [Trait("Category", "Slow")]
    [Fact]
    public void Harvester_WithoutACoolingPath_StallsAndKeepsItsHeatBounded()
    {
        var (mined, heatLeft) = RunHarvester(MachineType.Pylon);

        double unthrottled = 60.0 * 1000.0 / Tweaks.Machine.HarvesterIntervalMs / Tweaks.Ore.HarvesterPlainWork;
        Assert.True(mined > 0, "it should still mine until the ground gets hot");
        Assert.True(mined < unthrottled * 0.7, $"a harvester with nowhere to put its heat must slow down. mined={mined}, unthrottled={unthrottled:0}");
        double worked = mined * (double)Tweaks.Ore.HarvesterPlainWork * Tweaks.Machine.HarvesterHeatPerHarvest;
        Assert.True(heatLeft < worked + 1.0, "heat in the ground never exceeds what the work made");
    }

    [Trait("Category", "Slow")]
    [Fact]
    public void Harvester_WithALinkedRelay_KeepsWorkingBecauseTheHeatLeaves()
    {
        var alone = RunHarvester(MachineType.Pylon);
        var piped = RunHarvester(MachineType.Relay);

        Assert.True(piped.Mined > alone.Mined, $"a relay should let the harvester work faster. piped={piped.Mined}, alone={alone.Mined}");
        double pipedPerUnit = piped.HeatLeft / piped.Mined, alonePerUnit = alone.HeatLeft / Math.Max(1, alone.Mined);
        Assert.True(pipedPerUnit < alonePerUnit,
            $"a relay should carry heat away. heat per unit piped={pipedPerUnit:0.0}, alone={alonePerUnit:0.0}");
    }

    [Fact]
    public void Harvester_Heat_IsBookedInTheLedger_SoTheEnergyBalanceHolds()
    {
        var (s, _) = Networked();

        double start = s.World.SumSystemThermalEnergy();
        double startNet = s.World.Terrain.Ledger.NetExternal;
        double maxResidual = 0.0;
        for (int i = 0; i < Tweaks.Perf.TargetFps * 20; i++)
        {
            s.World.Advance(_ => default);
            double residual = (s.World.SumSystemThermalEnergy() - start) - (s.World.Terrain.Ledger.NetExternal - startNet);
            maxResidual = Math.Max(maxResidual, Math.Abs(residual));
        }

        Assert.True(s.World.Terrain.Ledger.ConvertedToCharge < 0, "harvesting should have turned battery charge into heat");
        Assert.True(maxResidual < 0.5, $"harvest heat must be accounted for. residual={maxResidual:0.0000}");
    }

    [Fact]
    public void Harvester_OnHotGround_ReportsStalledInItsRenderState()
    {
        var (s, harvester) = Networked();
        s.World.Terrain.AddHeatRadius(s.RelayPos, 200, 6);

        Advance(s, HarvestTicks(1));

        var states = new Tunnerer.Core.Rendering.MachineRenderState[8];
        int count = s.World.Machines.CopyRenderStates(states);
        Assert.True(harvester.IsStalled, "a harvester on very hot ground should be stalled");
        Assert.Contains(states.Take(count), m => m.Type == MachineType.Harvester && m.IsStalled);
    }

    [Fact]
    public void SecondaryBuildKey_BuildsCarriesAndPlantsAHarvester_PaidFromTheHoldOffNetwork()
    {
        var s = RelayHeatTests.CreateScenario();
        s.Tank.Hold.Add(Tweaks.Ore.HarvesterCost + 5);
        s.Tank.Position = s.RelayPos;
        var world = s.World;
        int holdBefore = s.Tank.OreHeld, storeBefore = s.Base.Store.Ore;

        void Press(bool down) => world.Advance(i => i == 0 ? new Tunnerer.Core.Input.ControllerOutput { BuildSecondary = down } : default);

        Press(true);
        Assert.True(s.Tank.IsCarryingMachine);
        Assert.Equal(MachineType.Harvester, s.Tank.CarriedMachineType);
        Assert.Equal(holdBefore - Tweaks.Ore.HarvesterCost, s.Tank.OreHeld);
        Assert.Equal(storeBefore, s.Base.Store.Ore);

        Press(false);
        Press(true); // a fresh press plants it where the tank stands

        Assert.False(s.Tank.IsCarryingMachine);
        var harvester = world.Machines.Machines.Single(m => m.Type == MachineType.Harvester);
        Assert.Equal(MachineState.Planted, harvester.State);
        Assert.Same(s.Base, harvester.OwnerBase);
    }
}
