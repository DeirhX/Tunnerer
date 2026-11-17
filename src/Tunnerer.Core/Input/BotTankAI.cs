namespace Tunnerer.Core.Input;

using Tunnerer.Core.Types;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Terrain;

public enum TwitchMode { Start, ExitBaseUp, ExitBaseDown, Twitch, Return, Recharge }

/// <summary>
/// The computer opponent. This class is its mode machine (leave the base, roam and fight, return, recharge);
/// <see cref="BotNavigator"/> picks its moves and <see cref="BotGunner"/> decides its fire. All three draw from one
/// seeded random stream, so the order of their calls within a tick is part of the bot's behaviour.
/// </summary>
public class BotTankAI
{
    /// <summary>Distance from the base centre (along Y) at which the bot counts as having left it.</summary>
    private const int OutsideBaseMargin = 5;
    private const int OutsideBase = Tweaks.Base.BaseSize / 2 + OutsideBaseMargin;

    private const int ExitStuckThresholdFrames = 10;
    private const int GeneralStuckThresholdFrames = 7;
    /// <summary>Stuck this long on the way home (a wall, an enemy base), the bot gives up and roams again.</summary>
    private const int ReturnGiveUpStuckFrames = 3 * Tweaks.Perf.TargetFps;
    private const int UnstuckReplanMinFrames = 4;
    private const int UnstuckReplanMaxFrames = 8;

    /// <summary>Roaming ends and the bot heads home below this health (heat limits are in <see cref="Tweaks.Bot"/>).</summary>
    private const int ReturnBelowHealth = 500;

    private readonly Random _rng;
    private readonly BotProfileCycle _profiles = new();
    private readonly BotNavigator _navigator;
    private readonly BotGunner _gunner;
    private TwitchMode _mode = TwitchMode.Start;
    private Offset _move;
    private bool _shoot;
    private int _timeToChange;
    private DirectionF _aimDirection;
    private float _rechargeLowestHeat = float.MaxValue;
    private int _rechargeStallFrames;

    public BotTankAI(int seed)
    {
        _rng = new Random(seed);
        _navigator = new BotNavigator(_rng);
        _gunner = new BotGunner(_rng);
    }

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

    public ControllerOutput GetInput(Tank tank, Tank? enemy, ITerrainPixelReadOnly terrain)
    {
        _profiles.Advance();
        _navigator.UpdateStuckState(tank.Position);
        if (_gunner.BeginTick(enemy) && _mode == TwitchMode.Twitch)
            _mode = TwitchMode.Return; // After a kill the bot goes home instead of waiting at the enemy's base exit.
        _gunner.UpdateLineOfSight(tank, enemy, terrain);

        var basePos = tank.Base?.Position ?? tank.Position;
        var relX = tank.Position.X - basePos.X;
        var relY = tank.Position.Y - basePos.Y;

        return _mode switch
        {
            TwitchMode.Start => DoStart(),
            TwitchMode.ExitBaseUp => DoExit(tank, terrain, exitY: -1, relY),
            TwitchMode.ExitBaseDown => DoExit(tank, terrain, exitY: 1, relY),
            TwitchMode.Twitch => DoTwitch(tank, enemy, terrain),
            TwitchMode.Return => DoReturn(tank, terrain, relX, relY),
            TwitchMode.Recharge => DoRecharge(tank, relX, relY),
            _ => default,
        };
    }

    /// <summary>Inside its own base's walls. A tank without a base counts as inside (it is at its own "base").</summary>
    private static bool IsInsideOwnBase(Tank tank) => tank.Base?.IsInside(tank.Position) ?? true;

    private ControllerOutput DoStart()
    {
        _mode = _rng.Next(2) == 0 ? TwitchMode.ExitBaseUp : TwitchMode.ExitBaseDown;
        return default;
    }

    /// <summary>Drives out of the base through the door in direction <paramref name="exitY"/>; stuck, it tries the other door.</summary>
    private ControllerOutput DoExit(Tank tank, ITerrainPixelReadOnly terrain, int exitY, int relY)
    {
        if (relY * exitY > OutsideBase) { _timeToChange = 0; _mode = TwitchMode.Twitch; return default; }
        if (_navigator.StuckFrames >= ExitStuckThresholdFrames)
        {
            _mode = exitY < 0 ? TwitchMode.ExitBaseDown : TwitchMode.ExitBaseUp;
            return BuildUnstuckOutput(tank.Position, terrain, preferY: exitY);
        }

        var move = new Offset(0, exitY);
        return MoveOutput(tank.Position, move, terrain);
    }

