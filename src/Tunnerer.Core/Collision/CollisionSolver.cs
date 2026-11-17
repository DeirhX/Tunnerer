namespace Tunnerer.Core.Collision;

using Tunnerer.Core.Types;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Tanks;

/// <summary>
/// What stops a tank's step, in the order the move meets them. <see cref="Dirt"/> yields to one dig; <see cref="Hard"/>
/// (rock, concrete, ore) blocks until the torch or the dig wears it away; <see cref="Solid"/> (hot and frost rock,
/// base walls, the map edge) never yields; <see cref="Entity"/> is another tank or another side's machine.
/// </summary>
public enum StepObstacle { None, Dirt, Hard, Solid, Entity }

/// <summary>What <see cref="CollisionSolver.SurveyTankTerrain"/> found under a tank's hull.</summary>
/// <param name="Worst">The worst ground any hull cell stands on.</param>
/// <param name="DirtCells">Hull cells standing in dirt.</param>
/// <param name="Cells">Hull cells looked at.</param>
public readonly record struct HullGround(StepObstacle Worst, int DirtCells, int Cells)
{
    /// <summary>Share of the hull on open ground, 0 to 1; meaningful while <see cref="Worst"/> is nothing or dirt.</summary>
    public float OpenShare => Cells > 0 ? 1f - (float)DirtCells / Cells : 0f;
}

/// <summary>
/// Central collision API. Wraps terrain, tanks, and machines behind a single
/// visitor-based query. Uses <see cref="WorldSectors"/> for broad-phase on
/// entity queries so projectile-vs-tank checks are O(local) not O(n).
/// It also owns what stops a tank's hull: <see cref="TestTankTerrain"/> (the ground) and <see cref="BlocksTank"/>
/// (other tanks and machines); <see cref="ProbeTankStep"/> answers both for a step that has not been taken.
/// </summary>
public class CollisionSolver
{
    private readonly TerrainGrid _terrain;
    private readonly WorldSectors _tankSectors;
    private TankList? _tanks;
    private MachineList? _machines;

    public TerrainGrid Terrain => _terrain;

    public CollisionSolver(TerrainGrid terrain)
    {
        _terrain = terrain;
        _tankSectors = new WorldSectors(terrain.Size);
    }

    /// <summary>
    /// Rebuilds the broad-phase sector index for the current frame.
    /// Call once at the start of each simulation step.
    /// </summary>
    public void Update(TankList tanks, MachineList machines)
    {
        _tanks = tanks;
        _machines = machines;

        _tankSectors.Clear();
        for (int i = 0; i < tanks.Tanks.Count; i++)
        {
            var t = tanks.Tanks[i];
            if (!t.IsDead)
                _tankSectors.AddEntity(_tankSectors.SectorIdForPosition(t.Position), i);
        }
    }

    /// <summary>
    /// Tests a single point against terrain, tanks, and/or machines.
    /// Supply only the callbacks you care about; null callbacks are skipped.
    /// Callbacks receive the hit entity and return true to signal a hit (stops further checks).
    /// Tank checks use sector-based broad-phase; machine checks are linear (few entities).
    /// </summary>
    public bool TestPoint(Position pos,
        Func<Tank, bool>? onTank = null,
        Func<Machine, bool>? onMachine = null,
        Func<TerrainPixel, bool>? onTerrain = null)
    {
        if (onTank != null && _tanks != null)
        {
            bool tankHit = _tankSectors.ForEachNearbyEntity(pos, idx =>
            {
                var tank = _tanks.Tanks[idx];
                if (tank.IsDead) return false;
                if (!TankSpriteShape.Contains(tank.Direction, tank.Position, pos)) return false;
                return onTank(tank);
            });
            if (tankHit) return true;
        }

        if (onMachine != null && _machines != null)
        {
            foreach (var m in _machines.Machines)
            {
                if (!m.IsAlive || !m.IsBlockingCollision) continue;
                if (m.TestCollide(pos) && onMachine(m)) return true;
            }
        }

        if (onTerrain != null && _terrain.IsInside(pos))
        {
            var pix = _terrain.GetPixelRaw(pos);
            if (onTerrain(pix)) return true;
        }

        return false;
    }

