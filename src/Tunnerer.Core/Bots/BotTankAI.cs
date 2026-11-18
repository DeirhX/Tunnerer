namespace Tunnerer.Core.Bots;

using Tunnerer.Core.Collision;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Core.Types;
using Tunnerer.Core.Input;

public enum TwitchMode { Start, ExitBaseUp, ExitBaseDown, Twitch, Return, Recharge }

/// <summary>
/// The computer opponent. This class is its mode machine (leave the base, roam and fight, return, recharge);
/// <see cref="BotNavigator"/> picks its moves and <see cref="BotGunner"/> decides its fire. All three draw from one
/// seeded random stream, so the order of their calls within a tick is part of the bot's behaviour.
/// </summary>
/// <remarks>
/// It drives the tank through the same <see cref="ControllerOutput"/> a player does and learns what is in its way
/// from <see cref="World.ProbeTankStep"/>, the answer the tank's own move gets. When it stops making headway it
/// commits to a short manoeuvre (a sidestep, or a held heading) instead of picking a new move every tick.
/// </remarks>
public class BotTankAI
{
    /// <summary>Distance from the base centre (along Y) at which the bot counts as having left it.</summary>
    private const int OutsideBaseMargin = 5;
    private const int OutsideBase = Tweaks.Base.BaseSize / 2 + OutsideBaseMargin;

    /// <summary>Distances from the base centre, out through a door, at which the bot feels how hot the ground is.</summary>
    private static readonly int[] ExitHeatCheckDistances = { Tweaks.Base.BaseSize / 2 + 4, OutsideBase + 6 };

    private const int ExitStuckThresholdFrames = 10;
    private const int GeneralStuckThresholdFrames = 7;
    /// <summary>
    /// How long the bot keeps at a step that takes time: torching through rock or ore, shooting a machine out of the
    /// way. Only after this does standing still count as stuck.
    /// </summary>
    private const int SlowStepPatienceFrames = 2 * Tweaks.Perf.TargetFps;
    /// <summary>A cold tank acts on only some ticks; the stuck limits stretch with it, down to this speed.</summary>
    private const float SlowestSpeedForStuck = 0.25f;
    private const int UnstuckReplanMinFrames = 4;
    private const int UnstuckReplanMaxFrames = 8;
    /// <summary>Roaming out of the enemy's sight, this long without headway is going nowhere: the bot breaks out.</summary>
    private const int RoamNoProgressLimitFrames = 4 * Tweaks.Perf.TargetFps;
    /// <summary>Heading home, this long around one spot (a wall, an enemy base in the way): the bot takes a detour.</summary>
    private const int ReturnNoProgressLimitFrames = 5 * Tweaks.Perf.TargetFps;
    /// <summary>
    /// Heading home this long without getting any nearer (the door sealed by hot rock, a trail that ends in a dead
    /// end): the bot gives up and roams again, as it can fight and look for another way in from there. A slow tank on
    /// a long way home keeps getting nearer, so it never gives up.
    /// </summary>
    private const int ReturnGiveUpFrames = 20 * Tweaks.Perf.TargetFps;
    /// <summary>How much nearer to the base (cells) counts as headway on the way home.</summary>
    private const int ReturnHeadwayCells = 3;
    /// <summary>
    /// After giving up on a return, low health or heat does not send the bot home again for this long (a kill or
    /// standing in its own base still does), or it would be back at the sealed door on the next tick.
    /// </summary>
    private const int ReturnCooldownFrames = 30 * Tweaks.Perf.TargetFps;
    /// <summary>How long a breakout or detour holds its heading.</summary>
    private const int BreakoutFrames = 2 * Tweaks.Perf.TargetFps;

    /// <summary>
    /// Dirt yields to the plain dig at half speed for no heat. The bot spends heat on torching it only while it has
    /// at least this share of its heat budget left.
    /// </summary>
    private const float TorchDirtMinHeatBudgetFraction = 0.75f;

    /// <summary>
    /// Torching rock or ore heats the ground the tank stands in more than anything else it does. Roaming, the bot
    /// goes around them; leaving the base, heading home or breaking out it cuts through, but only with at least this
    /// share of its heat budget left.
    /// </summary>
    private const float TorchRockMinHeatBudgetFraction = 0.5f;

    /// <summary>Roaming ends and the bot heads home below this health (heat limits are in <see cref="Tweaks.Bot"/>).</summary>
    private const int ReturnBelowHealth = 500;

