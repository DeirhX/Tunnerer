namespace Tunnerer.Core.Collision;

using Tunnerer.Core.Types;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Tanks;

public enum CollisionType { None, Dirt, Blocked }

/// <summary>
/// Central collision API. Wraps terrain, tanks, and machines behind a single
/// visitor-based query. Uses <see cref="WorldSectors"/> for broad-phase on
/// entity queries so projectile-vs-tank checks are O(local) not O(n).
/// </summary>
public class CollisionSolver
{
    private readonly TerrainGrid _terrain;
    private readonly WorldSectors _tankSectors;
    private TankList? _tanks;
    private MachineList? _machines;
    private readonly List<List<Machine>> _nearMachines = new();
    private int _nearDepth;

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
    /// Tank checks use sector-based broad-phase; machine checks look at the machines filed near the point.
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
            // In list order: the first machine the callback accepts ends the test. A callback may test another
            // point before it returns, so each test in progress has a scratch list of its own.
            if (_nearDepth == _nearMachines.Count)
                _nearMachines.Add(new List<Machine>());
            var near = _nearMachines[_nearDepth++];
            try
            {
                _machines.CollectNear(pos, Config.Tweaks.Machine.BoundingBoxHalfSize, near);
                foreach (var m in near)
                {
                    if (!m.IsAlive || !m.IsBlockingCollision) continue;
                    if (m.TestCollide(pos) && onMachine(m)) return true;
                }
            }
            finally
            {
                _nearDepth--;
            }
        }

        if (onTerrain != null && _terrain.IsInside(pos))
        {
            var pix = _terrain.GetPixelRaw(pos);
            if (onTerrain(pix)) return true;
        }

        return false;
    }
}
