namespace Emberward.Core.Entities.Emberites;

using Emberward.Core.Collision;
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
    /// The creature a gun at <paramref name="from"/> shoots at: of the creatures out, the nearest within
    /// <paramref name="range"/> cells that a shot reaches through at most <paramref name="soilPierce"/> cells of soil
    /// (<see cref="Raycaster.HasShotThroughSoil"/>); on a tie the one longest out. With nothing out it looks at nothing.
    /// </summary>
    public bool TryFindTarget(Position from, int range, int soilPierce, out Emberite target)
    {
        target = default;
        bool found = false;
        long best = (long)range * range;
        var span = EmberiteSpan;
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly Emberite emberite = ref span[i];
            if (emberite.Gone)
                continue;
            var cell = (Position)emberite.Position;
            long distance = Position.DistanceSquared(cell, from);
            // Strictly nearer only once one is found: the list is in the order they came out, so the earliest keeps a tie.
            if (distance > best || (found && distance == best))
                continue;
            if (!Raycaster.HasShotThroughSoil(_terrain, from, cell, soilPierce))
                continue;
            target = emberite;
            best = distance;
            found = true;
        }
        return found;
    }

    /// <summary>
    /// Kills creature <paramref name="index"/> (an index <see cref="TestPoint"/> gave): what it carries spills into the
    /// ground where it dies, so holding one choke point cooks it, and every few deaths one drops a unit of its nest's
    /// deposit there (<see cref="DropHomeOre"/>). The creature leaves the list at the start of the swarm's next step.
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
        Nest nest = _nests[emberite.Nest];
        nest.Out--;
        var cell = (Position)emberite.Position;
        if (emberite.Charge > 0.0 && _terrain.IsInside(cell))
            _terrain.AddEnergyInRadiusArea(cell, Tweaks.Emberites.SpillRadius, emberite.Charge, TerrainGrid.ThermalField.Terrain);
        if (emberite.Pyrite > 0)
        {
            // What finds no room goes back to its nest's store, so that no unit is lost.
            nest.Pyrite += emberite.Pyrite - OreDrop.Scatter(_terrain, cell, emberite.Pyrite, _isOccupied);
            emberite.Pyrite = 0;
            _emberites[index] = emberite;
        }
        // Counted on the creatures the nest has left, which only deaths and starvation lower (a starved creature
        // that lands on a multiple skips that drop, which only makes ore scarcer): no counter of its own is kept.
        if (nest.Alive % Tweaks.Emberites.DropEveryKills == 0)
            DropHomeOre(nest, cell);
        _events.Record(new StepEvent(StepEventKind.EmberiteKilled, cell));
    }

    /// <summary>
    /// A dying creature drops one unit of its nest's deposit at <paramref name="at"/>: the plain ore cell of the nest's
    /// ground farthest from its centre (the first in row order on a tie) is cleared, and a loose ore cell lands where
    /// the creature died. The deposit is nothing but its cells, so this moves a unit and makes none: with no plain ore
    /// left in the nest's ground, or no room where the creature died, nothing happens. It looks at the nest's ground
    /// once per drop, never per tick.
    /// </summary>
    private void DropHomeOre(Nest nest, Position at)
    {
        Position? rim = null;
        long farthest = -1;
        int radius = nest.Radius;
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                long distance = (long)dx * dx + (long)dy * dy;
                if (distance > (long)radius * radius || distance <= farthest)
                    continue;
                var cell = new Position(nest.Centre.X + dx, nest.Centre.Y + dy);
                if (!_terrain.IsInside(cell) || _terrain.GetPixelRaw(cell) != TerrainPixel.Pyrite)
                    continue;
                rim = cell;
                farthest = distance;
            }
        if (rim is not Position taken || OreDrop.Scatter(_terrain, at, 1, _isOccupied) == 0)
            return;
        _terrain.SetPixel(taken, TerrainPixel.Blank);
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
        if (tank.TakeDamage(Tweaks.Emberites.BiteDamage, DamageCause.Emberite))
            TankDamageEvents.Record(_events, tank, tank.Position, hullEvent: false);
        _events.Record(new StepEvent(StepEventKind.EmberiteBit, tank.Position, tank.Color, Count: stolen));
    }
}