    private readonly BotRandom _rng;
    private readonly BotProfileCycle _profiles = new();
    private readonly BotNavigator _navigator;
    private readonly BotGunner _gunner;
    private readonly BotTrail _trail = new();
    private TwitchMode _mode = TwitchMode.Start;
    private Offset _move;
    private bool _shoot;
    /// <summary>Whether the enemy was in sight when the current roaming plan was made.</summary>
    private bool _planHadLineOfSight;
    private bool _torchDirt;
    private int _timeToChange;
    /// <summary>Ticks since this return last got nearer to the base; <see cref="ReturnGiveUpFrames"/> ends it.</summary>
    private int _returnStallFrames;
    /// <summary>The nearest to the base (cells, along either axis) this return has been.</summary>
    private int _returnNearestCells = int.MaxValue;
    /// <summary>Ticks left in which low health or heat does not send the bot home (see <see cref="ReturnCooldownFrames"/>).</summary>
    private int _returnCooldownFrames;
    private DirectionF _aimDirection;
    private Offset _maneuverMove;
    private int _maneuverFrames;
    /// <summary>Whether the running manoeuvre is a breakout (which ends when the enemy comes into sight).</summary>
    private bool _maneuverIsBreakout;
    private float _rechargeLowestHeat = float.MaxValue;
    private int _rechargeStallFrames;

    public BotTankAI(int seed)
    {
        _rng = new BotRandom(seed);
        _navigator = new BotNavigator(_rng);
        _gunner = new BotGunner(_rng);
    }

    /// <summary>The mode the bot is in; for tests and diagnostics.</summary>
    public TwitchMode Mode => _mode;

    /// <summary>
    /// The tank a bot in slot <paramref name="self"/> fights: the nearest other tank that is alive, or the nearest
    /// other tank when none is. Ties go to the lower slot. Null when it is alone.
    /// </summary>
    public static Tank? PickEnemy(IReadOnlyList<Tank> tanks, int self)
    {
        Tank? best = null;
        long bestRank = long.MaxValue;
        for (int i = 0; i < tanks.Count; i++)
        {
            if (i == self)
                continue;
            // A dead tank ranks behind every live one, whatever the distance.
            long rank = Position.DistanceSquared(tanks[i].Position, tanks[self].Position)
                + (tanks[i].IsDead ? (long)int.MaxValue + 1 : 0);
            if (rank < bestRank)
            {
                best = tanks[i];
                bestRank = rank;
            }
        }
        return best;
    }

    public ControllerOutput GetInput(Tank tank, Tank? enemy, World world)
    {
        _profiles.Advance();
        if (_gunner.BeginTick(enemy) && _mode == TwitchMode.Twitch)
            SetMode(TwitchMode.Return); // After a kill the bot goes home instead of waiting at the enemy's base exit.
        _gunner.UpdateLineOfSight(tank, enemy, world.Terrain);

        if (tank.IsDead)
        {
            // It respawns in its base, whole and cold: it starts over from there.
            SetMode(TwitchMode.Start);
            _returnCooldownFrames = 0;
            return default;
        }

        if (_returnCooldownFrames > 0)
            _returnCooldownFrames--;
        if (_mode is TwitchMode.ExitBaseUp or TwitchMode.ExitBaseDown or TwitchMode.Twitch)
            _trail.Record(tank.Position);

        var basePos = tank.Base?.Position ?? tank.Position;
        // Roaming, headway is toward the enemy. Heading home it is just moving on: the trail may wind.
        _navigator.UpdateStuckState(tank.Position,
            _mode == TwitchMode.Twitch && enemy is { IsDead: false } ? enemy.Position : null);
        if (_maneuverFrames > 0)
        {
            // A breakout is for roaming out of sight; once the enemy is seen the bot fights, which a manoeuvre's
            // plain move would not.
            if (_maneuverIsBreakout && _mode == TwitchMode.Twitch && _gunner.EnemyInSight)
                _maneuverFrames = 0;
            else
                return ContinueManeuver(tank, world);
        }

        var relX = tank.Position.X - basePos.X;
        var relY = tank.Position.Y - basePos.Y;

        return _mode switch
        {
            TwitchMode.Start => DoStart(tank, world),
            TwitchMode.ExitBaseUp => DoExit(tank, world, exitY: -1, relX, relY),
            TwitchMode.ExitBaseDown => DoExit(tank, world, exitY: 1, relX, relY),
            TwitchMode.Twitch => DoTwitch(tank, enemy, world),
            TwitchMode.Return => DoReturn(tank, world, relX, relY),
            TwitchMode.Recharge => DoRecharge(tank, relX, relY),
            _ => default,
        };
    }