    private ControllerOutput DoTwitch(Tank tank, Tank? enemy, ITerrainPixelReadOnly terrain)
    {
        var profile = _profiles.Active;
        if (tank.Reactor.Health < ReturnBelowHealth || NeedsToCool(tank) || IsInsideOwnBase(tank))
        {
            _mode = TwitchMode.Return;
        }

        if (_timeToChange <= 0)
        {
            bool hasTarget = enemy != null && !enemy.IsDead;
            bool hasLos = hasTarget && BotGunner.HasLineOfSight(tank.Position, enemy!.Position, terrain);
            bool retreat = hasLos && ShouldRetreat(tank, enemy!, profile);
            var targetPos = hasTarget ? enemy!.Position : (Position?)null;

            _move = hasLos
                ? _navigator.ChooseCombatMove(tank.Position, enemy!.Position, terrain, retreat, _move, profile)
                : _navigator.ChooseBestMove(tank.Position, targetPos, terrain, _move, profile);
            if (_move.X == 0 && _move.Y == 0)
                _move = BotNavigator.MoveCandidates[_rng.Next(BotNavigator.MoveCandidates.Length)];

            bool shouldDig = BotNavigator.NeedsDigging(tank.Position, _move, terrain);
            (_shoot, _aimDirection) = _gunner.Plan(tank.Position, enemy, hasTarget, hasLos, retreat, _move, shouldDig, profile);
            _timeToChange = hasLos
                ? _rng.Next(profile.ReplanLosMin, profile.ReplanLosMax)
                : _rng.Next(profile.ReplanTunnelMin, profile.ReplanTunnelMax);

            if (_navigator.StuckFrames >= GeneralStuckThresholdFrames)
            {
                _move = _navigator.ChooseUnstuckMove(tank.Position, terrain);
                _shoot = BotNavigator.NeedsDigging(tank.Position, _move, terrain);
                _aimDirection = BotGunner.AimTowardsOffset(_move);
                _timeToChange = _rng.Next(UnstuckReplanMinFrames, UnstuckReplanMaxFrames);
            }
        }

        _timeToChange--;

        return new ControllerOutput
        {
            MoveSpeed = _move,
            ShootPrimary = _gunner.ApplyBurstLimit(_shoot),
            AimDirection = _aimDirection,
        };
    }

    private ControllerOutput DoReturn(Tank tank, ITerrainPixelReadOnly terrain, int relX, int relY)
    {
        // Stuck on the way home (a wall, an enemy base): dig through, and give up after a while instead of
        // waiting forever.
        if (_navigator.StuckFrames >= ReturnGiveUpStuckFrames)
        {
            _mode = TwitchMode.Twitch;
            _timeToChange = 0;
            return default;
        }
        var home = DoReturnMove(tank, relX, relY);
        if (_mode != TwitchMode.Return)
            return home;
        if (_navigator.StuckFrames >= GeneralStuckThresholdFrames)
            return BuildUnstuckOutput(tank.Position, terrain, preferY: Math.Sign(home.MoveSpeed.Y));
        return MoveOutput(tank.Position, home.MoveSpeed, terrain);
    }

    private ControllerOutput DoReturnMove(Tank tank, int relX, int relY)
    {
        int targetY = relY < 0 ? -OutsideBase : OutsideBase;

        if ((relX == 0 && relY == targetY) || IsInsideOwnBase(tank))
        {
            _mode = TwitchMode.Recharge;
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
            _mode = TwitchMode.Start;
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

    private static ControllerOutput MoveOutput(Position self, Offset move, ITerrainPixelReadOnly terrain) => new()
    {
        MoveSpeed = move,
        ShootPrimary = BotNavigator.NeedsDigging(self, move, terrain),
        AimDirection = BotGunner.AimTowardsOffset(move),
    };

    private ControllerOutput BuildUnstuckOutput(Position self, ITerrainPixelReadOnly terrain, int preferY) =>
        MoveOutput(self, _navigator.ChooseUnstuckMove(self, terrain, preferY), terrain);
}
