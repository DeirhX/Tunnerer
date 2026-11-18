using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Input;
using Tunnerer.Core.Rendering;
using Tunnerer.Core.Types;
using Tunnerer.Core.Bots;

namespace Tunnerer.Tests;

/// <summary>
/// Golden fingerprints of fixed simulation runs, one of them two bots playing each other.
/// <see cref="DeterminismTests"/> and <see cref="ReplayTests"/> only compare a run with itself; these compare today's output with the output recorded when the constants were set, so
/// a refactor that changes any float in the simulation fails here. The hash covers the raw bits of both temperature
/// fields, the terrain, the energy ledger and totals, tanks, bases, machines and projectiles.
/// </summary>
public class SimulationFingerprintTests
{
    private const string ChangedMessage =
        "Simulation output changed. If intended, update the constant and say so in the commit message (AGENTS.md).";

    // The same on every OS: Core uses DeterministicMath instead of the platform math library. A failure on one OS
    // only means something platform-dependent has crept back into the simulation.
    private const ulong ExpectedPlayerDrivesAndShoots = 0x6F6977DB7613A6EAUL;
    private const ulong ExpectedRelayPylonHarvesterNetwork = 0xE0D2264791DA58EAUL;
    private const ulong ExpectedEnemyMachinesShotToPieces = 0xB6102D1B5B5F1E46UL;
    private const ulong ExpectedBotVsBot = 0x56BB30122CA13DE1UL;

    [Fact]
    public void PlayerDrivesAndShoots_MatchesRecordedFingerprint()
    {
        var world = TestHelpers.CreateSeededWorld();
        for (int frame = 0; frame < 300; frame++)
        {
            var input = new ControllerOutput
            {
                MoveSpeed = new Offset((frame / 40) % 2 == 0 ? 0 : 1, (frame / 40) % 3 == 0 ? -1 : 1),
                ShootPrimary = frame % 7 == 0,
                AimDirection = new DirectionF(1, 0),
            };
            world.Advance(i => i == 0 ? input : TestSimulation.Idle);
        }

        AssertFingerprint(ExpectedPlayerDrivesAndShoots, world);
    }

    [Fact]
    public void RelayPylonHarvesterNetwork_MatchesRecordedFingerprint()
    {
        var s = RelayHeatTests.CreateScenario();
        s.World.Terrain.AddHeatRadius(s.RelayPos, 150, 8);
        RelayHeatTests.PlantRelay(s);
        Plant(s, MachineType.Pylon, Tweaks.Base.BaseSize / 2 + 6);
        // Ore beside the harvester, which stands in the base's coverage, so it mines into the store.
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = 3; dx <= 5; dx++)
                s.World.Terrain.SetPixel(s.Base.Position + new Offset(dx, s.DirY * (Tweaks.Base.BaseSize / 2 + 18 + dy)),
                    Tunnerer.Core.Terrain.TerrainPixel.Emberite);
        Plant(s, MachineType.Harvester, Tweaks.Base.BaseSize / 2 + 18);
        int storeBefore = s.Base.Store.Ore;

        for (int frame = 0; frame < 400; frame++)
        {
            var input = new ControllerOutput
            {
                MoveSpeed = new Offset(0, frame < 200 ? s.DirY : -s.DirY),
                ShootPrimary = frame % 11 == 0,
                AimDirection = new DirectionF(0, s.DirY),
            };
            s.World.Advance(i => i == 0 ? input : TestSimulation.Idle);
        }

