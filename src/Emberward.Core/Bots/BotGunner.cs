namespace Emberward.Core.Bots;

using Emberward.Core.Collision;
using Emberward.Core.Config;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using Emberward.Core.Entities.Tanks;

/// <summary>
/// When and where the bot fires. Keeps it fair for a human: no aimed fire during the opening, a reaction delay after
/// first seeing the enemy, a pause after the enemy respawns, a little aim spread and bursts with pauses between them.
/// Shares the bot's random stream, so the order of its draws is part of the bot's behaviour.
/// </summary>
internal sealed class BotGunner
{
    // Counted in ticks so the simulation stays deterministic.
    private const int OpeningGraceFrames = 30 * Tweaks.Perf.TargetFps;
    private const int BaseSiegeGraceFrames = Tweaks.Weapon.BotBaseSiegeGraceSeconds * Tweaks.Perf.TargetFps;
    private const int ReactionFrames = 12;
    private const double AimSpreadRadians = 0.10;
    // Aimed fire comes in bursts with pauses between them, so a human has time to react or escape.
    private const int BurstFrames = 9;
    private const int BurstPauseFrames = 20;
    // After its target respawns the bot holds aimed fire for a while.
    private const int RespawnHoldFireFrames = 6 * Tweaks.Perf.TargetFps;
    /// <summary>Lowest chance (per mille) of firing back while retreating in sight of the enemy.</summary>
    private const int RetreatShootChancePermille = 820;

    private readonly BotRandom _rng;
    private int _frame;
    private int _losStreak;
    private int _burstFramesLeft = BurstFrames;
    private int _burstPauseLeft;
    private int _holdFireUntilFrame;
    private bool _enemyWasDead;

    public BotGunner(BotRandom rng) => _rng = rng;

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

    /// <summary>True while the bot has a clear line of sight to a live enemy (as of <see cref="UpdateLineOfSight"/>).</summary>
    public bool EnemyInSight => _losStreak > 0;

    /// <summary>False during the opening: the bot does not shoot at the enemy's base until the siege grace is over.</summary>
    public bool BaseSiegeAllowed => _frame >= BaseSiegeGraceFrames;

    /// <summary>
    /// Decides whether to fire at the enemy this plan and where to aim. With the enemy in sight and aimed fire allowed
    /// the turret follows it; otherwise it points along the move, where the bot is going and where its torch has to
    /// point to cut through, so nothing it fires while digging is aimed at the enemy.
    /// </summary>
    public (bool Shoot, DirectionF Aim) Plan(Position self, Tank? enemy, bool hasLos, bool retreat, Offset move,
        BotBehaviorProfile profile)
    {
        bool mayFire = _frame >= OpeningGraceFrames && _frame >= _holdFireUntilFrame && _losStreak >= ReactionFrames;
        if (!hasLos || !mayFire || enemy == null)
            return (false, AimTowardsOffset(move));

        int chance = retreat
            ? Math.Max(profile.ShootChanceLosPermille, RetreatShootChancePermille)
            : profile.ShootChanceLosPermille;
        bool shoot = _rng.Next(1000) < chance;
        var aim = AimTowards(self, enemy.Position);
        if (shoot)
            aim = Rotate(aim, (_rng.NextDouble() * 2.0 - 1.0) * AimSpreadRadians);
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

    private static DirectionF AimTowards(Position from, Position to) => AimTowardsOffset(to - from);

    private static DirectionF Rotate(DirectionF d, double radians)
    {
        float c = (float)DeterministicMath.Cos(radians), s = (float)DeterministicMath.Sin(radians);
        return new DirectionF(d.X * c - d.Y * s, d.X * s + d.Y * c);
    }

    /// <summary>Mixes the gunner's clock, bursts and held fire, for the sync digest.</summary>
    internal ulong MixState(ulong hash)
    {
        hash = Fnv1a.Mix(hash, _frame);
        hash = Fnv1a.Mix(hash, _losStreak);
        hash = Fnv1a.Mix(hash, _burstFramesLeft);
        hash = Fnv1a.Mix(hash, _burstPauseLeft);
        hash = Fnv1a.Mix(hash, _holdFireUntilFrame);
        return Fnv1a.Mix(hash, _enemyWasDead);
    }
}
