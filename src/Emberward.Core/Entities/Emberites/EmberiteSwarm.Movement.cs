namespace Emberward.Core.Entities.Emberites;

using Emberward.Core.Collision;
using Emberward.Core.Config;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

public sealed partial class EmberiteSwarm
{
    // Unit vector of a 45 degree turn (cos 45 = sin 45), so that turning needs no trigonometry.
    private const float Diagonal = 0.70710678f;

    private void AdvanceEmberites()
    {
        // Creatures killed or home last step leave the list first, so that nothing below meets a dead one.
        _emberites.RemoveAll(static m => m.Gone);
        var span = EmberiteSpan;
        for (int i = 0; i < span.Length; i++)
        {
            ref Emberite emberite = ref span[i];
            CreatureSteps++;
            Move(ref emberite);
            if (emberite.State == EmberiteState.Hunting)
                Hunt(ref emberite);
            else
                GoHome(ref emberite);
        }
        _emberites.RemoveAll(static m => m.Gone);
    }

    /// <summary>Moves a creature by the velocity it committed last step; ground that has closed in the meantime stops it.</summary>
    private void Move(ref Emberite emberite)
    {
        if (emberite.Velocity.X == 0f && emberite.Velocity.Y == 0f)
            return;
        var next = new PositionF(emberite.Position.X + emberite.Velocity.X, emberite.Position.Y + emberite.Velocity.Y);
        if (GroundAt(next, _nests[emberite.Nest]) == EmberiteGround.Blocked)
        {
            emberite.Velocity = default;
            return;
        }
        emberite.Position = next;
        WearTrail(ref emberite, next);
    }

    /// <summary>A creature that has moved into a soil block wears its trail, once for each time it enters the block.</summary>
    private void WearTrail(ref Emberite emberite, PositionF at)
    {
        var cell = (Position)at;
        int block = _trails.BlockOf(cell);
        if (block < 0 || Classify(_terrain.GetPixelRaw(cell), cell, _nests[emberite.Nest].Centre, _nests[emberite.Nest].Radius) != EmberiteGround.Soil)
        {
            emberite.WornBlock = 0;
            return;
        }
        if (emberite.WornBlock != block + 1)
        {
            emberite.WornBlock = block + 1;
            _trails.Wear(block, _tick);
        }
    }

    /// <summary>Whether the soil at <paramref name="cell"/> is a lane, worn so that creatures run through it as through open ground.</summary>
    private bool IsLane(Position cell, int tick) => _trails.IsLane(_trails.BlockOf(cell), tick);

    private void Hunt(ref Emberite emberite)
    {
        Nest nest = _nests[emberite.Nest];
        Tank? tank = ValidTarget(emberite.Target) is { } t && TargetStillWanted(ref emberite, nest, t) ? t : null;
        if (tank is null)
        {
            // The nest's own pick, if it has one; else home.
            if (nest.Target >= 0 && ValidTarget(nest.Target) is { } other)
            {
                emberite.Target = nest.Target;
                emberite.SightTick = 0;
                emberite.Sees = false;
                tank = other;
            }
            else
            {
                emberite.State = EmberiteState.Returning;
                emberite.Target = -1;
                GoHome(ref emberite);
                return;
            }
        }

        float dx = tank.Position.X - emberite.Position.X, dy = tank.Position.Y - emberite.Position.Y;
        float range = Tweaks.Emberites.BiteRange;
        if (dx * dx + dy * dy <= range * range)
        {
            if (emberite.Charge > 0.0 && !tank.IsDockedAtOwnBase && tank.SpawnProtectionTicks == 0)
            {
                Bite(ref emberite, tank);
                GoHome(ref emberite);
            }
            else
            {
                // Waiting at the tank (a base shields it, or it has just respawned): hold still.
                emberite.Velocity = default;
            }
            return;
        }
        Steer(ref emberite, 0, emberite.Target, tank.Position, nest);
    }

    private Tank? ValidTarget(int index)
    {
        var tanks = _tanks.Tanks;
        return index >= 0 && index < tanks.Count && !tanks[index].IsDead ? tanks[index] : null;
    }

    /// <summary>
    /// A creature keeps its target while the tank is the nest's thief, or warm enough and within its heat leash of the
    /// nest, or in sight: a line clear of rock from the creature, within <see cref="Tweaks.Emberites.HuntLeashCells"/>,
    /// looked for once in <see cref="Tweaks.Emberites.SightCheckTicks"/> ticks.
    /// </summary>
    private bool TargetStillWanted(ref Emberite emberite, Nest nest, Tank tank)
    {
        if (nest.Thief == emberite.Target && nest.ThiefTicks > 0)
            return true;
        float leash = HeatLeash(tank.Heat);
        if (leash > 0f && Position.DistanceSquared(tank.Position, nest.Centre) <= leash * leash)
            return true;

        long reach = (long)Tweaks.Emberites.HuntLeashCells * Tweaks.Emberites.HuntLeashCells;
        var here = (Position)emberite.Position;
        if (Position.DistanceSquared(tank.Position, here) > reach)
        {
            emberite.Sees = false;
            return false;
        }
        if (emberite.SightTick == 0 || _tick - emberite.SightTick >= Tweaks.Emberites.SightCheckTicks)
        {
            emberite.Sees = Raycaster.HasCreatureLine(_terrain, here, tank.Position);
            emberite.SightTick = _tick;
        }
        return emberite.Sees;
    }

