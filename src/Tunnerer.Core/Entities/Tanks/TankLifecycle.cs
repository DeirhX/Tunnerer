namespace Tunnerer.Core.Entities.Tanks;

using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Projectiles;
using Tunnerer.Core.Resources;
using Tunnerer.Core.Thermal;
using Tunnerer.Core.Types;

/// <summary>
/// Damage, death and respawn: records why armor was lost, runs the hit flash and the respawn shield, and counts
/// down to the next spawn once the tank is destroyed.
/// </summary>
internal sealed class TankLifecycle
{
    private readonly Tank _tank;
    private readonly TankBuildController _build;
    private bool _protectedUntilExit;
    private TimeSpan _respawnTimer;
    private bool _respawning;

    public TankLifecycle(Tank tank, TankBuildController build)
    {
        _tank = tank;
        _build = build;
    }

    public DamageCause LastDamageCause { get; private set; }
    public DamageCause DeathCause { get; private set; }
    public int HitFlashTicks { get; private set; }
    public VectorF LastHitVelocity { get; private set; }
    public int SpawnProtectionTicks { get; private set; }

    public TimeSpan RespawnRemaining => _respawning && _tank.LivesLeft > 0 ? _respawnTimer : TimeSpan.Zero;

    public void BeginTick()
    {
        if (HitFlashTicks > 0)
            HitFlashTicks--;
    }

    public bool TakeDamage(int amount, DamageCause cause, VectorF hitVelocity)
    {
        if (amount <= 0 || _tank.IsDead)
            return false;
        if (cause == DamageCause.EnemyFire && SpawnProtectionTicks > 0)
            return false;
        _tank.Reactor.Exhaust(new ReactorState(0, amount));
        LastDamageCause = cause;
        if (cause == DamageCause.EnemyFire)
        {
            HitFlashTicks = Tank.HitFlashDuration;
            LastHitVelocity = hitVelocity;
        }
        if (_tank.IsDead)
            DeathCause = cause;
        return true;
    }

    /// <summary>
    /// The respawn shield holds while the tank is in its own base, then lasts <see cref="Tank.SpawnProtectionDuration"/>
    /// more ticks after it leaves; it never exceeds <see cref="Tank.SpawnProtectionCap"/> and firing ends it at once.
    /// </summary>
    public void UpdateSpawnProtection(bool inOwnBase, bool firing)
    {
        if (SpawnProtectionTicks <= 0)
            return;
        if (firing)
        {
            SpawnProtectionTicks = 0;
            _protectedUntilExit = false;
            return;
        }
        if (_protectedUntilExit && !inOwnBase)
        {
            _protectedUntilExit = false;
            SpawnProtectionTicks = Math.Min(SpawnProtectionTicks, Tank.SpawnProtectionDuration);
        }
        SpawnProtectionTicks--;
    }

    public void Die(ProjectileList? projectiles)
    {
        if (_respawning)
            return;

        var reactor = _tank.Reactor;
        reactor.Exhaust(new ReactorState(reactor.Heat, reactor.Health));
        _build.DiscardCarried();
        _tank.LivesLeft = Math.Max(0, _tank.LivesLeft - 1);
        _respawning = true;
        _respawnTimer = _tank.LivesLeft > 0 ? Tweaks.Tank.RespawnDelay : TimeSpan.Zero;

        projectiles?.AddExplosion(_tank.Position, Tweaks.Explosion.Death, Tweaks.Explosion.DeathHeatScale);
    }

    /// <summary>A dead tank's tick: it explodes once, then respawns at its base when the timer runs out and lives remain.</summary>
    public void AdvanceDeath(ProjectileList projectiles, ThermalLedger ledger, StepEventLog? events)
    {
        if (!_respawning)
        {
            events?.Record(new StepEvent(StepEventKind.TankDied, _tank.Position, _tank.Color));
            Die(projectiles);
        }

        if (_tank.LivesLeft <= 0)
            return;

        _respawnTimer -= Tweaks.World.AdvanceStep;
        if (_respawnTimer > TimeSpan.Zero)
            return;

        _tank.Spawn(ledger);
        events?.Record(new StepEvent(StepEventKind.TankRespawned, _tank.Position, _tank.Color));
    }

    /// <summary>Clears the death state and raises the respawn shield.</summary>
    public void OnSpawned()
    {
        _respawning = false;
        DeathCause = DamageCause.None;
        LastDamageCause = DamageCause.None;
        HitFlashTicks = 0;
        SpawnProtectionTicks = Tank.SpawnProtectionCap;
        _protectedUntilExit = true;
    }
}
