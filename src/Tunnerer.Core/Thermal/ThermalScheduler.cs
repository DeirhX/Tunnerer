namespace Tunnerer.Core.Thermal;

using Tunnerer.Core.Config;
using Tunnerer.Core.Types;

/// <summary>
/// Decides, once per tick, whether the terrain heat solver runs. With no active tile there is nothing to step:
/// every tile is in equilibrium until something writes heat into one. When most of the grid has been active for a
/// while it steps only every few ticks. It only counts frames and compares settings, so it is deterministic.
/// </summary>
internal sealed class ThermalScheduler
{
    private int _frameCounter;
    private int _saturatedCoverageFrameCount;

    /// <summary>True when the solver should step this tick.</summary>
    public bool ShouldStep(SimulationSettings settings, int activeTiles, int tileCount)
    {
        _frameCounter++;
        float coverage = tileCount <= 0 ? 1f : activeTiles / (float)tileCount;

        if (coverage >= settings.ThermalSaturationCoverageThreshold)
            _saturatedCoverageFrameCount++;
        else
            _saturatedCoverageFrameCount = 0;

        if (activeTiles == 0)
            return false;

        bool throttled =
            _saturatedCoverageFrameCount >= settings.ThermalSaturationFramesBeforeThrottle &&
            (_frameCounter % Math.Max(1, settings.ThermalSaturationStepIntervalFrames)) != 0;
        return !throttled;
    }

    /// <summary>Mixes the two counters that decide when the solver is throttled, for the sync digest.</summary>
    internal ulong MixLatent(ulong hash) => Fnv1a.Mix(Fnv1a.Mix(hash, _frameCounter), _saturatedCoverageFrameCount);
}
