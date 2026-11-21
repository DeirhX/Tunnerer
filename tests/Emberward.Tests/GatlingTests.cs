using Emberward.Core;
using Emberward.Core.Collision;
using Emberward.Core.Config;
using Emberward.Core.Entities.Emberites;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Projectiles;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Input;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using static Emberward.Tests.SnapshotRoundTrip;

namespace Emberward.Tests;

/// <summary>
/// The gatling (DESIGN.md §6.2, <see cref="TurretAction"/>): a turret of the energy network whose light rounds are for
/// emberites. It shoots the nearest creature out of its nest, ahead of where it runs and through a little soil, and an
/// enemy tank only when it sees no creature; its rounds burst on nothing; it is powered, heated and upgraded as a turret
/// is; and it costs nothing more than a turret while no creature is out.
/// </summary>
public class GatlingTests
{
    private const int YardHalfWidth = 30;
    private const int YardNear = 3, YardFar = 48;

    /// <summary>
    /// The relay scenario with the energy network, upgrades and the swarm, and an open yard in front of the base: 61
    /// cells wide, from 3 to 48 cells past the base wall.
    /// </summary>
    private static RelayHeatTests.Scenario Scenario()
    {
        var s = RelayHeatTests.CreateScenario(energyNetwork: true, machineUpgrades: true, emberites: true);
        for (int along = YardNear; along <= YardFar; along++)
            for (int across = -YardHalfWidth; across <= YardHalfWidth; across++)
                s.World.Terrain.SetPixel(Outside(s, along, across), TerrainPixel.Blank);
        return s;
    }

    /// <summary>A spot <paramref name="cells"/> past the base wall along the tunnel and <paramref name="across"/> to its side.</summary>
    private static Position Outside(RelayHeatTests.Scenario s, int cells, int across = 0) =>
        s.Base.Position + new Offset(across, s.DirY * (Tweaks.Base.BaseSize / 2 + 1 + cells));

    private static Machine Plant(RelayHeatTests.Scenario s, MachineType type, Position at)
    {
        var machine = new Machine(at, type, s.Tank.Color);
        s.World.Machines.Add(machine);
        s.World.PlantMachine(machine, s.Base);
        return machine;
    }

    /// <summary>A gatling 8 cells out (covered by the base), with the tank standing 4 cells behind it.</summary>
    private static (RelayHeatTests.Scenario S, Machine Gatling) Post()
    {
        var s = Scenario();
        var gatling = Plant(s, MachineType.Gatling, Outside(s, 8));
        s.Tank.Position = Outside(s, 4);
        return (s, gatling);
    }

    /// <summary>What <see cref="Fight"/> counted.</summary>
    private sealed record Tally(int Fired, int Killed, int Bites, List<StepEvent> Events);

    /// <summary>
    /// Steps <paramref name="ticks"/> times with the tank held hot (by default hot enough that a woken nest sends every
    /// creature that is ready), and counts what happened. A nest stirs for a second before its first group goes.
    /// </summary>
    private static Tally Fight(RelayHeatTests.Scenario s, int ticks, float heat = Tweaks.Emberites.FullHeat)
    {
        var events = new List<StepEvent>();
        for (int t = 0; t < ticks; t++)
        {
            s.Tank.Heat = heat;
            s.World.Advance(_ => default);
            events.AddRange(s.World.StepEvents.ToArray());
        }
        return new Tally(
            events.Count(e => e.Kind == StepEventKind.TurretFired),
            events.Count(e => e.Kind == StepEventKind.EmberiteKilled),
            events.Count(e => e.Kind == StepEventKind.EmberiteBit),
            events);
    }