    /// <summary>
    /// The worst ground under the hull of a tank at <paramref name="pos"/> facing sprite direction
    /// <paramref name="dir"/> (<see cref="TankSpriteShape"/>). Anything but <see cref="StepObstacle.None"/> stops a step.
    /// </summary>
    public static StepObstacle TestTankTerrain(ITerrainPixelReadOnly terrain, Position pos, int dir) =>
        SurveyTankTerrain(terrain, pos, dir).Worst;

    /// <summary>
    /// The ground under the hull of a tank at <paramref name="pos"/> facing <paramref name="dir"/>: the worst of it,
    /// and how much of the hull stands in dirt. The counts stop at the first <see cref="StepObstacle.Solid"/> cell.
    /// </summary>
    public static HullGround SurveyTankTerrain(ITerrainPixelReadOnly terrain, Position pos, int dir)
    {
        var worst = StepObstacle.None;
        int cells = 0, dirt = 0;
        TankSpriteShape.ForEachPixel(dir, pos, (_, worldPos) =>
        {
            cells++;
            if (!terrain.IsInside(worldPos)) { worst = StepObstacle.Solid; return false; }
            var pix = terrain.GetPixelRaw(worldPos);
            if (Pixel.IsBlockingCollision(pix))
            {
                if (!Pixel.IsTorchable(pix) && !Pixel.IsOre(pix)) { worst = StepObstacle.Solid; return false; }
                worst = StepObstacle.Hard;
            }
            else if (Pixel.IsSoftCollision(pix))
            {
                dirt++;
                if (worst == StepObstacle.None)
                    worst = StepObstacle.Dirt;
            }
            return true;
        });
        return new HullGround(worst, dirt, cells);
    }

    /// <summary>
    /// Whether another live tank, or a planted machine of another side, stops <paramref name="tank"/> from standing at
    /// <paramref name="pos"/> facing <paramref name="dir"/>. A tank's own machines never block it (as its own bullets
    /// pass them). Something the tank already overlaps where it stands (it respawned onto an intruder, a harvester
    /// crawled against it) does not hold it, so two overlapping bodies can always part.
    /// </summary>
    public bool BlocksTank(Tank tank, Position pos, int dir)
    {
        if (_tanks != null)
        {
            foreach (var other in _tanks.Tanks)
            {
                if (ReferenceEquals(other, tank) || other.IsDead) continue;
                if (TankSpriteShape.Overlaps(dir, pos, other.Direction, other.Position) &&
                    !TankSpriteShape.Overlaps(tank.Direction, tank.Position, other.Direction, other.Position))
                    return true;
            }
        }

        if (_machines != null)
        {
            foreach (var m in _machines.Machines)
            {
                if (!m.IsAlive || !m.IsBlockingCollision || m.OwnerColor == tank.Color) continue;
                int half = m.BoundingBox.HalfWidth;
                if (TankSpriteShape.OverlapsSquare(dir, pos, m.Position, half) &&
                    !TankSpriteShape.OverlapsSquare(tank.Direction, tank.Position, m.Position, half))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What would stop <paramref name="tank"/> stepping by <paramref name="move"/> from where it stands, in the order
    /// its move meets them (<c>TankDigger.Move</c>): the ground under the new hull first, then tanks and machines.
    /// </summary>
    public StepObstacle ProbeTankStep(Tank tank, Offset move)
    {
        int dir = TankSpriteShape.DirectionOf(move);
        var pos = tank.Position + move;
        var ground = TestTankTerrain(_terrain, pos, dir);
        if (ground != StepObstacle.None)
            return ground;
        return BlocksTank(tank, pos, dir) ? StepObstacle.Entity : StepObstacle.None;
    }
}
