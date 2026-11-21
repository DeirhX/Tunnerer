namespace Emberward.Core.Entities.Tanks;

using Emberward.Core.Config;
using Emberward.Core.Entities.Projectiles;
using Emberward.Core.Resources;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

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

    public TimeSpan RespawnRemaining =>
        _respawning && _tank.Base is { IsDestroyed: false } ? _respawnTimer : TimeSpan.Zero;

    public void BeginTick()
    {
        if (HitFlashTicks > 0)
            HitFlashTicks--;
    }

    /// <summary>What the last hit took, from the shield and from the hull; nothing when it was refused.</summary>
    public DamageTaken LastDamageTaken { get; private set; }

    /// <summary>
    /// Takes armor off the tank. With a shield, it takes the hit first and the hull the rest; being destroyed with its
    /// base or recalled goes straight to the hull, so a full shield never keeps a tank alive through either.
    /// </summary>
    public bool TakeDamage(int amount, DamageCause cause, VectorF hitVelocity)
    {
        LastDamageTaken = default;
        if (amount <= 0 || _tank.IsDead)
            return false;
        if (cause == DamageCause.EnemyFire && SpawnProtectionTicks > 0)
            return false;
        bool bypass = cause is DamageCause.BaseDestroyed or DamageCause.Recalled;
        var taken = _tank.Shield is { } shield && !bypass ? shield.Absorb(_tank, amount) : new DamageTaken(0, amount);
        if (taken.Hull > 0)
            _tank.Reactor.Exhaust(new ReactorState(0, taken.Hull));
        LastDamageTaken = taken;
        LastDamageCause = cause;
        if (cause == DamageCause.EnemyFire)
        {
            if (taken.Hull > 0)
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

    public void Die(ProjectileList? projectiles, ITerrainPixelAccessor? terrain, Func<Position, bool>? isOccupied = null,
        TimeSpan? delay = null, bool recall = false)
    {
        if (_respawning)
            return;

        var reactor = _tank.Reactor;
        reactor.Exhaust(new ReactorState(reactor.Heat, reactor.Health));
        _build.DiscardCarried();
        DropOre(terrain, isOccupied);
        _respawning = true;
        _respawnTimer = delay ?? Tweaks.Tank.RespawnDelay;

        if (!recall)
            projectiles?.AddExplosion(_tank.Position, Tweaks.Explosion.Death, Tweaks.Explosion.DeathHeatScale);
    }

    /// <summary>
    /// The hold spills back into the ground around the wreck (<see cref="OreDrop"/>), not under live tanks or planted
    /// machines (<paramref name="isOccupied"/>); what finds no room, or all of it when there is no terrain to drop
    /// into, is lost. Either way the hold is empty.
    /// </summary>
    private void DropOre(ITerrainPixelAccessor? terrain, Func<Position, bool>? isOccupied)
    {
        var hold = _tank.Hold;
        if (terrain != null && hold.Ore > 0)
            OreDrop.Scatter(terrain, _tank.Position, hold.Ore, isOccupied);
        hold.Ore = 0;
    }

    /// <summary>Drops cargo once, then respawns at an intact base when the timer runs out.</summary>
    public void AdvanceDeath(in SimulationContext ctx, bool recall)
    {
        if (!_respawning)
        {
            ctx.Events?.Record(new StepEvent(StepEventKind.TankDied, _tank.Position, _tank.Color));
            var delay = _tank.Base is { } home ? ctx.BasePower?.RespawnDelay(home) : null;
            Die(ctx.Projectiles, ctx.Terrain, ctx.IsOccupied, delay, recall);
        }

        if (_tank.Base is not { IsDestroyed: false })
            return;

        _respawnTimer -= ctx.Settings.AdvanceStep;
        if (!recall && _respawnTimer > TimeSpan.Zero)
            return;

        _tank.Spawn(ctx.Heat.Ledger);
        ctx.Events?.Record(new StepEvent(StepEventKind.TankRespawned, _tank.Position, _tank.Color));
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

    /// <summary>Mixes the respawn countdown and the spawn protection, for the sync digest.</summary>
    internal ulong MixLatent(ulong hash)
    {
        hash = Fnv1a.Mix(hash, _respawning);
        hash = Fnv1a.Mix(hash, _respawnTimer);
        hash = Fnv1a.Mix(hash, SpawnProtectionTicks);
        return Fnv1a.Mix(hash, _protectedUntilExit);
    }
}
