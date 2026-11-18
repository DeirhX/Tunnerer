namespace Tunnerer.Core.Terrain;

using Tunnerer.Core.Types;

/// <summary>What the sync digest (<see cref="WorldStateHash.ComputeSync"/>) reads of the terrain beyond its pixels.</summary>
public partial class TerrainGrid
{
    /// <summary>
    /// Mixes every cell's heat and air temperature, in cell order, so heat in the wrong place is seen even when the
    /// total is right. The same walk also hashes scorch intensity into <paramref name="scorch"/>, cell by cell where
    /// it is not zero. Walks the whole grid: the digest is taken once a second, not every tick.
    /// </summary>
    internal ulong MixCellFields(ulong hash, out ulong scorch)
    {
        scorch = Fnv1a.Offset;
        for (int i = 0; i < _heatTemperature.Length; i++)
        {
            hash = Fnv1a.Mix(Fnv1a.Mix(hash, _heatTemperature[i]), _airTemperature[i]);
            if (_scorchIntensity[i] != 0)
                scorch = Fnv1a.Mix(Fnv1a.Mix(scorch, i), (uint)_scorchIntensity[i]);
        }
        return hash;
    }

    /// <summary>Mixes which heat tiles are awake and the solver's throttle counters, for the sync digest.</summary>
    internal ulong MixHeatActivity(ulong hash) => _thermalScheduler.MixLatent(_thermalActivity.MixLatent(hash));
}
