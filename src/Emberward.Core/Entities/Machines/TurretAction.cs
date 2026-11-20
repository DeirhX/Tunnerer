namespace Emberward.Core.Entities.Machines;

using Emberward.Core.Collision;
using Emberward.Core.Config;
using Emberward.Core.Entities.Projectiles;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Network;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

/// <summary>
/// The turret (DESIGN.md §4.9): a machine of the energy network that shoots at enemy tanks within its range and in
/// direct line of sight. It works only while it is linked (in its base's network coverage, or beside a local
/// generator's island, the rule a harvester follows), its island holds it, its base is not dark and its capacitor can
/// pay for a shot. Each shot spends charge through the network, which turns it into heat in the ground beside the
/// turret, and the hotter that ground the slower it fires.
/// <para>
/// Cost follows what changes: between looks it only counts down, and a look reads the few tanks of the match, casting
/// a line of sight only to those in range. Its numbers come from its upgrade (<see cref="MachineUpgrades.TurretStatsOf"/>).
/// </para>
/// </summary>
public static class TurretAction
{
    // Scratch for one coverage question. Per thread, because worlds on different threads (tests, replays) may act at the
    // same time; nothing in it outlives a call.
    [ThreadStatic]
    private static List<Machine>? t_near;

    /// <summary>One tick of a planted turret: count down, or look for a target and shoot.</summary>
    internal static void Act(Machine turret, MachineContext ctx)
    {
        if (turret.TurretCooldown > 0)
        {
            turret.TurretCooldown--;
            // While it waits out a shot it still follows its coverage at the look rate, so its capacitor neither keeps
            // filling from an island it has left nor waits long to join one.
            if (turret.TurretCooldown > 0 && turret.TurretCooldown % Tweaks.Turret.ScanIntervalTicks == 0 &&
                ctx.Energy is EnergyNetwork waiting && turret.OwnerBase is { IsDestroyed: false })
                Cover(turret, ctx, waiting);
            return;
        }
        // Looked at again after a short wait unless it fires, which waits its interval instead.
        turret.TurretCooldown = Tweaks.Turret.ScanIntervalTicks - 1;
        if (ctx.Energy is not EnergyNetwork energy || turret.OwnerBase is not { IsDestroyed: false } home)
            return;

        var terrain = ctx.Terrain;
        Cover(turret, ctx, energy);
        float temperature = SenseTemperature(terrain, turret.Position);
        turret.SenseHeat = temperature / Tweaks.Turret.MaxSlowTemperature;
        turret.IsStalled = temperature > Tweaks.Turret.SlowTemperature;

        if (ctx.BasePower?.IsDark(home) == true || !energy.IsOnIsland(turret) ||
            ctx.Tanks is not TankList tanks || ctx.Projectiles is not ProjectileList projectiles)
            return;
        var stats = MachineUpgrades.TurretStatsOf(ctx.Upgrades?.LevelOf(turret) ?? default);
        if (TargetOf(turret, stats.Range, tanks, terrain) is not Tank target)
            return;
        energy.Settle(turret, terrain);
        if (turret.Charge < stats.ShotCharge)
            return;

        Fire(turret, target, stats, projectiles);
        energy.Spend(turret, stats.ShotCharge, terrain);
        ctx.Events?.Record(new StepEvent(StepEventKind.TurretFired, turret.Position, turret.OwnerColor, turret.Type,
            Count: target.Color));
        turret.TurretCooldown = IntervalAt(stats.IntervalTicks, temperature) - 1;
    }

    /// <summary>Joins the island whose coverage it stands in (its base's, or a local generator's), as a harvester does.</summary>
    private static void Cover(Machine turret, MachineContext ctx, EnergyNetwork energy) =>
        energy.SetCovered(turret, ctx.IsOnNetwork(turret), ctx.Machines, ctx.Terrain, t_near ??= new List<Machine>());

    /// <summary>
    /// The enemy tank <paramref name="turret"/> shoots at: of the live tanks of other sides without a respawn shield,
    /// within <paramref name="range"/> cells and with a clear shot (open ground, no base wall between), the nearest; on a
    /// tie the first in spawn order. Null when there is none.
    /// </summary>
    public static Tank? TargetOf(Machine turret, int range, TankList tanks, ITerrainPixelReadOnly terrain)
    {
        Tank? best = null;
        long bestDistance = (long)range * range;
        foreach (var tank in tanks.Tanks)
        {
            if (tank.IsDead || tank.Color == turret.OwnerColor || tank.SpawnProtectionTicks > 0)
                continue;
            long dx = tank.Position.X - turret.Position.X, dy = tank.Position.Y - turret.Position.Y;
            long distance = dx * dx + dy * dy;
            // Strictly nearer only once one is found: the list is in spawn order, so the earliest keeps a tie.
            if (distance > bestDistance || (best != null && distance == bestDistance))
                continue;
            if (!Raycaster.HasClearShot(terrain, turret.Position, tank.Position))
                continue;
            best = tank;
            bestDistance = distance;
        }
        return best;
    }

    /// <summary>
    /// Ticks between shots at ground <paramref name="temperature"/>: <paramref name="interval"/> up to
    /// <c>Turret.SlowTemperature</c>, rising linearly to <c>Turret.MaxSlowdown</c> times it at
    /// <c>Turret.MaxSlowTemperature</c>.
    /// </summary>
    public static int IntervalAt(int interval, float temperature)
    {
        double over = (temperature - Tweaks.Turret.SlowTemperature) /
            (double)(Tweaks.Turret.MaxSlowTemperature - Tweaks.Turret.SlowTemperature);
        double slowdown = 1.0 + Math.Clamp(over, 0.0, 1.0) * (Tweaks.Turret.MaxSlowdown - 1.0);
        return Math.Max(1, (int)Math.Ceiling(interval * slowdown));
    }

    /// <summary>The ground temperature a turret at <paramref name="at"/> senses, averaged over its sense radius.</summary>
    public static float SenseTemperature(ITerrainHeatSource heat, Position at) =>
        // SampleAverageHeat is normalised (temperature / 255).
        heat.SampleAverageHeat(at, Tweaks.Turret.HeatSenseRadius) * 255f;

    /// <summary>A bullet from the barrel toward <paramref name="target"/>; the turret turns to face it.</summary>
    private static void Fire(Machine turret, Tank target, TurretStats stats, ProjectileList projectiles)
    {
        int dx = target.Position.X - turret.Position.X, dy = target.Position.Y - turret.Position.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        var direction = length > 0f ? new DirectionF(dx / length, dy / length) : new DirectionF(0f, -1f);
        // From the barrel's tip, or from halfway to a tank closer than that: a bullet is tested from its first step on,
        // so a muzzle past a point-blank tank would miss it.
        float reach = MathF.Min(Tweaks.Turret.BarrelLength, length / 2f);
        var muzzle = (Position)new PositionF(
            turret.Position.X + direction.X * reach,
            turret.Position.Y + direction.Y * reach);
        projectiles.Add(Projectile.CreateBullet(muzzle, direction.ToVector(Tweaks.Weapon.CannonBulletSpeed),
            turret.OwnerColor, stats.Damage));
        if (dx != 0 || dy != 0)
            turret.Heading = new Offset(dx, dy);
    }
}