    /// <summary>Inside its own base's walls. A tank without a base counts as inside (it is at its own "base").</summary>
    private static bool IsInsideOwnBase(Tank tank) => tank.Base?.IsInside(tank.Position) ?? true;

    /// <summary>Changes mode. What the bot was stuck on, and any manoeuvre around it, belonged to the old mode.</summary>
    private void SetMode(TwitchMode mode)
    {
        if (mode == _mode)
            return;
        _mode = mode;
        if (mode == TwitchMode.Start)
            _trail.Clear(); // A new outing leaves a new trail.
        _maneuverFrames = 0;
        _returnStallFrames = 0;
        _returnNearestCells = int.MaxValue;
        _navigator.ResetStuck();
        _navigator.ResetProgress();
    }

    private ControllerOutput DoStart(Tank tank, World world)
    {
        SetMode(PickExit(tank, world) < 0 ? TwitchMode.ExitBaseUp : TwitchMode.ExitBaseDown);
        return default;
    }

    /// <summary>
    /// The door to leave by (-1 up, 1 down): the one whose way out is clearer, a coin flip when they are alike. Open
    /// ground and dirt count the same, so the door it dug out last time is not always the one it takes. Scorching
    /// ground outside a door (where it torched through rock, or died) counts like rock.
    /// </summary>
    private int PickExit(Tank tank, World world)
    {
        int coin = _rng.Next(2) == 0 ? -1 : 1;
        if (tank.Base == null)
            return coin;
        int up = WayOutRank(tank.Base.Position, -1, world);
        int down = WayOutRank(tank.Base.Position, 1, world);
        return up == down ? coin : up < down ? -1 : 1;
    }

    /// <summary>The worst the hull meets driving from the base centre out through a door: 0 for nothing or dirt.</summary>
    private static int WayOutRank(Position basePos, int exitY, World world)
    {
        var worst = StepObstacle.None;
        int dir = TankSpriteShape.DirectionOf(new Offset(0, exitY));
        for (int step = 1; step <= OutsideBase + 1; step++)
        {
            var met = CollisionSolver.TestTankTerrain(world.Terrain, basePos + new Offset(0, exitY * step), dir);
            if (met > worst)
                worst = met;
        }
        foreach (int distance in ExitHeatCheckDistances)
        {
            if (worst < StepObstacle.Hard &&
                world.GroundHeatAt(basePos + new Offset(0, exitY * distance)) >= BotNavigator.ScorchingGround)
                worst = StepObstacle.Hard;
        }
        return worst <= StepObstacle.Dirt ? 0 : (int)worst;
    }

    /// <summary>Drives out of the base through the door in direction <paramref name="exitY"/>; stuck, it tries the other door.</summary>
    private ControllerOutput DoExit(Tank tank, World world, int exitY, int relX, int relY)
    {
        if (relY * exitY > OutsideBase)
        {
            _timeToChange = 0;
            SetMode(TwitchMode.Twitch);
            return default;
        }

        // The door is as wide as the tank: it lines up with the door before driving at it.
        var move = relX != 0 && IsInsideOwnBase(tank) ? new Offset(-Math.Sign(relX), 0) : new Offset(0, exitY);
        if (IsStuck(tank, world, move, ExitStuckThresholdFrames, torchRock: MayTorchRock(tank)))
        {
            SetMode(exitY < 0 ? TwitchMode.ExitBaseDown : TwitchMode.ExitBaseUp);
            return default;
        }

        return MoveOutput(tank, move, world);
    }

