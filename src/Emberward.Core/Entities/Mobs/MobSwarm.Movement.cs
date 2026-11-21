namespace Emberward.Core.Entities.Mobs;

using Emberward.Core.Config;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

public sealed partial class MobSwarm
{
    // Unit vector of a 45 degree turn (cos 45 = sin 45), so that turning needs no trigonometry.
    private const float Diagonal = 0.70710678f;

    private void AdvanceMobs()
    {
        // Creatures killed or home last step leave the list first, so that nothing below meets a dead one.
        _mobs.RemoveAll(static m => m.Gone);
        var span = MobSpan;
        for (int i = 0; i < span.Length; i++)
        {
            ref Mob mob = ref span[i];
            CreatureSteps++;
            Move(ref mob);
            if (mob.State == MobState.Hunting)
                Hunt(ref mob);
            else
                GoHome(ref mob);
        }
        _mobs.RemoveAll(static m => m.Gone);
    }

    /// <summary>Moves a creature by the velocity it committed last step; ground that has closed in the meantime stops it.</summary>
    private void Move(ref Mob mob)
    {
        if (mob.Velocity.X == 0f && mob.Velocity.Y == 0f)
            return;
        var next = new PositionF(mob.Position.X + mob.Velocity.X, mob.Position.Y + mob.Velocity.Y);
        if (GroundAt(next, _nests[mob.Nest]) == MobGround.Blocked)
        {
            mob.Velocity = default;
            return;
        }
        mob.Position = next;
        WearTrail(ref mob, next);
    }

    /// <summary>A creature that has moved into a soil block wears its trail, once for each time it enters the block.</summary>
    private void WearTrail(ref Mob mob, PositionF at)
    {
        var cell = (Position)at;
        int block = _trails.BlockOf(cell);
        if (block < 0 || Classify(_terrain.GetPixelRaw(cell), cell, _nests[mob.Nest].Centre, _nests[mob.Nest].Radius) != MobGround.Soil)
        {
            mob.WornBlock = 0;
            return;
        }
        if (mob.WornBlock != block + 1)
        {
            mob.WornBlock = block + 1;
            _trails.Wear(block, _tick);
        }
    }

    /// <summary>Whether the soil at <paramref name="cell"/> is a lane, worn so that creatures run through it as through open ground.</summary>
    private bool IsLane(Position cell, int tick) => _trails.IsLane(_trails.BlockOf(cell), tick);

    private void Hunt(ref Mob mob)
    {
        Nest nest = _nests[mob.Nest];
        Tank? tank = ValidTarget(mob.Target) is { } t && TargetStillWanted(nest, mob.Target, t) ? t : null;
        if (tank is null)
        {
            // The nest's own pick, if it has one; else home.
            if (nest.Target >= 0 && ValidTarget(nest.Target) is { } other)
            {
                mob.Target = nest.Target;
                tank = other;
            }
            else
            {
                mob.State = MobState.Returning;
                mob.Target = -1;
                GoHome(ref mob);
                return;
            }
        }

        float dx = tank.Position.X - mob.Position.X, dy = tank.Position.Y - mob.Position.Y;
        float range = Tweaks.Mobs.BiteRange;
        if (dx * dx + dy * dy <= range * range)
        {
            if (mob.Charge > 0.0 && !tank.IsDockedAtOwnBase && tank.SpawnProtectionTicks == 0)
            {
                Bite(ref mob, tank);
                GoHome(ref mob);
            }
            else
            {
                // Waiting at the tank (a base shields it, or it has just respawned): hold still.
                mob.Velocity = default;
            }
            return;
        }
        Steer(ref mob, 0, mob.Target, tank.Position, nest);
    }

    private Tank? ValidTarget(int index)
    {
        var tanks = _tanks.Tanks;
        return index >= 0 && index < tanks.Count && !tanks[index].IsDead ? tanks[index] : null;
    }

    /// <summary>A creature keeps its target while the tank is warm enough, or is the nest's thief.</summary>
    private static bool TargetStillWanted(Nest nest, int index, Tank tank) =>
        tank.Heat >= Tweaks.Mobs.LoseScentHeat || (nest.Thief == index && nest.ThiefTicks > 0);

    private void GoHome(ref Mob mob)
    {
        mob.State = MobState.Returning;
        Nest nest = _nests[mob.Nest];
        float dx = nest.Centre.X - mob.Position.X, dy = nest.Centre.Y - mob.Position.Y;
        float arrive = nest.Radius * 0.5f + 1f;
        if (dx * dx + dy * dy <= arrive * arrive)
        {
            nest.Resting++;
            nest.Stored += mob.Charge;
            nest.Out--;
            mob.Gone = true;
            mob.Velocity = default;
            return;
        }
        Steer(ref mob, 1, mob.Nest, nest.Centre, nest);
    }

