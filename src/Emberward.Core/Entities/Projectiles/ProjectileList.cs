namespace Emberward.Core.Entities.Projectiles;

using Emberward.Core.Types;
using Emberward.Core.Terrain;
using Emberward.Core.Config;
using Emberward.Core.Entities;
using Emberward.Core.Resources;
using Emberward.Core.Collision;
using Emberward.Core.Rendering;
using Emberward.Core.Network;

/// <summary>
/// Defines per-type projectile behavior: how it advances and what color it draws.
/// Adding a new projectile type = one entry in <see cref="ProjectileList.Behaviors"/>.
/// </summary>
public record ProjectileBehavior(
    Action<Projectile, CollisionSolver, ProjectileList> Advance,
    Color DrawColor);

public class ProjectileList
{
    private readonly List<Projectile> _projectiles = new();
    private readonly List<Projectile> _pending = new();
    private FastRandom _rng;
    private readonly StepEventLog? _events;
    private readonly BasePowerSystem? _basePower;

    /// <summary>
    /// Single source of truth for per-type projectile behavior.
    /// To add a new projectile type: add the enum value, then add one entry here.
    /// </summary>
    public static readonly ProjectileBehavior[] Behaviors = BuildBehaviors();

    private static ProjectileBehavior[] BuildBehaviors()
    {
        var values = Enum.GetValues<ProjectileType>();
        int max = 0;
        foreach (var v in values)
            max = Math.Max(max, (int)v);

        var behaviors = new ProjectileBehavior[max + 1];
        behaviors[(int)ProjectileType.Bullet] = new(AdvanceBullet, Tweaks.Colors.FireHot);
        behaviors[(int)ProjectileType.Shrapnel] = new(AdvanceShrapnel, Tweaks.Colors.FireHot);
        return behaviors;
    }

    /// <param name="events">Where impacts, hits and explosions are recorded (<see cref="World.StepEvents"/>); null records nothing.</param>
    public ProjectileList(int seed, StepEventLog? events = null, BasePowerSystem? basePower = null)
    {
        _rng = new FastRandom(seed);
        _events = events;
        _basePower = basePower;
    }

    public int Count => _projectiles.Count + _pending.Count;
    public void Add(Projectile p) => _pending.Add(p);
    public void AddRange(IEnumerable<Projectile> items) => _pending.AddRange(items);

    public void Advance(CollisionSolver solver)
    {
        _projectiles.AddRange(_pending);
        _pending.Clear();

        for (int i = _projectiles.Count - 1; i >= 0; i--)
        {
            var p = _projectiles[i];
            if (!p.IsAlive) { _projectiles.RemoveAt(i); continue; }

            Behaviors[(int)p.Type].Advance(p, solver, this);
        }

        if (_projectiles.Count > Tweaks.Perf.ProjectileCompactThreshold)
            _projectiles.RemoveAll(p => !p.IsAlive);
    }

