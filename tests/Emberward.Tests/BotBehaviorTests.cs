namespace Emberward.Tests;

using Emberward.Core;
using Emberward.Core.Collision;
using Emberward.Core.Config;
using Emberward.Core.Entities;
using Emberward.Core.Input;
using Emberward.Core.Match;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using Emberward.Core.Bots;
using Emberward.Core.Entities.Tanks;

/// <summary>
/// How the bot gets about: it sees what its hull would meet (not single cells), leaves its base whichever door is
/// blocked, and does not stand pushing on things. The numbers in <see cref="KeepsMoving_AndClosesOnTheEnemy"/> are a
/// ratchet with room to spare, not a tuning target.
/// </summary>
public class BotBehaviorTests
{
    private const int Fps = Tweaks.Perf.TargetFps;

    /// <summary>What a run of the bot (tank 1) against an idle player (tank 0) looked like.</summary>
    private sealed record Run(
        bool LeftBase, int LongestStandstill, int ModeSwitches, int NearestToEnemy, int StartDistance,
        int HardPushedWithoutTorch, int TorchTicks, List<DamageCause> Deaths)
    {
        public override string ToString() =>
            $"leftBase={LeftBase} longestStandstill={LongestStandstill} modeSwitches={ModeSwitches} " +
            $"nearest={NearestToEnemy}/{StartDistance} hardWithoutTorch={HardPushedWithoutTorch} torchTicks={TorchTicks} " +
            $"deaths=[{string.Join(",", Deaths)}]";
    }

    private static Run Play(World world, int seconds, int botSeed, Func<Tank, bool>? stopWhen = null)
    {
        var tanks = world.TankList.Tanks;
        var (player, botTank) = (tanks[0], tanks[1]);
        var bot = new BotTankAI(botSeed);

        int still = 0, longestStill = 0, switches = 0, nearest = int.MaxValue, hardNoTorch = 0, torchTicks = 0;
        int startDistance = Distance(botTank.Position, player.Position);
        var deaths = new List<DamageCause>();
        var lastMode = bot.Mode;
        bool wasDead = false;

        for (int tick = 0; tick < seconds * Fps; tick++)
        {
            var before = botTank.Position;
            ControllerOutput output = default;
            var met = StepObstacle.None;
            world.Advance(i =>
            {
                if (i != 1)
                    return default;
                output = bot.GetInput(botTank, player, world);
                if (output.MoveSpeed != default)
                    met = world.ProbeTankStep(botTank, output.MoveSpeed);
                return output;
            });

            if (bot.Mode != lastMode)
            {
                switches++;
                lastMode = bot.Mode;
            }
            if (botTank.IsDead && !wasDead)
                deaths.Add(botTank.DeathCause);
            wasDead = botTank.IsDead;
            if (output.ShootPrimary)
                torchTicks++;

            bool wantsToMove = output.MoveSpeed != default && !botTank.IsDead;
            if (wantsToMove && botTank.Position == before)
            {
                longestStill = Math.Max(longestStill, ++still);
                if (met == StepObstacle.Hard && !output.ShootPrimary)
                    hardNoTorch++;
            }
            else
            {
                still = 0;
            }

            if (!botTank.IsDead)
                nearest = Math.Min(nearest, Distance(botTank.Position, player.Position));
            if (stopWhen?.Invoke(botTank) == true)
                break;
        }

        return new Run(botTank.HasLeftBase, longestStill, switches, nearest, startDistance, hardNoTorch, torchTicks, deaths);
    }

    private static int Distance(Position a, Position b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    [Fact]
    public void LeavesItsBase_WhenOneDoorFacesRock()
    {
        // Seed 7 puts the bot's base under the top edge of the map: solid rock above its upper door.
        var world = TestHelpers.CreateSeededWorld(seed: 7, enableTerrainRegrowth: false);
        var botBase = world.TankList.Tanks[1].Base!;
        var aboveTheDoor = botBase.Position + new Offset(0, -(Tweaks.Base.BaseSize / 2 + 3));
        Assert.True(Pixel.IsBlockingCollision(world.Terrain.GetPixel(aboveTheDoor)),
            "the scenario needs rock outside the bot's upper door");

        var run = Play(world, seconds: 15, botSeed: 9, stopWhen: tank => tank.HasLeftBase);

        Assert.True(run.LeftBase, $"the bot should drive out through the other door. {run}");
    }

    [Fact]
    public void TorchesItsWayOut_WhenBothDoorsFaceRock()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false);
        var botBase = world.TankList.Tanks[1].Base!;
        int wall = Tweaks.Base.BaseSize / 2;
        foreach (int side in new[] { -1, 1 })
            for (int depth = 2; depth <= 4; depth++)
                for (int dx = -8; dx <= 8; dx++)
                    world.Terrain.SetPixel(botBase.Position + new Offset(dx, side * (wall + depth)), TerrainPixel.Rock);

