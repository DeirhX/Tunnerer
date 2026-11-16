namespace Tunnerer.Core.Input;

using Tunnerer.Core.Collision;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

/// <summary>
/// When and where the bot fires. Keeps it fair for a human: no aimed fire during the opening, a reaction delay after
/// first seeing the enemy, a pause after the enemy respawns, a little aim spread and bursts with pauses between them.
/// Shares the bot's random stream, so the order of its draws is part of the bot's behaviour.
/// </summary>
internal sealed class BotGunner
{
    // Counted in ticks so the simulation stays deterministic.
    private const int OpeningGraceFrames = 30 * Tweaks.Perf.TargetFps;
    private const int ReactionFrames = 12;
    private const double AimSpreadRadians = 0.10;
    // Aimed fire comes in bursts with pauses between them, so a human has time to react or escape.
    private const int BurstFrames = 9;
    private const int BurstPauseFrames = 20;
    // After its target respawns the bot holds aimed fire for a while.
    private const int RespawnHoldFireFrames = 6 * Tweaks.Perf.TargetFps;
    /// <summary>Lowest chance (per mille) of firing back while retreating in sight of the enemy.</summary>
    private const int RetreatShootChancePermille = 820;

    private readonly Random _rng;
    private int _frame;
    private int _losStreak;
    private int _burstFramesLeft = BurstFrames;
    private int _burstPauseLeft;
    private int _holdFireUntilFrame;
    private bool _enemyWasDead;

    public BotGunner(Random rng) => _rng = rng;

    /// <summary>
    /// Advances the gunner's clock and follows the enemy's life: after it respawns, aimed fire is held for a while.
    /// Returns true on the tick the enemy died.
    /// </summary>
    public bool BeginTick(Tank? enemy)
    {
        _frame++;
        if (enemy == null)
            return false;
        bool justDied = enemy.IsDead && !_enemyWasDead;
        if (!enemy.IsDead && _enemyWasDead)
            _holdFireUntilFrame = _frame + RespawnHoldFireFrames;
        _enemyWasDead = enemy.IsDead;
        return justDied;
    }

    /// <summary>Counts consecutive ticks with a clear line of sight to a live enemy.</summary>
    public void UpdateLineOfSight(Tank tank, Tank? enemy, ITerrainPixelReadOnly terrain)
    {
        _losStreak = enemy is { IsDead: false } && HasLineOfSight(tank.Position, enemy.Position, terrain)
            ? _losStreak + 1
            : 0;
    }

    /// <summary>
    /// Decides whether to fire this plan and where to aim: at the enemy when there is one, along the move otherwise.
    /// While aimed fire is not yet allowed it only digs, aiming along the move so stray digging shots miss the enemy.
    /// </summary>
    public (bool Shoot, DirectionF Aim) Plan(Position self, Tank? enemy, bool hasTarget, bool hasLos, bool retreat,
        Offset move, bool shouldDig, BotBehaviorProfile profile)
    {
        bool shoot = retreat && hasLos
            ? _rng.Next(1000) < Math.Max(profile.ShootChanceLosPermille, RetreatShootChancePermille)
            : hasLos
                ? _rng.Next(1000) < profile.ShootChanceLosPermille
                : shouldDig && _rng.Next(1000) < profile.ShootChanceDigPermille;
        var aim = hasTarget ? AimTowards(self, enemy!.Position) : AimTowardsOffset(move);
        bool mayFire = _frame >= OpeningGraceFrames && _frame >= _holdFireUntilFrame && _losStreak >= ReactionFrames;
        if (hasLos && !mayFire)
        {
            shoot = shouldDig;
            aim = AimTowardsOffset(move);
        }
        else if (hasLos && shoot)
        {
            aim = Rotate(aim, (_rng.NextDouble() * 2.0 - 1.0) * AimSpreadRadians);
        }
        return (shoot, aim);
    }

    /// <summary>Burst limiter for aimed fire (fire at the enemy while it is in sight): returns whether to fire this tick.</summary>
    public bool ApplyBurstLimit(bool shoot)
    {
        bool aimedFire = shoot && _losStreak > 0;
        bool shootNow = shoot;
        if (_burstPauseLeft > 0)
        {
            _burstPauseLeft--;
            if (aimedFire)
                shootNow = false;
        }
        else if (aimedFire && --_burstFramesLeft <= 0)
        {
            _burstFramesLeft = BurstFrames;
            _burstPauseLeft = BurstPauseFrames;
        }
        return shootNow;
    }

    public static bool HasLineOfSight(Position from, Position to, ITerrainPixelReadOnly terrain)
    {
        bool blocked = Raycaster.BresenhamLineAny(from, to, pos =>
        {
            if (pos == from || pos == to)
                return false;
            if (!terrain.IsInside(pos))
                return true;
            return Pixel.IsBlockingCollision(terrain.GetPixelRaw(pos));
        });
        return !blocked;
    }

    public static DirectionF AimTowardsOffset(Offset offset)
    {
        float dx = offset.X;
        float dy = offset.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.001f)
            return default;
        return new DirectionF(dx / len, dy / len);
    }

    private static DirectionF AimTowards(Position from, Position to)
    {
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.001f)
            return default;
        return new DirectionF(dx / len, dy / len);
    }

    private static DirectionF Rotate(DirectionF d, double radians)
    {
        float c = (float)DeterministicMath.Cos(radians), s = (float)DeterministicMath.Sin(radians);
        return new DirectionF(d.X * c - d.Y * s, d.X * s + d.Y * c);
    }
}
