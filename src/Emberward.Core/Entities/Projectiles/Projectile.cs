namespace Emberward.Core.Entities.Projectiles;

using Emberward.Core.Types;
using Emberward.Core.Terrain;
using Emberward.Core.Config;

public enum ProjectileType
{
    Bullet,
    Shrapnel,

    /// <summary>A gatling's round: light, spent after a short flight, and it stops on what it hits without bursting.</summary>
    Round,
}

public class Projectile
{
    public PositionF Position { get; set; }
    public VectorF Speed { get; init; }
    public bool IsAlive { get; set; } = true;
    public ProjectileType Type { get; init; }
    public int Life { get; set; }
    public int OwnerColor { get; init; } = -1;
    public float HeatScale { get; init; } = 1f;

    /// <summary>What a bullet takes off what it strikes: a tank's shell does <see cref="Tweaks.Weapon.BulletDamage"/>.</summary>
    public int Damage { get; init; } = Tweaks.Weapon.BulletDamage;

    public static Projectile CreateBullet(Position pos, VectorF speed, int ownerColor, int damage = Tweaks.Weapon.BulletDamage) => new()
    {
        Position = (PositionF)pos,
        Speed = speed,
        Type = ProjectileType.Bullet,
        IsAlive = true,
        OwnerColor = ownerColor,
        Damage = damage,
    };

    /// <summary>Cells of soil a round still passes through; the next one after that stops it.</summary>
    public int Pierce { get; set; }

    /// <summary>
    /// A gatling's round: it flies for <paramref name="life"/> ticks, passes through <paramref name="pierce"/> cells of
    /// soil and takes <paramref name="damage"/> off a tank or a machine it strikes.
    /// </summary>
    public static Projectile CreateRound(Position pos, VectorF speed, int ownerColor, int damage, int life, int pierce) => new()
    {
        Position = (PositionF)pos,
        Speed = speed,
        Type = ProjectileType.Round,
        IsAlive = true,
        OwnerColor = ownerColor,
        Damage = damage,
        Life = life,
        Pierce = pierce,
    };

    public static Projectile CreateShrapnel(Position pos, VectorF speed, int life, float heatScale = 1f) => new()
    {
        Position = (PositionF)pos,
        Speed = speed,
        Type = ProjectileType.Shrapnel,
        Life = life,
        IsAlive = true,
        HeatScale = heatScale,
    };

}