    /// <summary>
    /// Chooses the velocity for the next step: toward the goal by the flow field where the creature is inside its
    /// window, straight otherwise, at the speed of the ground it would land on, turning aside from what blocks it.
    /// </summary>
    private void Steer(ref Mob mob, int fieldKind, int fieldId, Position goal, Nest nest)
    {
        float dirX, dirY;
        MobFlowField field = FieldFor(fieldKind, fieldId, goal, nest);
        field.Used = true;
        if (!field.TryDirection(mob.Position.X, mob.Position.Y, out dirX, out dirY))
        {
            dirX = goal.X - mob.Position.X;
            dirY = goal.Y - mob.Position.Y;
            float length = MathF.Sqrt(dirX * dirX + dirY * dirY);
            if (length < 1e-4f)
            {
                mob.Velocity = default;
                return;
            }
            dirX /= length;
            dirY /= length;
        }
        Commit(ref mob, dirX, dirY, nest);
    }

    /// <summary>
    /// Takes the first heading of straight on, 45 degrees to either side, then 90 degrees to either side (the side
    /// alternating by creature, so a crowd does not all slide the same way) that it can move along, at the speed its
    /// landing cell allows.
    /// </summary>
    private void Commit(ref Mob mob, float dirX, float dirY, Nest nest)
    {
        float turn = (mob.Id & 1) == 0 ? 1f : -1f;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            float ux = dirX, uy = dirY;
            switch (attempt)
            {
                case 1: (ux, uy) = Rotate(dirX, dirY, turn); break;
                case 2: (ux, uy) = Rotate(dirX, dirY, -turn); break;
                case 3: (ux, uy) = (-dirY * turn, dirX * turn); break;
                case 4: (ux, uy) = (dirY * turn, -dirX * turn); break;
            }
            float speed = SpeedAlong(mob.Position, ux, uy, nest);
            if (speed > 0f)
            {
                mob.Velocity = new VectorF(ux * speed, uy * speed);
                return;
            }
        }
        mob.Velocity = default;
    }

    private static (float X, float Y) Rotate(float x, float y, float side) =>
        ((x - side * y) * Diagonal, (y + side * x) * Diagonal);

    /// <summary>
    /// How fast a creature at <paramref name="at"/> may go along the unit vector (<paramref name="ux"/>,
    /// <paramref name="uy"/>): the open speed when both the middle and the end of that step are open ground, the soil
    /// speed when the end of a slow step is open or soil, nothing when rock, ore or a base is in the way.
    /// </summary>
    private float SpeedAlong(PositionF at, float ux, float uy, Nest nest)
    {
        float open = Tweaks.Mobs.OpenSpeed, soil = Tweaks.Mobs.SoilSpeed;
        if (RunsAt(new PositionF(at.X + ux * open * 0.5f, at.Y + uy * open * 0.5f), nest) &&
            RunsAt(new PositionF(at.X + ux * open, at.Y + uy * open), nest))
            return open;
        return GroundAt(new PositionF(at.X + ux * soil, at.Y + uy * soil), nest) != MobGround.Blocked ? soil : 0f;
    }

    /// <summary>Whether a creature of <paramref name="nest"/> can run at <paramref name="at"/>: open ground, or a lane worn into soil.</summary>
    private bool RunsAt(PositionF at, Nest nest)
    {
        MobGround ground = GroundAt(at, nest);
        return ground == MobGround.Open || (ground == MobGround.Soil && IsLane((Position)at, _tick));
    }

    /// <summary>The ground a creature of <paramref name="nest"/> finds at <paramref name="at"/>.</summary>
    private MobGround GroundAt(PositionF at, Nest nest)
    {
        var cell = (Position)at;
        if (at.X < 0f || at.Y < 0f || !_terrain.IsInside(cell))
            return MobGround.Blocked;
        return Classify(_terrain.GetPixelRaw(cell), cell, nest.Centre, nest.Radius);
    }

    // ---- Fields ----------------------------------------------------------------------------------------------

    private MobFlowField FieldFor(int kind, int id, Position goal, Nest nest)
    {
        MobFlowField? field = null;
        foreach (MobFlowField existing in _fields)
            if (existing.Kind == kind && existing.Id == id)
            {
                field = existing;
                break;
            }
        if (field is null)
        {
            field = new MobFlowField { Kind = kind, Id = id };
            _fields.Add(field);
        }
        if (field.NeedsBuild(goal))
        {
            if (kind == 1)
                field.Build(_terrain, goal, _queue, _trails, _tick, nest.Centre, nest.Radius);
            else
                field.Build(_terrain, goal, _queue, _trails, _tick);
        }
        return field;
    }

    /// <summary>Ages the fields, and forgets those no creature has read for a refresh period.</summary>
    private void AdvanceFields()
    {
        for (int i = _fields.Count - 1; i >= 0; i--)
        {
            MobFlowField field = _fields[i];
            field.Tick();
            if (field.Used)
            {
                field.Idle = 0;
                field.Used = false;
            }
            else if (++field.Idle >= Tweaks.Mobs.FieldRefreshTicks)
            {
                _fields.RemoveAt(i);
            }
        }
    }
}
