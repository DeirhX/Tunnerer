namespace Emberward.Core.Entities.Emberites;

using Emberward.Core.Config;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Resources;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

public sealed partial class EmberiteSwarm
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
        for (int i = 0; i < _emberites.Count; i++)
        {
            if (_emberites[i].Gone)
                continue;
            _sectors.AddEntity(_sectors.SectorIdForPosition((Position)_emberites[i].Position), i);
            _indexed = true;
        }
    }

    /// <summary>Creatures are out and filed by sector, so a shot can hit one.</summary>
    public bool HasCreatures => _indexed;

    /// <summary>
    /// Whether a creature is within <see cref="Tweaks.Emberites.HitRadius"/> of cell <paramref name="at"/>; the first one
    /// <paramref name="onHit"/> accepts (it is given the creature's index and returns true to stop) ends the search.
    /// </summary>
    public bool TestPoint(Position at, Func<int, bool> onHit)
    {
        if (!_indexed)
            return false;
        float radius = Tweaks.Emberites.HitRadius;
        return _sectors.ForEachNearbyEntity(at, index =>
        {
            if (index >= _emberites.Count)
                return false;
            Emberite emberite = _emberites[index];
            if (emberite.Gone)
                return false;
            float dx = emberite.Position.X - at.X, dy = emberite.Position.Y - at.Y;
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
        if (index < 0 || index >= _emberites.Count)
            return;
        Emberite emberite = _emberites[index];
        if (emberite.Gone)
            return;
        emberite.Gone = true;
        _emberites[index] = emberite;
        _nests[emberite.Nest].Out--;
        var cell = (Position)emberite.Position;
        if (emberite.Charge > 0.0 && _terrain.IsInside(cell))
            _terrain.AddEnergyInRadiusArea(cell, Tweaks.Emberites.SpillRadius, emberite.Charge, TerrainGrid.ThermalField.Terrain);
        if (emberite.Pyrite > 0)
        {
            // What finds no room goes back to its nest's store, so that no unit is lost.
            _nests[emberite.Nest].Pyrite += emberite.Pyrite - OreDrop.Scatter(_terrain, cell, emberite.Pyrite, _isOccupied);
            emberite.Pyrite = 0;
            _emberites[index] = emberite;
        }
        _events.Record(new StepEvent(StepEventKind.EmberiteKilled, cell));
    }

    /// <summary>
    /// A bite: the creature's whole charge goes into the tank (its overheat rule may do more damage), the bite itself
    /// does <see cref="Tweaks.Emberites.BiteDamage"/>, and up to <see cref="Tweaks.Emberites.BiteStealUnits"/> of pyrite
    /// leaves the tank's hold for the creature to carry home. The event's count is the pyrite taken.
    /// </summary>
    private void Bite(ref Emberite emberite, Tank tank)
    {
        tank.Heat += (float)(emberite.Charge / tank.ThermalCapacity);
        emberite.Charge = 0.0;
        int stolen = tank.Hold.Pay(Tweaks.Emberites.BiteStealUnits) ? Tweaks.Emberites.BiteStealUnits : 0;
        emberite.Pyrite += stolen;
        tank.TakeDamage(Tweaks.Emberites.BiteDamage, DamageCause.Emberite);
        _events.Record(new StepEvent(StepEventKind.EmberiteBit, tank.Position, tank.Color, Count: stolen));
    }
}
