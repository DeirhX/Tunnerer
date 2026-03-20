namespace Tunnerer.Core.Terrain;

using System.Diagnostics;
using Tunnerer.Core.Config;
using Tunnerer.Core.Types;

/// <summary>
/// Cellular automata that regrows dirt terrain in blank and scorched areas.
/// Extracted from World to consolidate terrain mutation logic and enable independent testing.
/// </summary>
public sealed class TerrainRegrowth
{
    private readonly bool _deterministicSimulation;
    private readonly bool _enabled;
    private readonly int _simulationSeed;
    private TimeSpan _regrowAccumulator;
    private readonly Stopwatch _regrowTimer = new();

    public TerrainRegrowth(bool deterministicSimulation, bool enabled, int simulationSeed)
    {
        _deterministicSimulation = deterministicSimulation;
        _enabled = enabled;
        _simulationSeed = simulationSeed;
        _regrowTimer.Start();
    }

    public void Advance(TerrainGrid terrain, int advanceCount)
    {
        if (_deterministicSimulation)
        {
            _regrowAccumulator += Tweaks.World.AdvanceStep;
            if (_regrowAccumulator < Tweaks.World.DirtRecoverInterval)
                return;
            _regrowAccumulator -= Tweaks.World.DirtRecoverInterval;
        }
        else
        {
            if (_regrowTimer.Elapsed < _regrowAccumulator + Tweaks.World.DirtRecoverInterval)
                return;
            _regrowAccumulator += Tweaks.World.DirtRecoverInterval;
        }

        if (!_enabled)
            return;

        int w = terrain.Width, h = terrain.Height;

        if (_deterministicSimulation)
        {
            var writes = new List<(int offset, TerrainPixel value)>(1024);
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    var pos = new Position(x, y);
                    var pix = terrain.GetPixelRaw(pos);

                    if (pix == TerrainPixel.Blank)
                    {
                        int neighbors = terrain.CountDirtNeighbors(pos);
                        if (TryQueueDirtRegrow(advanceCount, x, y, neighbors, Tweaks.World.DirtRegrowBlankModifier, 0))
                            writes.Add((x + y * w, TerrainPixel.DirtGrow));
                    }
                    else if (Pixel.IsScorched(pix))
                    {
                        int neighbors = terrain.CountDirtNeighbors(pos);
                        if (TryQueueDirtRegrow(advanceCount, x, y, neighbors, Tweaks.World.DirtRegrowScorchedModifier, 1))
                        {
                            writes.Add((x + y * w, TerrainPixel.DirtGrow));
                        }
                        else if (pix == TerrainPixel.DecalHigh && Roll1000(advanceCount, x, y, 2) < Tweaks.World.DecalDecaySpeed)
                        {
                            writes.Add((x + y * w, TerrainPixel.DecalLow));
                        }
                        else if (pix == TerrainPixel.DecalLow && Roll1000(advanceCount, x, y, 3) < Tweaks.World.DecalDecaySpeed)
                        {
                            writes.Add((x + y * w, TerrainPixel.Blank));
                        }
                    }
                    else if (pix == TerrainPixel.DirtGrow)
                    {
                        if (Roll1000(advanceCount, x, y, 4) < Tweaks.World.DirtRecoverSpeed)
                        {
                            writes.Add((x + y * w, TerrainPixel.DirtHigh));
                        }
                    }
                }
            }

            foreach (var (offset, value) in writes)
            {
                terrain.SetPixelRaw(offset, value);
                terrain.CommitPixel(new Position(offset % w, offset / w));
            }
            return;
        }

        var stagedWrites = new ThreadLocal<List<(int offset, TerrainPixel value)>>(
            () => new List<(int, TerrainPixel)>(), trackAllValues: true);
        var rng = new ThreadLocal<Random>(() => new Random(), trackAllValues: true);

        Parallel.For(1, h - 1, y =>
        {
            var writes = stagedWrites.Value!;
            var random = rng.Value!;

            for (int x = 1; x < w - 1; x++)
            {
                var pos = new Position(x, y);
                var pix = terrain.GetPixelRaw(pos);

                if (pix == TerrainPixel.Blank)
                {
                    int neighbors = terrain.CountDirtNeighbors(pos);
                    if (TryQueueDirtRegrow(random, neighbors, Tweaks.World.DirtRegrowBlankModifier))
                        writes.Add((x + y * w, TerrainPixel.DirtGrow));
                }
                else if (Pixel.IsScorched(pix))
                {
                    int neighbors = terrain.CountDirtNeighbors(pos);
                    if (TryQueueDirtRegrow(random, neighbors, Tweaks.World.DirtRegrowScorchedModifier))
                    {
                        writes.Add((x + y * w, TerrainPixel.DirtGrow));
                    }
                    else if (pix == TerrainPixel.DecalHigh && random.Next(1000) < Tweaks.World.DecalDecaySpeed)
                    {
                        writes.Add((x + y * w, TerrainPixel.DecalLow));
                    }
                    else if (pix == TerrainPixel.DecalLow && random.Next(1000) < Tweaks.World.DecalDecaySpeed)
                    {
                        writes.Add((x + y * w, TerrainPixel.Blank));
                    }
                }
                else if (pix == TerrainPixel.DirtGrow)
                {
                    if (random.Next(1000) < Tweaks.World.DirtRecoverSpeed)
                    {
                        writes.Add((x + y * w, TerrainPixel.DirtHigh));
                    }
                }
            }
        });

        foreach (var writes in stagedWrites.Values)
        {
            foreach (var (offset, value) in writes)
            {
                terrain.SetPixelRaw(offset, value);
                terrain.CommitPixel(new Position(offset % w, offset / w));
            }
        }

        stagedWrites.Dispose();
        rng.Dispose();
    }

    private static bool TryQueueDirtRegrow(Random random, int neighbors, int modifier)
    {
        if (neighbors <= 2) return false;
        int chance = Tweaks.World.DirtRegrowSpeed * neighbors * modifier;
        return random.Next(1000) < chance;
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
