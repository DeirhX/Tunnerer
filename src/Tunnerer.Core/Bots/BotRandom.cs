namespace Tunnerer.Core.Bots;

using Tunnerer.Core.Config;
using Tunnerer.Core.Types;

/// <summary>
/// The one random stream a bot, its navigator and its gunner draw from. It is a <see cref="FastRandom"/>, whose state
/// is one number that the sync digest can hash and a snapshot can save, which <see cref="System.Random"/>'s is not
/// (docs/late-join-design.md §9.1). A class, so that the three parts share one stream.
/// </summary>
internal sealed class BotRandom
{
    private FastRandom _rng;

    /// <summary>
    /// The seed is scrambled first: bots are seeded with consecutive numbers, and xorshift's first outputs from nearby
    /// seeds are alike.
    /// </summary>
    public BotRandom(int seed) => _rng = new FastRandom(FastRandom.Hash32((uint)seed));

    /// <summary>A number from 0 to <paramref name="maxExclusive"/> - 1; 0 when <paramref name="maxExclusive"/> is 1 or less.</summary>
    public int Next(int maxExclusive) => _rng.NextInt(maxExclusive);

    /// <summary>A number from <paramref name="minInclusive"/> to <paramref name="maxExclusive"/> - 1.</summary>
    public int Next(int minInclusive, int maxExclusive) => _rng.NextInt(minInclusive, maxExclusive);

    /// <summary>A number from 0 up to, not including, 1.</summary>
    public double NextDouble() => _rng.NextUInt() / 4294967296.0;

    /// <summary>Mixes where the stream is, for the sync digest.</summary>
    public ulong MixState(ulong hash) => Fnv1a.Mix(hash, _rng.State);
}
