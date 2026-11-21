namespace Emberward.Core.Entities.Mobs;

using Emberward.Core.Config;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

public sealed partial class MobSwarm
{
    /// <summary>
    /// Files the creatures out by sector, for shots to find them (<see cref="Collision.CollisionSolver.Update"/> calls it
    /// first thing in a step). With nothing out it does nothing, and clears nothing it did not fill.
    /// </summary>
    public void IndexCreatures()
    {
        if (_indexed)
        {
            _sectors.Clear();
            _indexed = false;
        }
        for (int i = 0; i < _mobs.Count; i++)
        {
            if (_mobs[i].Gone)
                continue;
            _sectors.AddEntity(_sectors.SectorIdForPosition((Position)_mobs[i].Position), i);
            _indexed = true;
        }
    }

    /// <summary>Creatures are out and filed by sector, so a shot can hit one.</summary>
    public bool HasCreatures => _indexed;

    /// <summary>
    /// Whether a creature is within <see cref="Tweaks.Mobs.HitRadius"/> of cell <paramref name="at"/>; the first one
    /// <paramref name="onHit"/> accepts (it is given the creature's index and returns true to stop) ends the search.
    /// </summary>
    public bool TestPoint(Position at, Func<int, bool> onHit)
    {
        if (!_indexed)
            return false;
        float radius = Tweaks.Mobs.HitRadius;
        return _sectors.ForEachNearbyEntity(at, index =>
        {
            if (index >= _mobs.Count)
                return false;
            Mob mob = _mobs[index];
            if (mob.Gone)
                return false;
            float dx = mob.Position.X - at.X, dy = mob.Position.Y - at.Y;
            return dx * dx + dy * dy <= radius * radius && onHit(index);
        });
    }

    /// <summary>
    /// Kills creature <paramref name="index"/> (an index <see cref="TestPoint"/> gave): what it carries spills into the
    /// ground where it dies, so holding one choke point cooks it. The creature leaves the list at the start of the
    /// swarm's next step.
    /// </summary>
    public void Kill(int index)
    {
        if (index < 0 || index >= _mobs.Count)
            return;
        Mob mob = _mobs[index];
        if (mob.Gone)
            return;
        mob.Gone = true;
        _mobs[index] = mob;
        _nests[mob.Nest].Out--;
        var cell = (Position)mob.Position;
        if (mob.Charge > 0.0 && _terrain.IsInside(cell))
            _terrain.AddEnergyInRadiusArea(cell, Tweaks.Mobs.SpillRadius, mob.Charge, TerrainGrid.ThermalField.Terrain);
        _events.Record(new StepEvent(StepEventKind.MobKilled, cell));
    }

    /// <summary>A bite: the creature's whole charge goes into the tank, whose overheat rule does the damage.</summary>
    private void Bite(ref Mob mob, Tank tank)
    {
        tank.Heat += (float)(mob.Charge / tank.ThermalCapacity);
        mob.Charge = 0.0;
        _events.Record(new StepEvent(StepEventKind.MobBit, tank.Position, tank.Color));
    }
}