        var run = Play(world, seconds: 30, botSeed: 9, stopWhen: tank => tank.HasLeftBase);

        Assert.True(run.LeftBase, $"the bot should torch through the rock outside a door. {run}");
        Assert.True(run.TorchTicks > 0, $"only the torch gets through rock. {run}");
        Assert.Equal(0, run.HardPushedWithoutTorch);
    }

    [Fact]
    public void SiegesTheEnemyBase_ThroughItsDoor()
    {
        // Blank map, two bases side by side (as in BasePowerTests). Tank 0 is the enemy, tank 1 the bot.
        var size = new Size(100, 72);
        var ground = new TerrainGrid(size);
        ground.Fill(TerrainPixel.Blank);
        var world = new World(size, rules: new MatchRules { TerrainRegrowth = false });
        world.Initialize(ground, [new Position(25, 36), new Position(75, 36)]);
        var enemy = world.TankList.Tanks[0];
        var bot = world.TankList.Tanks[1];
        var enemyBase = enemy.Base!;

        // The enemy sits out of sight behind a ring of rock, so the bot is in siege mode, not fighting it.
        enemy.Position = new Position(60, 60);
        for (int dy = -3; dy <= 3; dy++)
            for (int dx = -3; dx <= 3; dx++)
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) >= 2)
                    world.Terrain.SetPixel(enemy.Position + new Offset(dx, dy), TerrainPixel.Rock);

        // Above the enemy base's top door, outside the bot's own base (the bot leaves its base at the start).
        bot.Position = enemyBase.Position + new Offset(0, -26);
        var ai = new BotTankAI(9);
        for (int tick = 0; tick < 600 && ai.Mode != TwitchMode.Twitch; tick++)
            ai.GetInput(bot, enemy, world);
        Assert.Equal(TwitchMode.Twitch, ai.Mode);

        // A straight line through the centre runs through both doors and meets no wall, so the bot must aim at a wall
        // beside a door. Its shots should hit the base (a bullet that finds a wall takes armour off it).
        double armourBefore = enemyBase.ArmorFill;
        bool shot = false;
        for (int tick = 0; tick < 120; tick++)
            world.Advance(i =>
            {
                if (i != 1)
                    return default;
                var output = ai.GetInput(bot, enemy, world);
                shot |= output.ShootPrimary;
                return output;
            });
        Assert.True(shot, "the bot should shoot the enemy base from its open door");
        Assert.True(enemyBase.ArmorFill < armourBefore, $"its shots should hit the enemy base's wall. {armourBefore:0.000} -> {enemyBase.ArmorFill:0.000}");
    }

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData(7)]
    [InlineData(42)]
    public void KeepsMoving_AndClosesOnTheEnemy(int seed)
    {
        var world = TestHelpers.CreateSeededWorld(seed: seed);

        var run = Play(world, seconds: 60, botSeed: seed + 2);

        Assert.True(run.LeftBase, $"the bot never left its base. {run}");
        Assert.True(run.LongestStandstill <= 2 * Fps, $"the bot stood pushing on something for over two seconds. {run}");
        Assert.True(run.ModeSwitches <= 60, $"the bot kept changing its mind (one mode change a second). {run}");
        Assert.True(run.NearestToEnemy <= run.StartDistance / 2, $"the bot did not get half way to the enemy. {run}");
        Assert.DoesNotContain(DamageCause.Overheat, run.Deaths);
    }

    [Fact]
    public void GroundHeatAt_IsWhatTheGroundThere_WouldDoToATank()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false);
        var spot = new Position(world.Terrain.Width / 2, world.Terrain.Height / 2);
        float before = world.GroundHeatAt(spot);

        world.Terrain.AddHeatRadius(spot, 120, Tweaks.Tank.DigRadius);

        Assert.True(world.GroundHeatAt(spot) > before + 20f,
            $"heated ground should read hot. before={before:0.0}, after={world.GroundHeatAt(spot):0.0}");
    }
}