    private ControllerOutput DoTwitch(Tank tank, Tank? enemy, World world)
    {
        var profile = _profiles.Active;
        bool hurtOrHot = _returnCooldownFrames <= 0 && (tank.Reactor.Health < ReturnBelowHealth || NeedsToCool(tank));
        if (hurtOrHot || IsInsideOwnBase(tank))
        {
            SetMode(TwitchMode.Return);
            return default;
        }

        bool hasTarget = enemy != null && !enemy.IsDead;
        bool hasLos = _gunner.EnemyInSight;
        var targetPos = hasTarget ? enemy!.Position : (Position?)null;
        if (hasLos)
            _navigator.ResetProgress(); // Circling an enemy in sight is fighting, not going nowhere.

        // A move that has run into rock or a wall is not held to the end of its plan.
        if (world.ProbeTankStep(tank, _move) is StepObstacle.Hard or StepObstacle.Solid)
            _timeToChange = 0;
        // Nor is a plan made in the enemy's sight held once it is lost (or one made out of sight once it is found):
        // the turret was set on the enemy, or along the move, for the other case.
        if (hasLos != _planHadLineOfSight)
            _timeToChange = 0;

        if (_timeToChange <= 0)
        {
            _planHadLineOfSight = hasLos;
            bool retreat = hasLos && ShouldRetreat(tank, enemy!, profile);

            _move = hasLos
                ? _navigator.ChooseCombatMove(tank, enemy!.Position, world, retreat, _move, profile)
                : _navigator.ChooseBestMove(tank, targetPos, world, _move, profile);
            if (_move.X == 0 && _move.Y == 0)
                _move = BotNavigator.MoveCandidates[_rng.Next(BotNavigator.MoveCandidates.Length)];

            (_shoot, _aimDirection) = _gunner.Plan(tank.Position, enemy, hasLos, retreat, _move, profile);
            _torchDirt = _rng.Next(1000) < profile.ShootChanceDigPermille &&
                HeatBudgetFraction(tank) >= TorchDirtMinHeatBudgetFraction;
            _timeToChange = hasLos
                ? _rng.Next(profile.ReplanLosMin, profile.ReplanLosMax)
                : _rng.Next(profile.ReplanTunnelMin, profile.ReplanTunnelMax);
        }

        _timeToChange--;

        if (IsStuck(tank, world, _move, GeneralStuckThresholdFrames, torchRock: false))
            return StartManeuver(tank, world, _navigator.ChooseUnstuckMove(tank, world, MayTorchRock(tank)),
                _rng.Next(UnstuckReplanMinFrames, UnstuckReplanMaxFrames));
        if (!hasLos && _navigator.NoProgressFrames >= RoamNoProgressLimitFrames)
        {
            _navigator.ResetProgress();
            var heading = _navigator.ChooseBreakoutHeading(tank, targetPos, world, MayTorchRock(tank));
            if (heading.X != 0 || heading.Y != 0)
                return StartManeuver(tank, world, heading, BreakoutFrames, isBreakout: true);
        }

        // In the enemy's sight only aimed fire is fired, so the gunner's fairness rules hold. Out of it the torch
        // hurries through dirt and shoots a machine out of the way (the plan already points the turret along the move
        // there); rock is left to a manoeuvre.
        bool aimedFire = _gunner.ApplyBurstLimit(_shoot);
        bool torch = !hasLos && NeedsTorch(world.ProbeTankStep(tank, _move), _torchDirt, torchRock: false);
        return new ControllerOutput
        {
            MoveSpeed = _move,
            ShootPrimary = aimedFire || torch,
            AimDirection = _aimDirection,
        };
    }

    /// <summary>
    /// Heads home: back along the trail it left on the way out, then, where the trail has run out, straight for the
    /// point outside the nearer door.
    /// </summary>
    private ControllerOutput DoReturn(Tank tank, World world, int relX, int relY)
    {
        int cellsFromBase = Math.Max(Math.Abs(relX), Math.Abs(relY));
        if (cellsFromBase < _returnNearestCells - ReturnHeadwayCells)
        {
            _returnNearestCells = cellsFromBase;
            _returnStallFrames = 0;
        }
        else if (++_returnStallFrames > ReturnGiveUpFrames)
        {
            SetMode(TwitchMode.Twitch);
            _timeToChange = 0;
            _returnCooldownFrames = ReturnCooldownFrames;
            return default;
        }

        Offset move;
        if (!IsInsideOwnBase(tank) && _trail.NextHomeward(tank.Position) is Position waypoint)
        {
            move = new Offset(Math.Sign(waypoint.X - tank.Position.X), Math.Sign(waypoint.Y - tank.Position.Y));
        }
        else
        {
            var home = DoReturnMove(tank, relX, relY);
            if (_mode != TwitchMode.Return)
                return home;
            move = home.MoveSpeed;
        }

        // Something it cannot get through on the way (a wall, an enemy base): a detour, then on.
        if (_navigator.NoProgressFrames >= ReturnNoProgressLimitFrames)
        {
            _navigator.ResetProgress();
            var heading = _navigator.ChooseBreakoutHeading(tank, target: null, world, MayTorchRock(tank));
            if (heading.X != 0 || heading.Y != 0)
                return StartManeuver(tank, world, heading, BreakoutFrames, isBreakout: true);
        }
        if (IsStuck(tank, world, move, GeneralStuckThresholdFrames, torchRock: MayTorchRock(tank)))
            return StartManeuver(tank, world,
                _navigator.ChooseUnstuckMove(tank, world, MayTorchRock(tank), preferredYDirection: move.Y),
                _rng.Next(UnstuckReplanMinFrames, UnstuckReplanMaxFrames));
        return MoveOutput(tank, move, world);
    }

