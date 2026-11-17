namespace Tunnerer.Core.Entities.Bases;

using Tunnerer.Core.Types;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Config;

public class TankBases
{
    private readonly List<TankBase> _bases = new();
    private readonly SimulationSettings _settings;

    public TankBases(SimulationSettings? settings = null)
    {
        _settings = settings ?? SimulationSettings.FromTweaks();
    }

    public IReadOnlyList<TankBase> Bases => _bases;

    public void AddBase(Position position, int color)
    {
        _bases.Add(new TankBase(position, color, _settings));
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

    public void CreateBasesInTerrain(ITerrainPixelAccessor terrain)
    {
        for (int i = 0; i < _bases.Count; i++)
            _bases[i].CreateInTerrain(terrain, i);
    }

    public void Advance(ITerrainHeatSource terrain)
    {
        foreach (var b in _bases)
            b.Advance(terrain);
    }
}