        Assert.True(s.Base.Store.Ore > storeBefore, "the scenario must have the harvester mine ore");
        AssertFingerprint(ExpectedRelayPylonHarvesterNetwork, s.World);
    }

    [Fact]
    public void BotVsBot_MatchesRecordedFingerprint()
    {
        var world = TestHelpers.CreateSeededWorld();
        var tanks = world.TankList.Tanks;
        var bots = new[] { new BotTankAI(seed: 11), new BotTankAI(seed: 22) };
        for (int frame = 0; frame < 1200; frame++)
            world.Advance(i => bots[i].GetInput(tanks[i], tanks[1 - i], world));

        AssertFingerprint(ExpectedBotVsBot, world);
    }

    [Fact]
    public void EnemyMachinesShotToPieces_MatchesRecordedFingerprint()
    {
        // The player shoots down the tunnel into an enemy relay and harvester: hits, wrecks, scorch, wreck shrapnel
        // and the network rebuilding all feed the hash.
        var s = RelayHeatTests.CreateScenario();
        var enemy = s.World.TankBases.Bases.First(b => b.Color != s.Tank.Color);
        var relay = new Machine(s.RelayPos, MachineType.Relay, enemy.Color);
        var harvester = new Machine(s.RelayPos + new Offset(0, s.DirY * 10), MachineType.Harvester, enemy.Color);
        foreach (var machine in new[] { relay, harvester })
        {
            s.World.Machines.Add(machine);
            s.World.PlantMachine(machine, enemy);
        }

        for (int frame = 0; frame < 400; frame++)
        {
            var input = new ControllerOutput
            {
                MoveSpeed = new Offset(0, frame < 60 ? s.DirY : 0),
                ShootPrimary = frame % 5 == 0,
                AimDirection = new DirectionF(0, s.DirY),
            };
            s.World.Advance(i => i == 0 ? input : TestSimulation.Idle);
        }

        Assert.False(relay.IsAlive, "the scenario must wreck at least the relay");
        AssertFingerprint(ExpectedEnemyMachinesShotToPieces, s.World);
    }

    private static void Plant(RelayHeatTests.Scenario s, MachineType type, int distanceFromBase)
    {
        var machine = new Machine(s.Base.Position + new Offset(0, s.DirY * distanceFromBase), type, s.Tank.Color);
        s.World.Machines.Add(machine);
        s.World.PlantMachine(machine, s.Base);
    }

    private static void AssertFingerprint(ulong expected, World world)
    {
        ulong actual = Fingerprint(world);
        Assert.True(expected == actual, $"{ChangedMessage} Expected 0x{expected:X16}UL, got 0x{actual:X16}UL.");
    }

    internal static ulong Fingerprint(World world)
    {
        var h = new Fnv64();
        h.Add(WorldStateHash.Compute(world));

        var terrain = world.Terrain;
        int area = terrain.Width * terrain.Height;
        for (int i = 0; i < area; i++)
        {
            h.Add(terrain.GetHeatTemperature(i));
            h.Add(terrain.GetAirTemperature(i));
        }

        var ledger = terrain.Ledger;
        h.Add(ledger.Injected);
        h.Add(ledger.SunkFixedTemperature);
        h.Add(ledger.SunkAmbientLoss);
        h.Add(ledger.SunkBase);
        h.Add(ledger.SunkTank);
        h.Add(ledger.ConvertedToCharge);
        h.Add(world.SumSystemThermalEnergy());
        h.Add(world.SumSystemCharge());

        foreach (var tank in world.TankList.Tanks)
        {
            h.Add(tank.Position.X);
            h.Add(tank.Position.Y);
            h.Add(tank.Direction);
            h.Add(tank.Heat);
            h.Add(tank.OffNetworkFloor);
            h.Add(tank.Reactor.Current.Heat.Value);
            h.Add(tank.Reactor.Current.Health.Value);
            h.Add(tank.LivesLeft);
        }

        foreach (var tankBase in world.TankBases.Bases)
        {
            h.Add(tankBase.Charge);
            h.Add(tankBase.ReactorHeatEnergy);
            h.Add(tankBase.Store.Ore);
        }

        foreach (var machine in world.Machines.Machines)
        {
            h.Add((int)machine.Type);
            h.Add((int)machine.State);
            h.Add(machine.IsAlive ? 1 : 0);
            h.Add(machine.Position.X);
            h.Add(machine.Position.Y);
            h.Add(machine.Charge);
            h.Add(machine.LastPumpedEnergy);
            h.Add(machine.LastChargeGiven);
            h.Add(machine.Reactor.Current.Heat.Value);
            h.Add(machine.Reactor.Current.Health.Value);
        }

        var projectiles = new ProjectileRenderState[world.Projectiles.Count];
        int count = world.Projectiles.CopyRenderStates(projectiles);
        h.Add(world.Projectiles.Count);
        for (int i = 0; i < count; i++)
        {
            var p = projectiles[i];
            h.Add(p.Position.X);
            h.Add(p.Position.Y);
            h.Add(p.Velocity.X);
            h.Add(p.Velocity.Y);
            h.Add((int)p.Type);
            h.Add(p.IsAlive ? 1 : 0);
        }

        return h.Value;
    }

    /// <summary>FNV-1a over the raw bits of each value: stable across runs, builds and platforms.</summary>
    private struct Fnv64()
    {
        public ulong Value { get; private set; } = 14695981039346656037UL;

        public void Add(ulong bits)
        {
            for (int i = 0; i < 8; i++)
            {
                Value ^= (bits >> (i * 8)) & 0xFF;
                Value *= 1099511628211UL;
            }
        }

        public void Add(int value) => Add((ulong)(uint)value);
        public void Add(float value) => Add((ulong)(uint)BitConverter.SingleToInt32Bits(value));
        public void Add(double value) => Add((ulong)BitConverter.DoubleToInt64Bits(value));
    }
}