    /// <summary>The scenario's other tank, standing at <paramref name="at"/> once its respawn shield has gone.</summary>
    private static Tank Enemy(RelayHeatTests.Scenario s, Position at)
    {
        var enemy = s.World.TankList.Tanks[1];
        enemy.Position = at;
        for (int i = 0; i < 20 * Tweaks.Perf.TargetFps && enemy.SpawnProtectionTicks > 0; i++)
            s.World.Advance(_ => default);
        Assert.Equal(0, enemy.SpawnProtectionTicks);
        return enemy;
    }

    private static List<int> FiringTicks(World world, int ticks)
    {
        var fired = new List<int>();
        for (int t = 0; t < ticks; t++)
        {
            world.Advance(_ => default);
            foreach (var e in world.StepEvents)
                if (e.Kind == StepEventKind.TurretFired)
                    fired.Add(t);
        }
        return fired;
    }

    private static int[] Gaps(List<int> ticks) => ticks.Zip(ticks.Skip(1), (a, b) => b - a).ToArray();

    /// <summary>The tank buys path <paramref name="key"/> for the gatling beside it: one press, then a release.</summary>
    private static void Buy(RelayHeatTests.Scenario s, Machine gatling, int key)
    {
        s.Tank.Position = gatling.Position + new Offset(0, -s.DirY * 4);
        s.Base.Store.Ore = Math.Max(s.Base.Store.Ore, 100);
        int before = s.World.MachineUpgradeOf(gatling).Level;
        s.World.Advance(i => i == 0 ? new ControllerOutput { UpgradeMachine = true, Upgrade = key } : default);
        s.World.Advance(_ => default);
        Assert.Equal(before + 1, s.World.MachineUpgradeOf(gatling).Level);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FifthKey_BuildsAGatling_OnlyInEnergyMatches(bool energyNetwork)
    {
        var s = RelayHeatTests.CreateScenario(energyNetwork: energyNetwork);
        s.Tank.Hold.Ore = Tweaks.Gatling.OreCost;
        s.Tank.Position = Outside(s, 8);
        var press = new ControllerOutput { BuildQuinary = true };
        s.World.Advance(i => i == 0 ? press : default);
        Assert.Equal(energyNetwork ? MachineType.Gatling : (MachineType?)null, s.Tank.CarriedMachineType);

        s.World.Advance(_ => default);
        s.World.Advance(i => i == 0 ? press : default);
        Assert.Null(s.Tank.CarriedMachineType);
        if (energyNetwork)
            Assert.True(Assert.Single(s.World.Machines.Machines).IsStanding);
        else
            Assert.Equal(Tweaks.Gatling.OreCost, s.Tank.Hold.Ore);
    }

    [Fact]
    public void AGatling_KillsTheCreaturesThatComeForTheTank()
    {
        var (s, gatling) = Post();
        var nest = s.World.Emberites!.AddNest(Outside(s, 8 + 30), radius: 2, creatures: 6);

        Tally tally = Fight(s, 10 * Tweaks.Perf.TargetFps);

        Assert.True(s.World.MachinePowerOf(gatling)!.Value.IsFed, "the base's coverage links the gatling to its island");
        Assert.All(tally.Events.Where(e => e.Kind == StepEventKind.TurretFired), e =>
        {
            Assert.Equal(gatling.Position, e.Position);
            Assert.Equal(gatling.OwnerColor, e.Color);
            Assert.Equal(MachineType.Gatling, e.Machine);
            Assert.Equal(-1, e.Count);
        });
        Assert.Equal(6, tally.Killed);
        Assert.True(nest.IsCleared, "every creature of the nest should be dead");
        Assert.Equal(0, tally.Bites);
        Assert.Equal(Math.Sign(s.DirY), Math.Sign(gatling.Heading.Y));
        // Rounds burst on nothing: no shrapnel, no impact to hear, no dirt dug.
        Assert.DoesNotContain(tally.Events, e => e.Kind is StepEventKind.Explosion or StepEventKind.BulletImpact);
    }

    /// <summary>A nest of <paramref name="creatures"/> 30 cells beyond a tank held at full heat, with a gatling (of Spin level <paramref name="spin"/>) between them, or none.</summary>
    private static Tally Wave(bool gatling, int creatures, int spin = 0)
    {
        var s = Scenario();
        Machine? gun = gatling ? Plant(s, MachineType.Gatling, Outside(s, 8)) : null;
        for (int i = 0; i < spin; i++)
            Buy(s, gun!, 1);
        s.Tank.Position = Outside(s, 4);
        s.World.Emberites!.AddNest(Outside(s, 8 + 30), radius: 2, creatures: creatures);
        return Fight(s, 14 * Tweaks.Perf.TargetFps);
    }

    [Fact]
    public void ALoneGatling_StopsASmallWaveBeforeItBites()
    {
        // A hot tank makes a nest send every creature that is ready, one every PulseSpacingTicks.
        Tally bare = Wave(gatling: false, creatures: 12), defended = Wave(gatling: true, creatures: 12);

        Assert.Equal(0, bare.Fired);
        Assert.True(bare.Bites >= 10, $"a full-heat pulse should reach the tank: {bare.Bites} bites");
        Assert.Equal(0, defended.Bites);
        Assert.Equal(12, defended.Killed);
    }

    [Fact]
    public void ABigPulse_OutrunsAPlainGatling_ButNotOneWithSpin()
    {
        // It fires every 3 ticks; a pulse of 40 comes one creature every 2. Spin 1 fires every 2 and keeps up.
        Assert.Equal(Tweaks.Emberites.PulseSpacingTicks, MachineUpgrades.GatlingStatsOf(new MachineUpgradeLevel(MachineUpgradePath.Spin, 1)).IntervalTicks);
        Tally plain = Wave(gatling: true, creatures: 40), spun = Wave(gatling: true, creatures: 40, spin: 1);

        Assert.True(plain.Bites >= 10, $"a plain gatling should be outrun: {plain.Bites} bites");
        Assert.Equal(0, spun.Bites);
        Assert.Equal(40, spun.Killed);
    }

    [Fact]
    public void Lead_IsTheTimeARoundTakesToMeetARunningTarget()
    {
        // 10 cells off, crossing at 1.5 a tick, a round at 4: |(10, 1.5 t)| = 4 t.
        float t = TurretAction.Lead(10f, 0f, new VectorF(0f, 1.5f), 4f);
        Assert.Equal(MathF.Sqrt(100f / (16f - 2.25f)), t, 4);
        // Coming straight at the gun it is met sooner, running away later.
        Assert.Equal(10f / 5.5f, TurretAction.Lead(10f, 0f, new VectorF(-1.5f, 0f), 4f), 4);
        Assert.Equal(10f / 2.5f, TurretAction.Lead(10f, 0f, new VectorF(1.5f, 0f), 4f), 4);
        // Standing still, or faster than the round: aim straight at it.
        Assert.Equal(0f, TurretAction.Lead(10f, 0f, default, 4f));
        Assert.Equal(0f, TurretAction.Lead(10f, 0f, new VectorF(5f, 0f), 4f));
    }

    [Fact]
    public void AGatling_HitsCreaturesRunningAcrossItsField()
    {
        // The tank stands far to one side of the yard and the nest to the other, so the creatures cross in front of the
        // gatling instead of running at it: only rounds aimed ahead of them land.
        var s = Scenario();
        Plant(s, MachineType.Gatling, Outside(s, 8));
        s.Tank.Position = Outside(s, 20, across: -28);
        s.World.Emberites!.AddNest(Outside(s, 20, across: 28), radius: 2, creatures: 12);

        Tally tally = Fight(s, 14 * Tweaks.Perf.TargetFps);

        Assert.True(tally.Killed >= 10, $"killed {tally.Killed} of 12 with {tally.Fired} rounds");
        Assert.True(tally.Killed * 2 >= tally.Fired, $"at least every other round should land: {tally.Killed} kills, {tally.Fired} rounds");
    }

    [Fact]
    public void WithNoCreatureInSight_AGatlingFiresAtAnEnemyTank_AndItsRoundsAreLight()
    {
        var s = Scenario();
        var gatling = Plant(s, MachineType.Gatling, Outside(s, 8));
        var enemy = Enemy(s, gatling.Position + new Offset(0, s.DirY * 12));
        int health = enemy.Reactor.Health;

        var events = new List<StepEvent>();
        for (int t = 0; t < 6 * Tweaks.Gatling.FireIntervalTicks; t++)
        {
            s.World.Advance(_ => default);
            events.AddRange(s.World.StepEvents.ToArray());
        }

        var fired = events.Where(e => e.Kind == StepEventKind.TurretFired).ToArray();
        Assert.True(fired.Length >= 4, $"it should fire about once in {Tweaks.Gatling.FireIntervalTicks} ticks, fired {fired.Length}");
        Assert.All(fired, e => Assert.Equal(enemy.Color, e.Count));
        int lost = health - enemy.Reactor.Health;
        Assert.True(lost >= Tweaks.Gatling.Damage, "its rounds should reach the enemy");
        Assert.Equal(0, lost % Tweaks.Gatling.Damage);
        Assert.True(lost <= fired.Length * Tweaks.Gatling.Damage, "a round does its damage and no more: nothing bursts");
        Assert.DoesNotContain(events, e => e.Kind is StepEventKind.Explosion or StepEventKind.BulletImpact);
        Assert.Equal(0L, s.World.Emberites!.CreatureSteps);
    }

    [Fact]
    public void WithNothingToShootAt_AGatlingHoldsFire()
    {
        var (s, _) = Post();
        s.World.Emberites!.AddNest(Outside(s, 8 + 30), radius: 2, creatures: 6);

        // A cool tank wakes no nest: nothing is out, and the gatling spends nothing looking.
        Tally tally = Fight(s, 2 * Tweaks.Perf.TargetFps, heat: 20f);

        Assert.Equal(0, tally.Fired);
        Assert.Equal(0, s.World.Emberites.OutCount);
        Assert.Equal(0L, s.World.Emberites.CreatureSteps);
        Assert.Equal(0, s.World.Projectiles.Count);
    }

    [Theory]
    [InlineData(TerrainPixel.Dirt, 3, true)]
    [InlineData(TerrainPixel.Dirt, 4, false)]
    [InlineData(TerrainPixel.Rock, 1, false)]
    public void ARound_PassesThroughAFewCellsOfSoil_AndNothingHarder(TerrainPixel wall, int thickness, bool reaches)
    {
        Assert.Equal(3, Tweaks.Gatling.SoilPierceCells);
        var s = Scenario();
        var from = Outside(s, 8);
        var enemy = Enemy(s, from + new Offset(0, s.DirY * 16));
        for (int i = 0; i < thickness; i++)
            for (int across = -YardHalfWidth; across <= YardHalfWidth; across++)
                s.World.Terrain.SetPixel(from + new Offset(across, s.DirY * (6 + i)), wall);
        int health = enemy.Reactor.Health;

        // The line a gatling aims along agrees with what its round does.
        Assert.Equal(reaches, Raycaster.HasShotThroughSoil(s.World.Terrain, from, enemy.Position, Tweaks.Gatling.SoilPierceCells));
        s.World.Projectiles.Add(Projectile.CreateRound(from, new VectorF(0f, s.DirY * Tweaks.Gatling.RoundSpeed), s.Tank.Color,
            Tweaks.Gatling.Damage, life: 10, pierce: Tweaks.Gatling.SoilPierceCells));
        for (int t = 0; t < 10; t++)
            s.World.Advance(_ => default);

        Assert.Equal(reaches ? health - Tweaks.Gatling.Damage : health, enemy.Reactor.Health);
        Assert.Equal(0, s.World.Projectiles.Count);
        // The wall is as it was: a round digs nothing.
        for (int i = 0; i < thickness; i++)
            Assert.Equal(wall, s.World.Terrain.GetPixelRaw(from + new Offset(0, s.DirY * (6 + i))));
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void TheAimCheck_DoesNotChargeASoilCellForTheTargetItself(int wallThickness, bool reaches)
    {
        // A creature burrowing in soil is hit where it stands: its own cell is not one the round has to pass through.
        var s = Scenario();
        var from = Outside(s, 8);
        var target = from + new Offset(0, s.DirY * 16);
        for (int i = 0; i < wallThickness; i++)
            s.World.Terrain.SetPixel(from + new Offset(0, s.DirY * (6 + i)), TerrainPixel.Dirt);
        s.World.Terrain.SetPixel(target, TerrainPixel.Dirt);

        Assert.Equal(reaches, Raycaster.HasShotThroughSoil(s.World.Terrain, from, target, Tweaks.Gatling.SoilPierceCells));
    }

    [Fact]
    public void ARoundSpendsOnePierceForACell_AlsoWhenTwoOfItsSamplesInOneCellFallInTwoTicks()
    {
        var s = Scenario();
        var from = Outside(s, 8);
        for (int dy = -45; dy <= 45; dy++)
            for (int dx = -45; dx <= 45; dx++)
                s.World.Terrain.SetPixel(from + new Offset(dx, dy), TerrainPixel.Dirt);
        // Diagonally, four samples of 0.7075 cells a tick: the last of one tick and the first of the next share a cell.
        float step = Tweaks.Gatling.RoundSpeed / 1.4142135f;
        var round = Projectile.CreateRound(from, new VectorF(step, s.DirY * step), s.Tank.Color, Tweaks.Gatling.Damage,
            life: 20, pierce: 40);
        s.World.Projectiles.Add(round);

        for (int t = 1; t <= 8; t++)
        {
            s.World.Advance(_ => default);
            Assert.True(round.IsAlive);
            // Every cell on its diagonal, the cell it started in included, cost one.
            int cells = (int)MathF.Abs(round.Position.X - from.X) + 1;
            Assert.Equal(cells, 40 - round.Pierce);
        }
    }

    [Fact]
    public void ARoundThatHitsNothing_IsSpentPastItsRange()
    {
        var s = Scenario();
        var from = Outside(s, 8);
        var across = new VectorF(Tweaks.Gatling.RoundSpeed, 0f);
        s.World.Projectiles.Add(Projectile.CreateRound(from, across, s.Tank.Color, Tweaks.Gatling.Damage, life: 3, pierce: 0));
        for (int t = 0; t < 3; t++)
            s.World.Advance(_ => default);
        Assert.Equal(1, s.World.Projectiles.Count);
        s.World.Advance(_ => default);
        s.World.Advance(_ => default);
        Assert.Equal(0, s.World.Projectiles.Count);
    }

    [Fact]
    public void AGatling_ShootsACreatureBurrowingJustInsideAWall_ButNotOneDeepInTheGround()
    {
        var s = Scenario();
        var gatling = Plant(s, MachineType.Gatling, Outside(s, 8));
        var swarm = s.World.Emberites!;
        // Fill the far half of the yard with dirt, and wake a nest in it: its creatures burrow toward the tank.
        for (int along = 20; along <= YardFar; along++)
            for (int across = -YardHalfWidth; across <= YardHalfWidth; across++)
                s.World.Terrain.SetPixel(Outside(s, along, across), TerrainPixel.Dirt);
        s.Tank.Position = Outside(s, 4);
        swarm.AddNest(Outside(s, 26), radius: 0, creatures: 1);

        // Six cells of dirt lie between the creature and the open yard when it comes out: too deep.
        Tally deep = Fight(s, 1);
        for (int t = 0; t < 5 * Tweaks.Perf.TargetFps && swarm.OutCount == 0; t++)
            deep = Fight(s, 1);
        Assert.Equal(1, swarm.OutCount);
        Assert.False(swarm.TryFindTarget(gatling.Position, Tweaks.Gatling.Range, Tweaks.Gatling.SoilPierceCells, out _));
        Assert.Equal(0, deep.Fired);

        // It burrows on, and dies before it is out of the dirt.
        var states = new Emberward.Core.Rendering.EmberiteRenderState[1];
        bool killedInSoil = false;
        for (int t = 0; t < 60 && !killedInSoil; t++)
        {
            Assert.Equal(1, s.World.CopyEmberiteRenderStates(states));
            bool burrowing = states[0].IsBurrowing;
            Tally step = Fight(s, 1);
            if (step.Killed > 0)
                killedInSoil = burrowing;
            else
                Assert.True(burrowing, "it should not have reached the open yard alive");
        }
        Assert.True(killedInSoil, "a round should reach it through the last cells of dirt");
    }

    [Fact]
    public void Firing_HeatsTheGatlingsGround_AndHeatAndChargeStillBalance()
    {
        var (s, gatling) = Post();
        s.World.Emberites!.AddNest(Outside(s, 8 + 30), radius: 2, creatures: 30);
        var ledger = s.World.Terrain.Ledger;
        float before = TurretAction.SenseTemperature(s.World.Terrain, gatling.Position);
        double charge = gatling.Charge;
        // The tank's heat is set by hand before each tick, so each tick is balanced on its own.
        double Total() => s.World.SumSystemThermalEnergy() + s.World.SumSystemCharge();
        double maxResidual = 0.0;
        int fired = 0;
        for (int t = 0; t < 10 * Tweaks.Perf.TargetFps; t++)
        {
            s.Tank.Heat = Tweaks.Emberites.FullHeat;
            double start = Total();
            double startExternal = ledger.Injected - ledger.TotalSunk;
            s.World.Advance(_ => default);
            fired += s.World.StepEvents.ToArray().Count(e => e.Kind == StepEventKind.TurretFired);
            double residual = (Total() - start) - (ledger.Injected - ledger.TotalSunk - startExternal);
            maxResidual = Math.Max(maxResidual, Math.Abs(residual));
        }
        float after = TurretAction.SenseTemperature(s.World.Terrain, gatling.Position);

        Assert.True(fired >= 15, $"it should have kept firing, fired {fired}");
        Assert.True(charge > 0.0 && after > before + 2f, $"its rounds should warm its ground: {before:0.0} -> {after:0.0}");
        Assert.True(maxResidual < 1.0, $"heat and charge must add up. residual={maxResidual:0.0000}");
    }

    [Fact]
    public void AGatlingOfADarkBase_HoldsFire_UntilTheBaseRestarts()
    {
        var (s, gatling) = Post();
        s.World.Emberites!.AddNest(Outside(s, 8 + 30), radius: 2, creatures: 40);
        s.Base.DrawCharge(s.Base.Charge);

        Assert.Equal(0, Fight(s, 24).Fired);
        Assert.True(s.World.IsBaseDark(s.Base));

        s.Base.StoreCharge(Tweaks.Base.RestartCharge);
        Assert.True(Fight(s, 24).Fired > 0);
        Assert.True(s.World.Energy!.IsOnIsland(gatling));
    }

    [Fact]
    public void Spin_FiresFaster_AndEachRoundSpendsMore()
    {
        var plain = MachineUpgrades.GatlingStatsOf(default);
        var spin1 = MachineUpgrades.GatlingStatsOf(new MachineUpgradeLevel(MachineUpgradePath.Spin, 1));
        var spin2 = MachineUpgrades.GatlingStatsOf(new MachineUpgradeLevel(MachineUpgradePath.Spin, 2));
        Assert.Equal((3, 2, 1), (plain.IntervalTicks, spin1.IntervalTicks, spin2.IntervalTicks));
        Assert.True(plain.ShotCharge < spin1.ShotCharge && spin1.ShotCharge < spin2.ShotCharge);
        Assert.Equal((plain.Range, plain.Damage, plain.SoilPierce), (spin2.Range, spin2.Damage, spin2.SoilPierce));
        Assert.Equal(plain, MachineUpgrades.GunStatsOf(MachineBehaviors.Gatling, default));
        Assert.Equal(MachineUpgrades.TurretStatsOf(default), MachineUpgrades.GunStatsOf(MachineBehaviors.Turret, default));

        var s = Scenario();
        var gatling = Plant(s, MachineType.Gatling, Outside(s, 8));
        Enemy(s, gatling.Position + new Offset(0, s.DirY * 12));
        Assert.All(Gaps(FiringTicks(s.World, 5 * plain.IntervalTicks)), gap => Assert.Equal(plain.IntervalTicks, gap));
        Buy(s, gatling, 1);
        Buy(s, gatling, 1);
        Assert.Equal(new MachineUpgradeLevel(MachineUpgradePath.Spin, 2), s.World.MachineUpgradeOf(gatling));
        int[] gaps = Gaps(FiringTicks(s.World, 5 * plain.IntervalTicks));
        Assert.NotEmpty(gaps);
        Assert.All(gaps, gap => Assert.Equal(spin2.IntervalTicks, gap));
    }

    [Fact]
    public void Scope_ReachesFurther_AndItsRoundsPassThroughMoreSoil()
    {
        var plain = MachineUpgrades.GatlingStatsOf(default);
        var scope1 = MachineUpgrades.GatlingStatsOf(new MachineUpgradeLevel(MachineUpgradePath.Scope, 1));
        Assert.Equal(plain.Range + Tweaks.Gatling.ScopeRangePerLevel, scope1.Range);
        Assert.Equal(plain.SoilPierce + Tweaks.Gatling.ScopePiercePerLevel, scope1.SoilPierce);
        Assert.Equal((plain.IntervalTicks, plain.ShotCharge, plain.Damage), (scope1.IntervalTicks, scope1.ShotCharge, scope1.Damage));

        // Beyond its plain range it does not fire; with Scope it does.
        var s = Scenario();
        var gatling = Plant(s, MachineType.Gatling, Outside(s, 8));
        var enemy = Enemy(s, gatling.Position + new Offset(0, s.DirY * (Tweaks.Gatling.Range + 3)));
        Assert.Empty(FiringTicks(s.World, 3 * Tweaks.Turret.ScanIntervalTicks));
        Buy(s, gatling, 2);
        int health = enemy.Reactor.Health;
        Assert.NotEmpty(FiringTicks(s.World, 4 * Tweaks.Turret.ScanIntervalTicks));
        Assert.True(enemy.Reactor.Health < health, "its rounds should reach the enemy");
    }

    [Fact]
    public void AGatlingMidFight_SurvivesASnapshot()
    {
        RelayHeatTests.Scenario Fresh()
        {
            var s = Scenario();
            s.World.Emberites!.AddNest(Outside(s, 8 + 30), radius: 2, creatures: 30);
            return s;
        }
        var live = Fresh();
        var gatling = Plant(live, MachineType.Gatling, Outside(live, 8));
        live.Tank.Position = Outside(live, 4);
        Buy(live, gatling, 2);
        Tally before = Fight(live, 40);
        Assert.True(before.Fired > 0 && live.World.Emberites!.OutCount > 0 && live.World.Projectiles.Count > 0,
            "the snapshot should be taken with creatures out and rounds in the air");

        AssertPlaysOnAlike(live.World, [null, null], () => Fresh().World, ticks: 60,
            (_, _) => default, before: (w, _) => w.TankList.Tanks[0].Heat = 80f);
    }
}