    private void GoHome(ref Emberite emberite)
    {
        emberite.State = EmberiteState.Returning;
        Nest nest = _nests[emberite.Nest];
        float dx = nest.Centre.X - emberite.Position.X, dy = nest.Centre.Y - emberite.Position.Y;
        float arrive = nest.Radius * 0.5f + 1f;
        if (dx * dx + dy * dy <= arrive * arrive)
        {
            nest.Resting++;
            nest.Stored += emberite.Charge;
            nest.Pyrite += emberite.Pyrite;
            emberite.Pyrite = 0;
            nest.Out--;
            emberite.Gone = true;
            emberite.Velocity = default;
            return;
        }
        Steer(ref emberite, 1, emberite.Nest, nest.Centre, nest);
    }

    /// <summary>
    /// Chooses the velocity for the next step: toward the goal by the flow field where the creature is inside its
    /// window, straight otherwise, at the speed of the ground it would land on, turning aside from what blocks it.
    /// </summary>
    private void Steer(ref Emberite emberite, int fieldKind, int fieldId, Position goal, Nest nest)
    {
        float dirX, dirY;
        EmberiteFlowField field = FieldFor(fieldKind, fieldId, goal, nest);
        field.Used = true;
        if (!field.TryDirection(emberite.Position.X, emberite.Position.Y, out dirX, out dirY))
        {
            dirX = goal.X - emberite.Position.X;
            dirY = goal.Y - emberite.Position.Y;
            float length = MathF.Sqrt(dirX * dirX + dirY * dirY);
            if (length < 1e-4f)
            {
                emberite.Velocity = default;
                return;
            }
            dirX /= length;
            dirY /= length;
        }
        Commit(ref emberite, dirX, dirY, nest);
    }

    /// <summary>
    /// Takes the first heading of straight on, 45 degrees to either side, then 90 degrees to either side (the side
    /// alternating by creature, so a crowd does not all slide the same way) that it can move along, at the speed its
    /// landing cell allows.
    /// </summary>
    private void Commit(ref Emberite emberite, float dirX, float dirY, Nest nest)
    {
        float turn = (emberite.Id & 1) == 0 ? 1f : -1f;
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
            float speed = SpeedAlong(emberite.Position, ux, uy, nest);
            if (speed > 0f)
            {
                emberite.Velocity = new VectorF(ux * speed, uy * speed);
                return;
            }
        }
        emberite.Velocity = default;
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
        float open = Tweaks.Emberites.OpenSpeed, soil = Tweaks.Emberites.SoilSpeed;
        if (RunsAt(new PositionF(at.X + ux * open * 0.5f, at.Y + uy * open * 0.5f), nest) &&
            RunsAt(new PositionF(at.X + ux * open, at.Y + uy * open), nest))
            return open;
        return GroundAt(new PositionF(at.X + ux * soil, at.Y + uy * soil), nest) != EmberiteGround.Blocked ? soil : 0f;
    }

    /// <summary>Whether a creature of <paramref name="nest"/> can run at <paramref name="at"/>: open ground, or a lane worn into soil.</summary>
    private bool RunsAt(PositionF at, Nest nest)
    {
        EmberiteGround ground = GroundAt(at, nest);
        return ground == EmberiteGround.Open || (ground == EmberiteGround.Soil && IsLane((Position)at, _tick));
    }

    /// <summary>The ground a creature of <paramref name="nest"/> finds at <paramref name="at"/>.</summary>
    private EmberiteGround GroundAt(PositionF at, Nest nest)
    {
        var cell = (Position)at;
        if (at.X < 0f || at.Y < 0f || !_terrain.IsInside(cell))
            return EmberiteGround.Blocked;
        return Classify(_terrain.GetPixelRaw(cell), cell, nest.Centre, nest.Radius);
    }

    // ---- Fields ----------------------------------------------------------------------------------------------

    private EmberiteFlowField FieldFor(int kind, int id, Position goal, Nest nest)
    {
        EmberiteFlowField? field = null;
        foreach (EmberiteFlowField existing in _fields)
            if (existing.Kind == kind && existing.Id == id)
            {
                field = existing;
                break;
            }
        if (field is null)
        {
            field = new EmberiteFlowField { Kind = kind, Id = id };
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
            EmberiteFlowField field = _fields[i];
            field.Tick();
            if (field.Used)
            {
                field.Idle = 0;
                field.Used = false;
            }
            else if (++field.Idle >= Tweaks.Emberites.FieldRefreshTicks)
            {
                _fields.RemoveAt(i);
            }
        }
    }
}