    private ControllerOutput DoReturnMove(Tank tank, int relX, int relY)
    {
        int targetY = relY < 0 ? -OutsideBase : OutsideBase;

        if ((relX == 0 && relY == targetY) || IsInsideOwnBase(tank))
        {
            SetMode(TwitchMode.Recharge);
            return default;
        }

        if (Math.Abs(relX) <= OutsideBase && Math.Abs(relY) < OutsideBase)
            return new ControllerOutput { MoveSpeed = new Offset(0, relY < targetY ? 1 : -1) };

        int sx = relX != 0 ? (relX < 0 ? 1 : -1) : 0;
        int sy = relY != targetY ? (relY < targetY ? 1 : -1) : 0;
        return new ControllerOutput { MoveSpeed = new Offset(sx, sy) };
    }

    private ControllerOutput DoRecharge(Tank tank, int relX, int relY)
    {
        if (tank.Reactor.Health >= Tweaks.Tank.InitialHealth && IsRecharged(tank))
        {
            SetMode(TwitchMode.Start);
            _rechargeLowestHeat = float.MaxValue;
            _rechargeStallFrames = 0;
            return default;
        }

        int sx = relX != 0 ? (relX < 0 ? 1 : -1) : 0;
        int sy = relY != 0 ? (relY < 0 ? 1 : -1) : 0;
        return new ControllerOutput { MoveSpeed = new Offset(sx, sy) };
    }

    /// <summary>Room left before the tank overheats, as a share of a fully cooled tank's room.</summary>
    private static float HeatBudgetFraction(Tank tank) => tank.HeatBudget / Tank.HeatBudgetCapacity;

    /// <summary>
    /// Time to head home: the heat budget is low (sooner off the network, where the tank cannot cool on the way), the
    /// tank is already overheating, or its rising off-network heat floor will soon reach the safe maximum.
    /// </summary>
    private static bool NeedsToCool(Tank tank)
    {
        float returnBelow = tank.IsOffNetwork ? Tweaks.Bot.ReturnHeatBudgetFractionOffNetwork : Tweaks.Bot.ReturnHeatBudgetFraction;
        return HeatBudgetFraction(tank) < returnBelow || tank.IsAboveSafeHeat || tank.IsOffNetworkUrgent;
    }

    /// <summary>
    /// Docked, the bot leaves once it is fully cooled. If its heat has stopped falling (the base's reservoir is
    /// full) it settles for a reasonable budget instead of waiting forever.
    /// </summary>
    private bool IsRecharged(Tank tank)
    {
        if (!tank.IsCoolingDown)
            return true;

        if (tank.Heat < _rechargeLowestHeat)
        {
            _rechargeLowestHeat = tank.Heat;
            _rechargeStallFrames = 0;
        }
        else
        {
            _rechargeStallFrames++;
        }

        return _rechargeStallFrames >= Tweaks.Bot.RechargeStallFrames &&
               HeatBudgetFraction(tank) >= Tweaks.Bot.RechargeStalledMinHeatBudgetFraction;
    }

    private static bool ShouldRetreat(Tank self, Tank enemy, BotBehaviorProfile profile)
    {
        bool lowHealth = self.Reactor.Health < (int)(Tweaks.Tank.InitialHealth * profile.LowHealthRatio);
        bool highHeat = HeatBudgetFraction(self) < profile.RetreatHeatBudgetFraction;
        bool outgunned = self.Reactor.Health + profile.OutgunnedHealthGap < enemy.Reactor.Health;
        return lowHealth || highHeat || outgunned;
    }

    /// <summary>Whether the bot has the heat budget to torch through rock and ore.</summary>
    private static bool MayTorchRock(Tank tank) => HeatBudgetFraction(tank) >= TorchRockMinHeatBudgetFraction;

