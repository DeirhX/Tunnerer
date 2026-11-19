using Emberward.Core;
using Emberward.Core.Bots;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Match;
using Emberward.Core.Types;

namespace Emberward.Tests;

/// <summary>
/// The sync digest network clients compare (<see cref="WorldStateHash.ComputeSync"/>) sees differences the state hash
/// does not read, each in the part that names it (docs/late-join-design.md §10).
/// </summary>
public class SyncDigestTests
{
    [Fact]
    public void Heat_SeesWhereHeatIs_NotOnlyHowMuch()
    {
        var a = TestHelpers.CreateSeededWorld();
        var b = TestHelpers.CreateSeededWorld();
        var (first, second) = TwoCellsOfOneMaterial(a);

        a.Terrain.AddHeat(first, 5);
        b.Terrain.AddHeat(second, 5);

        // The same heat in cells of the same material: the totals agree, which is all the old digest compared.
        Assert.Equal(a.SumSystemThermalEnergy(), b.SumSystemThermalEnergy(), 6);
        SyncDigest digestA = WorldStateHash.ComputeSync(a), digestB = WorldStateHash.ComputeSync(b);
        Assert.Equal(digestA.State, digestB.State);
        Assert.NotEqual(digestA.Heat, digestB.Heat);
        Assert.NotEqual(digestA.Combined, digestB.Combined);
    }

    [Fact]
    public void Charge_SeesChargeMovedFromOneBatteryToAnother()
    {
        var a = WorldWithTwoBatteries(out _, out _);
        var b = WorldWithTwoBatteries(out var from, out var to);

        from.Charge -= 1.0;
        to.Charge += 1.0;

        Assert.Equal(a.SumSystemCharge(), b.SumSystemCharge());
        SyncDigest digestA = WorldStateHash.ComputeSync(a), digestB = WorldStateHash.ComputeSync(b);
        Assert.Equal(digestA.State, digestB.State);
        Assert.NotEqual(digestA.Charge, digestB.Charge);
    }

    [Fact]
    public void Economy_SeesWhoPlaysABase()
    {
        var world = TestHelpers.CreateSeededWorld();
        SyncDigest before = WorldStateHash.ComputeSync(world);

        world.SetBotControlled(1, botControlled: true);

        SyncDigest after = WorldStateHash.ComputeSync(world);
        Assert.Equal(before.State, after.State);
        Assert.NotEqual(before.Economy, after.Economy);
    }

    [Fact]
    public void Outcome_SeesProgressTowardsTheCore()
    {
        var world = CreateWorldWithCore();
        SyncDigest before = WorldStateHash.ComputeSync(world);

        typeof(CoreTap).GetProperty(nameof(CoreTap.HoldSeconds))!
            .SetValue(world.CoreTapOf(world.TankList.Tanks[0].Base), 1.5);

        SyncDigest after = WorldStateHash.ComputeSync(world);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Economy, after.Economy);
        Assert.NotEqual(before.Outcome, after.Outcome);
    }

    [Fact]
    public void Latent_SeesAHarvestersMemory()
    {
        var a = WorldWithTwoBatteries(out _, out _);
        var b = WorldWithTwoBatteries(out var harvester, out _);

        harvester.Heading = new Offset(1, 0);

        SyncDigest digestA = WorldStateHash.ComputeSync(a), digestB = WorldStateHash.ComputeSync(b);
        Assert.Equal(digestA.State, digestB.State);
        Assert.NotEqual(digestA.Latent, digestB.Latent);
    }

    [Fact]
    public void Bots_SeesWhatABotRemembers()
    {
        var world = TestHelpers.CreateSeededWorld();
        var tanks = world.TankList.Tanks;
        BotTankAI?[] fresh = [new BotTankAI(seed: 11), new BotTankAI(seed: 22)];
        BotTankAI?[] asked = [new BotTankAI(seed: 11), new BotTankAI(seed: 22)];
        Assert.Equal(WorldStateHash.ComputeSync(world, fresh), WorldStateHash.ComputeSync(world, asked));

        // Asking a bot for its input changes only the bot: its plan, its clocks and its random stream.
        asked[0]!.GetInput(tanks[0], tanks[1], world);

        SyncDigest before = WorldStateHash.ComputeSync(world, fresh), after = WorldStateHash.ComputeSync(world, asked);
        Assert.Equal(before.State, after.State);
        Assert.NotEqual(before.Bots, after.Bots);
    }

    [Fact]
    public void TwoWorldsSteppedAlike_HaveTheSameDigest_EveryPart()
    {
        var a = CreateWorldWithCore();
        var b = CreateWorldWithCore();

        TestSimulation.AdvanceIdle(a, frames: 48);
        TestSimulation.AdvanceIdle(b, frames: 48);

        Assert.Equal(WorldStateHash.ComputeSync(a), WorldStateHash.ComputeSync(b));
    }

    private static (Position First, Position Second) TwoCellsOfOneMaterial(World world)
    {
        var first = new Position(100, 100);
        var pixel = world.Terrain.GetPixel(first);
        for (int x = first.X + 20; x < world.Terrain.Width - 20; x++)
        {
            var candidate = new Position(x, first.Y);
            if (world.Terrain.GetPixel(candidate) == pixel)
                return (first, candidate);
        }
        throw new InvalidOperationException("No second cell of the same material on the row.");
    }

    private static World WorldWithTwoBatteries(out Machine first, out Machine second)
    {
        var world = TestHelpers.CreateSeededWorld();
        int cx = world.Terrain.Width / 2, cy = world.Terrain.Height / 2;
        first = new Machine(new Position(cx - 10, cy), MachineType.Harvester, ownerColor: 0)
        {
            State = MachineState.Planted,
            Charge = 10.0,
        };
        second = new Machine(new Position(cx + 10, cy), MachineType.Harvester, ownerColor: 0)
        {
            State = MachineState.Planted,
            Charge = 10.0,
        };
        world.Machines.Add(first);
        world.Machines.Add(second);
        return world;
    }

    private static World CreateWorldWithCore()
    {
        var size = new Size(480, 300);
        var (terrain, spawns) = new Core.LevelGen.ToastGenerator().Generate(size, seed: 42, playerCount: 2);
        var world = new World(size, simulationSeed: 42, new MatchRules { HotRockPockets = 3, CoreTapWins = true });
        world.Initialize(terrain, spawns, decorationSeed: 43);
        Assert.NotNull(world.CorePocket);
        return world;
    }
}
