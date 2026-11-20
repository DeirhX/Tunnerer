namespace Emberward.Core.Entities.Tanks;

using System.Diagnostics;
using Emberward.Core.Types;
using Emberward.Core.Config;
using Emberward.Core.Entities.Projectiles;

public class TankTurret
{
    private DirectionF _direction = new(0, -1);
    private int _cooldown;
    private readonly int _ownerColor;

    public DirectionF Direction => _direction;
    public bool IsShooting { get; private set; }

    public TankTurret(int ownerColor)
    {
        _ownerColor = ownerColor;
    }

    public void SetDirection(DirectionF dir)
    {
        float lenSq = dir.X * dir.X + dir.Y * dir.Y;
        Debug.Assert(!float.IsNaN(lenSq) && !float.IsInfinity(lenSq), "Turret direction must be finite.");
        if (lenSq < 0.000001f)
            return;
        float invLen = 1f / MathF.Sqrt(lenSq);
        _direction = new DirectionF(dir.X * invLen, dir.Y * invLen);
    }

    public void Update(bool shooting)
    {
        IsShooting = shooting;
        if (_cooldown > 0) _cooldown--;
    }

    public PositionF GetBarrelTip(Position tankPos)
    {
        return new PositionF(
            tankPos.X + _direction.X * Tweaks.Tank.TurretLength,
            tankPos.Y + _direction.Y * Tweaks.Tank.TurretLength);
    }

    /// <param name="delayFactor">How many times the usual delay the next shot waits (a degraded reserve slows the gun).</param>
    public Projectile? TryShoot(Position tankPos, ProjectileList projectileList, int delayFactor = 1)
    {
        if (!IsShooting || _cooldown > 0) return null;

        _cooldown = Tweaks.Tank.TurretDelay * delayFactor;
        var barrel = GetBarrelTip(tankPos);
        var speed = _direction.ToVector(Tweaks.Weapon.CannonBulletSpeed);
        var bullet = Projectile.CreateBullet((Position)barrel, speed, _ownerColor);
        projectileList.Add(bullet);
        return bullet;
    }

    public void Reset()
    {
        _cooldown = 0;
    }

    /// <summary>Mixes what the rest of the sync digest does not see: the ticks until the next shot.</summary>
    internal ulong MixLatent(ulong hash) => Fnv1a.Mix(hash, _cooldown);
}