    /// <summary>
    /// Standing still for longer than what is in the way warrants. Rock and ore being torched
    /// (<paramref name="torchRock"/>) and a machine being shot take a while to give, so the bot keeps at those for
    /// <see cref="SlowStepPatienceFrames"/> before it counts as stuck.
    /// </summary>
    private bool IsStuck(Tank tank, World world, Offset move, int thresholdFrames, bool torchRock)
    {
        float speed = MathF.Max(tank.SpeedFactor, SlowestSpeedForStuck);
        if (_navigator.StuckFrames < (int)(thresholdFrames / speed))
            return false;
        if (_navigator.StuckFrames >= (int)(SlowStepPatienceFrames / speed))
            return true;
        var met = world.ProbeTankStep(tank, move);
        bool wearingItDown = met is StepObstacle.Hard or StepObstacle.Entity && NeedsTorch(met, torchDirt: false, torchRock);
        return !wearingItDown;
    }

    /// <summary>
    /// Whether to fire along the move to get through <paramref name="obstacle"/>: at rock and ore when
    /// <paramref name="torchRock"/> (only the torch takes them, and it heats the ground the tank then stands in), at
    /// dirt when <paramref name="torchDirt"/> (the torch digs it at full speed, for heat; the plain dig is half as
    /// fast and free), and at a machine in the way. With the enemy in sight the thing in the way may be its tank,
    /// and that is the gunner's to shoot.
    /// </summary>
    private bool NeedsTorch(StepObstacle obstacle, bool torchDirt, bool torchRock) => obstacle switch
    {
        StepObstacle.Hard => torchRock,
        StepObstacle.Dirt => torchDirt,
        StepObstacle.Entity => !_gunner.EnemyInSight,
        _ => false,
    };

    /// <summary>
    /// A committed move: leaving the base, heading home, a manoeuvre. It digs dirt the plain way and torches rock in
    /// its way only while the heat budget allows.
    /// </summary>
    private ControllerOutput MoveOutput(Tank tank, Offset move, World world) => new()
    {
        MoveSpeed = move,
        ShootPrimary = NeedsTorch(world.ProbeTankStep(tank, move), torchDirt: false, torchRock: MayTorchRock(tank)),
        AimDirection = BotGunner.AimTowardsOffset(move),
    };

    /// <summary>Commits to <paramref name="move"/> for <paramref name="frames"/> ticks, then plans afresh.</summary>
    private ControllerOutput StartManeuver(Tank tank, World world, Offset move, int frames, bool isBreakout = false)
    {
        _maneuverMove = move;
        _maneuverFrames = frames;
        _maneuverIsBreakout = isBreakout;
        _timeToChange = 0;
        _navigator.ResetStuck();
        return MoveOutput(tank, move, world);
    }

    private ControllerOutput ContinueManeuver(Tank tank, World world)
    {
        _maneuverFrames--;
        // Nothing gets through a wall, nor through rock without heat to spare: the manoeuvre ends there.
        var met = world.ProbeTankStep(tank, _maneuverMove);
        if (met == StepObstacle.Solid || (met == StepObstacle.Hard && !MayTorchRock(tank)))
            _maneuverFrames = 0;
        return MoveOutput(tank, _maneuverMove, world);
    }

    /// <summary>
    /// Mixes everything the bot remembers between ticks, its random stream included, for the sync digest. Every
    /// client runs every bot, so a bot that went apart on one machine would show only once its tank moved
    /// differently; this sees it at once.
    /// </summary>
    internal ulong MixState(ulong hash)
    {
        hash = _rng.MixState(hash);
        hash = _profiles.MixState(hash);
        hash = _navigator.MixState(hash);
        hash = _gunner.MixState(hash);
        hash = _trail.MixState(hash);
        hash = Fnv1a.Mix(hash, (int)_mode);
        hash = Fnv1a.Mix(Fnv1a.Mix(hash, _move.X), _move.Y);
        hash = Fnv1a.Mix(hash, _shoot);
        hash = Fnv1a.Mix(hash, _planHadLineOfSight);
        hash = Fnv1a.Mix(hash, _torchDirt);
        hash = Fnv1a.Mix(hash, _timeToChange);
        hash = Fnv1a.Mix(hash, _returnStallFrames);
        hash = Fnv1a.Mix(hash, _returnNearestCells);
        hash = Fnv1a.Mix(hash, _returnCooldownFrames);
        hash = Fnv1a.Mix(Fnv1a.Mix(hash, _aimDirection.X), _aimDirection.Y);
        hash = Fnv1a.Mix(Fnv1a.Mix(hash, _maneuverMove.X), _maneuverMove.Y);
        hash = Fnv1a.Mix(hash, _maneuverFrames);
        hash = Fnv1a.Mix(hash, _maneuverIsBreakout);
        hash = Fnv1a.Mix(hash, _rechargeLowestHeat);
        return Fnv1a.Mix(hash, _rechargeStallFrames);
    }
}
