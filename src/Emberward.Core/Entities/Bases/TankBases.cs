namespace Emberward.Core.Entities.Bases;

using Emberward.Core.Types;
using Emberward.Core.Terrain;
using Emberward.Core.Config;
using Emberward.Core.Network;

public class TankBases
{
    private readonly List<TankBase> _bases = new();
    private readonly SimulationSettings _settings;

    public TankBases(SimulationSettings? settings = null)
    {
        _settings = settings ?? SimulationSettings.FromTweaks();
    }

    public IReadOnlyList<TankBase> Bases => _bases;

    /// <param name="front">The side the base's tank spawns on (<see cref="TankBase.Front"/>).</param>
    public TankBase AddBase(Position position, int color, Offset? front = null)
    {
        var tankBase = new TankBase(position, color, _settings, front);
        _bases.Add(tankBase);
        return tankBase;
    }

    public TankBase? GetSpawn(int color)
    {
        return color >= 0 && color < _bases.Count ? _bases[color] : null;
    }

    public TankBase? CheckBaseCollision(Position pos)
    {
        foreach (var b in _bases)
            if (b.IsInside(pos))
                return b;
        return null;
    }

    /// <summary>The base whose cells, wall included, hold <paramref name="pos"/>.</summary>
    public TankBase? BaseOccupying(Position pos)
    {
        foreach (var b in _bases)
            if (b.OccupiesCell(pos))
                return b;
        return null;
    }

    /// <param name="gatesShut">Whether the gates start shut to the enemy (<see cref="TankBase.CreateInTerrain"/>).</param>
    public void CreateBasesInTerrain(ITerrainPixelAccessor terrain, bool gatesShut = false)
    {
        for (int i = 0; i < _bases.Count; i++)
            _bases[i].CreateInTerrain(terrain, i, gatesShut);
    }

    public void Advance(ITerrainHeatSource terrain, BasePowerSystem? power = null)
    {
        foreach (var b in _bases)
            b.Advance(terrain, canRepair: power?.IsDark(b) != true);
    }
}
