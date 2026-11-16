namespace Tunnerer.Core.Terrain;

using Tunnerer.Core.Config;
using Tunnerer.Core.Types;

/// <summary>A circle in which no new dirt grows: the range of a planted relay or pylon.</summary>
public readonly record struct RegrowthBlocker(Position Center, int Radius);

/// <summary>
/// Cellular automata that regrows dirt terrain in blank and scorched areas, except inside a
/// <see cref="RegrowthBlocker"/>, where open ground stays open (growth already under way still finishes). Every roll is a hash of the seed, the
/// step and the cell, and rows are applied in order, so the parallel path (<c>--fast</c>) gives exactly the same
/// result as the sequential one.
/// </summary>
public sealed class TerrainRegrowth
{
    private readonly bool _parallel;
    private readonly bool _enabled;
    private readonly int _simulationSeed;
    private TimeSpan _regrowAccumulator;

    public TerrainRegrowth(bool parallel, bool enabled, int simulationSeed)
    {
        _parallel = parallel;
        _enabled = enabled;
        _simulationSeed = simulationSeed;
    }

    public void Advance(TerrainGrid terrain, int advanceCount, IReadOnlyList<RegrowthBlocker> blockers)
    {
        _regrowAccumulator += Tweaks.World.AdvanceStep;
        if (_regrowAccumulator < Tweaks.World.DirtRecoverInterval)
            return;
        _regrowAccumulator -= Tweaks.World.DirtRecoverInterval;

        if (!_enabled)
            return;

        int w = terrain.Width, h = terrain.Height;
        if (h <= 2)
            return;

        // One write list per row; a row reads only raw pixels (writes are staged) and touches only its own scorch.
        var rows = new List<(int offset, TerrainPixel value)>[h];
        if (_parallel)
            Parallel.For(1, h - 1, y => rows[y] = RegrowRow(terrain, advanceCount, y, blockers));
        else
            for (int y = 1; y < h - 1; y++)
                rows[y] = RegrowRow(terrain, advanceCount, y, blockers);

        for (int y = 1; y < h - 1; y++)
        {
            foreach (var (offset, value) in rows[y])
            {
                // SetPixel (not SetPixelRaw): a material change moves energy, which the thermal ledger must see.
                terrain.SetPixel(new Position(offset % w, offset / w), value);
            }
        }
    }

    private static bool IsBlocked(int x, int y, IReadOnlyList<RegrowthBlocker> blockers)
    {
        for (int i = 0; i < blockers.Count; i++)
        {
            RegrowthBlocker blocker = blockers[i];
            int dx = x - blocker.Center.X, dy = y - blocker.Center.Y;
            if (dx * dx + dy * dy <= blocker.Radius * blocker.Radius)
                return true;
        }
        return false;
    }

    private List<(int offset, TerrainPixel value)> RegrowRow(
        TerrainGrid terrain, int advanceCount, int y, IReadOnlyList<RegrowthBlocker> blockers)
    {
        int w = terrain.Width;
        var writes = new List<(int offset, TerrainPixel value)>();
        for (int x = 1; x < w - 1; x++)
        {
            var pos = new Position(x, y);
            var pix = terrain.GetPixelRaw(pos);

            if (pix == TerrainPixel.Blank)
            {
                int neighbors = terrain.CountDirtNeighbors(pos);
                if (TryQueueDirtRegrow(advanceCount, x, y, neighbors, Tweaks.World.DirtRegrowBlankModifier, 0) &&
                    !IsBlocked(x, y, blockers))
                    writes.Add((x + y * w, TerrainPixel.DirtGrow));
            }
            else if (Pixel.IsScorched(pix))
            {
                int offset = x + y * w;
                int neighbors = terrain.CountDirtNeighbors(pos);
                if (TryQueueDirtRegrow(advanceCount, x, y, neighbors, Tweaks.World.DirtRegrowScorchedModifier, 1) &&
                    !IsBlocked(x, y, blockers))
                {
                    writes.Add((offset, TerrainPixel.DirtGrow));
                    terrain.SetScorchIntensity(offset, 0);
                }
                else
                {
                    int cur = terrain.GetScorchIntensity(offset);
                    int next = Math.Max(0, cur - Tweaks.World.DecalIntensityDecay);
                    terrain.SetScorchIntensity(offset, (byte)next);
                    if (next <= 0)
                        writes.Add((offset, TerrainPixel.Blank));
                }
            }
            else if (pix == TerrainPixel.DirtGrow)
            {
                if (Roll1000(advanceCount, x, y, 4) < Tweaks.World.DirtRecoverSpeed)
                    writes.Add((x + y * w, TerrainPixel.Dirt));
            }
        }
        return writes;
    }

    private bool TryQueueDirtRegrow(int advanceCount, int x, int y, int neighbors, int modifier, uint salt)
    {
        if (neighbors <= 2) return false;
        int chance = Tweaks.World.DirtRegrowSpeed * neighbors * modifier;
        return Roll1000(advanceCount, x, y, salt) < chance;
    }

    private int Roll1000(int advanceCount, int x, int y, uint salt)
    {
        uint mixed = (uint)_simulationSeed
            ^ (uint)advanceCount * 0x9e3779b9u
            ^ (uint)(x * 73856093)
            ^ (uint)(y * 19349663)
            ^ (salt * 83492791u);
        return (int)(FastRandom.Hash32(mixed) % 1000u);
    }
}
