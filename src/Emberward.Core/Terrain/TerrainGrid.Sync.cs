namespace Emberward.Core.Terrain;

using System.Runtime.InteropServices;
using Emberward.Core.Types;

/// <summary>What the sync digest (<see cref="WorldStateHash.ComputeSync"/>) reads of the terrain beyond its pixels.</summary>
public partial class TerrainGrid
{
    /// <summary>
    /// Mixes every cell's heat and air temperature, in cell order, so heat in the wrong place is seen even when the
    /// total is right. The same walk also hashes scorch intensity into <paramref name="scorch"/>, cell by cell where
    /// it is not zero. Walks the whole grid: the digest is taken once a second, not every tick. It is paid by every
    /// player of a network match, so it must cost no more than the energy total it replaced: a cell's two
    /// temperatures go into one 64-bit word, four cells' words into four independent lanes (so the multiplies do not
    /// wait on each other), and scorch is skipped eight cells at a time where it is zero.
    /// </summary>
    internal ulong MixCellFields(ulong hash, out ulong scorch)
    {
        ReadOnlySpan<uint> heat = MemoryMarshal.Cast<float, uint>(_heatTemperature);
        ReadOnlySpan<uint> air = MemoryMarshal.Cast<float, uint>(_airTemperature);
        ulong lane0 = hash, lane1 = ~hash, lane2 = hash ^ Fnv1a.Prime, lane3 = hash + Fnv1a.Prime;
        int i = 0;
        for (; i + 4 <= heat.Length; i += 4)
        {
            lane0 = (lane0 ^ (heat[i] | (ulong)air[i] << 32)) * Fnv1a.Prime;
            lane1 = (lane1 ^ (heat[i + 1] | (ulong)air[i + 1] << 32)) * Fnv1a.Prime;
            lane2 = (lane2 ^ (heat[i + 2] | (ulong)air[i + 2] << 32)) * Fnv1a.Prime;
            lane3 = (lane3 ^ (heat[i + 3] | (ulong)air[i + 3] << 32)) * Fnv1a.Prime;
        }
        for (; i < heat.Length; i++)
            lane0 = (lane0 ^ (heat[i] | (ulong)air[i] << 32)) * Fnv1a.Prime;
        hash = Fnv1a.Mix(Fnv1a.Mix(Fnv1a.Mix(Fnv1a.Mix(hash, lane0), lane1), lane2), lane3);

        scorch = Fnv1a.Offset;
        ReadOnlySpan<byte> bytes = _scorchIntensity;
        ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(bytes);
        for (int w = 0; w < words.Length; w++)
            if (words[w] != 0)
                scorch = MixScorch(scorch, bytes, w * sizeof(ulong), (w + 1) * sizeof(ulong));
        scorch = MixScorch(scorch, bytes, words.Length * sizeof(ulong), bytes.Length);
        return hash;

        static ulong MixScorch(ulong scorch, ReadOnlySpan<byte> bytes, int from, int to)
        {
            for (int cell = from; cell < to; cell++)
                if (bytes[cell] != 0)
                    scorch = Fnv1a.Mix(Fnv1a.Mix(scorch, cell), (uint)bytes[cell]);
            return scorch;
        }
    }

    /// <summary>Mixes which heat tiles are awake and the solver's throttle counters, for the sync digest.</summary>
    internal ulong MixHeatActivity(ulong hash) => _thermalScheduler.MixLatent(_thermalActivity.MixLatent(hash));
}