    private static void AdvanceBullet(Projectile p, CollisionSolver solver, ProjectileList self)
    {
        var terrain = solver.Terrain;
        int steps = Math.Max(1, (int)MathF.Ceiling(p.Speed.Length));
        var stepDir = p.Speed * (1f / steps);

        for (int s = 0; s < steps; s++)
        {
            if (!TryAdvanceInside(terrain, p, stepDir, out var ipos))
                return;

            bool hit = solver.TestPoint(ipos,
                onTank: tank =>
                {
                    if (tank.Color == p.OwnerColor) return false;
                    if (tank.TakeDamage(p.Damage, DamageCause.EnemyFire, p.Speed))
                        self._events?.Record(new StepEvent(StepEventKind.TankHit, tank.Position, tank.Color));
                    self.RecordImpact(ipos, p, ImpactTarget.Tank);
                    return true;
                },
                // Like tanks, a bullet passes through its own side's machines and stops on the enemy's.
                onMachine: machine =>
                {
                    if (machine.OwnerColor == p.OwnerColor) return false;
                    bool wrecked = machine.TakeDamage(p.Damage);
                    self._events?.Record(new StepEvent(wrecked ? StepEventKind.MachineWrecked : StepEventKind.MachineDamaged,
                        machine.Position, machine.OwnerColor, machine.Type));
                    if (wrecked)
                        self.AddExplosion(machine.Position, Tweaks.Explosion.MachineWreck, Tweaks.Explosion.MachineWreckHeatScale);
                    self.RecordImpact(ipos, p, ImpactTarget.Machine);
                    return true;
                },
                // One hit kills an emberite; the shot bursts on it as on anything else.
                onEmberite: solver.CanHitEmberites ? KillsEmberite(solver, self, ipos, p) : null,
                onTerrain: pix =>
                {
                    // A gate is open ground to its owner's shells. An enemy's stop on it while the gate shield
                    // holds, and wear the shield down.
                    if (Pixel.IsBaseGate(pix))
                        return self.HitsGate(solver, ipos, p);
                    if (!Pixel.IsAnyCollision(pix)) return false;
                    // Walls and baffles take no damage; only a shell that reaches the core wears a base down.
                    if (Pixel.IsBaseCore(pix) && solver.EnemyBaseAt(ipos, p.OwnerColor) is { } target)
                    {
                        int raw = p.Damage;
                        target.TakeDamage(self._basePower?.CoreDamage(target, raw) ?? raw);
                        self._events?.Record(new StepEvent(target.IsDestroyed ? StepEventKind.BaseDestroyed : StepEventKind.BaseDamaged,
                            ipos, target.Color));
                        self.RecordImpact(ipos, p, ImpactTarget.Base);
                    }
                    else
                        self.RecordImpact(ipos, p, ImpactTarget.Terrain);
                    return true;
                });

            if (hit)
            {
                self.SpawnNormalExplosion(ipos);
                int bulletExplosionHeat = Math.Max(0, (int)MathF.Round(
                    Tweaks.Explosion.BulletHeatAmount * Tweaks.Explosion.BulletExplosionHeatScale));
                terrain.AddHeatRadius(ipos, bulletExplosionHeat, Tweaks.Explosion.BulletHeatRadius);
                p.IsAlive = false;
                return;
            }
        }
    }

    private static Func<int, bool> KillsEmberite(CollisionSolver solver, ProjectileList self, Position at, Projectile bullet) => emberite =>
    {
        solver.KillEmberite(emberite);
        self.RecordImpact(at, bullet, ImpactTarget.Terrain);
        return true;
    };

    private static Func<int, bool> KillsEmberite(CollisionSolver solver) => emberite =>
    {
        solver.KillEmberite(emberite);
        return true;
    };

    /// <summary>
    /// Whether <paramref name="bullet"/> stops on the gate cell at <paramref name="at"/>: an enemy's does while the
    /// gate shield holds, and the shield takes its damage (<see cref="BasePowerSystem.AbsorbGateHit"/>).
    /// </summary>
    private bool HitsGate(CollisionSolver solver, Position at, Projectile bullet)
    {
        if (_basePower == null || solver.EnemyBaseAt(at, bullet.OwnerColor) is not { } target)
            return false;
        GateHit hit = _basePower.AbsorbGateHit(target, bullet.Damage, solver.Terrain);
        if (hit == GateHit.Open)
            return false;
        _events?.Record(new StepEvent(hit == GateHit.Broken ? StepEventKind.BaseShieldBroken : StepEventKind.BaseShieldHit,
            at, target.Color));
        RecordImpact(at, bullet, ImpactTarget.Base);
        return true;
    }

    private void RecordImpact(Position at, Projectile bullet, ImpactTarget target) =>
        _events?.Record(new StepEvent(StepEventKind.BulletImpact, at, bullet.OwnerColor, Target: target));

    private void SpawnNormalExplosion(Position pos)
    {
        _events?.Record(new StepEvent(StepEventKind.Explosion, pos));
        AddRange(ExplosionFactory.CreateExplosion(
            pos,
            Tweaks.Explosion.Normal,
            ref _rng,
            Tweaks.Explosion.BulletExplosionHeatScale));
    }

    public void AddExplosion(Position pos, ExplosionParams p, float shrapnelHeatScale = 1f)
    {
        _events?.Record(new StepEvent(StepEventKind.Explosion, pos));
        AddRange(ExplosionFactory.CreateExplosion(pos, p, ref _rng, shrapnelHeatScale));
    }

