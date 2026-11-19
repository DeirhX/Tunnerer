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

    public TankBase AddBase(Position position, int color)
    {
        var tankBase = new TankBase(position, color, _settings);
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

    public void CreateBasesInTerrain(ITerrainPixelAccessor terrain)
    {
        for (int i = 0; i < _bases.Count; i++)
            _bases[i].CreateInTerrain(terrain, i);
    }

    public void Advance(ITerrainHeatSource terrain, BasePowerSystem? power = null)
    {
        foreach (var b in _bases)
            b.Advance(terrain, canRepair: power?.IsDark(b) != true);
    }
}
