namespace Tunnerer.Core.Entities;

/// <summary>What last took armor off a tank; the HUD uses it to explain hits and deaths.</summary>
public enum DamageCause
{
    None,
    EnemyFire,
    Overheat,
}