    private static void AdvanceShrapnel(Projectile p, CollisionSolver solver, ProjectileList self)
    {
        var terrain = solver.Terrain;
        if (p.Life-- <= 0) { p.IsAlive = false; self.RecordShrapnelSpent(p); return; }

        if (!TryAdvanceInside(terrain, p, out var ipos))
            return;

        // A fragment that meets an emberite kills it and is spent.
        if (solver.CanHitEmberites && solver.TestPoint(ipos, onEmberite: KillsEmberite(solver)))
        {
            p.IsAlive = false;
            self.RecordShrapnelSpent(p);
            return;
        }

        var pix = terrain.GetPixelRaw(ipos);
        if (Pixel.IsBlockingCollision(pix))
        {
            if ((Pixel.IsConcrete(pix) && self._rng.Chance1000(Tweaks.Explosion.ChanceToDestroyConcrete)) ||
                (Pixel.IsRock(pix) && self._rng.Chance1000(Tweaks.Explosion.ChanceToDestroyRock)))
            {
                terrain.SetPixel(ipos, TerrainPixel.Decal);
            }
            int hitHeat = Math.Max(0, (int)MathF.Round(Tweaks.Explosion.ShrapnelHitHeat * p.HeatScale));
            terrain.AddHeat(ipos, hitHeat);
            p.IsAlive = false;
            self.RecordShrapnelSpent(p);
            return;
        }

        // Shrapnel flies over a base's floor and gates and leaves them as they are: a keep does not wear away.
        if (Pixel.IsAnyBaseCell(pix))
            return;
        terrain.SetPixel(ipos, TerrainPixel.Decal);
        int digHeat = Math.Max(0, (int)MathF.Round(Tweaks.Explosion.ShrapnelDigHeatAmount * p.HeatScale));
        terrain.AddHeatRadius(ipos, digHeat, Tweaks.Explosion.ShrapnelDigHeatRadius);
    }

    private void RecordShrapnelSpent(Projectile p) =>
        _events?.Record(new StepEvent(StepEventKind.ShrapnelSpent, (Position)p.Position));

    private static bool TryAdvanceInside(TerrainGrid terrain, Projectile p, out Position ipos)
    {
        p.Position += p.Speed;
        ipos = (Position)p.Position;
        if (!terrain.IsInside(ipos))
        {
            p.IsAlive = false;
            return false;
        }
        return true;
    }

    private static bool TryAdvanceInside(TerrainGrid terrain, Projectile p, VectorF step, out Position ipos)
    {
        p.Position += step;
        ipos = (Position)p.Position;
        if (!terrain.IsInside(ipos))
        {
            p.IsAlive = false;
            return false;
        }
        return true;
    }

    public int CopyRenderStates(Span<ProjectileRenderState> destination)
    {
        int count = Math.Min(destination.Length, _projectiles.Count);
        for (int i = 0; i < count; i++)
        {
            var p = _projectiles[i];
            destination[i] = new ProjectileRenderState(
                Position: p.Position,
                Velocity: p.Speed,
                Type: p.Type,
                IsAlive: p.IsAlive);
        }
        return count;
    }

    /// <summary>
    /// Mixes what <see cref="WorldStateHash.Compute"/> does not read: the generator explosions draw from, each live
    /// projectile's life, owner and heat, and the projectiles waiting to join the list next tick.
    /// </summary>
    internal ulong MixLatent(ulong hash)
    {
        hash = Fnv1a.Mix(hash, _rng.State);
        foreach (Projectile p in _projectiles)
            hash = MixUnhashed(hash, p);
        hash = Fnv1a.Mix(hash, _pending.Count);
        foreach (Projectile p in _pending)
        {
            hash = Fnv1a.Mix(Fnv1a.Mix(hash, p.Position.X), p.Position.Y);
            hash = Fnv1a.Mix(Fnv1a.Mix(hash, p.Speed.X), p.Speed.Y);
            hash = Fnv1a.Mix(hash, (int)p.Type);
            hash = Fnv1a.Mix(hash, p.IsAlive);
            hash = MixUnhashed(hash, p);
        }
        return hash;

        static ulong MixUnhashed(ulong hash, Projectile p)
        {
            hash = Fnv1a.Mix(Fnv1a.Mix(Fnv1a.Mix(hash, p.Life), p.OwnerColor), p.HeatScale);
            // Mixed only when it is not a tank's shell, so the digest of a world without turrets is what it always was.
            return p.Damage != Tweaks.Weapon.BulletDamage ? Fnv1a.Mix(hash, p.Damage) : hash;
        }
    }
}
