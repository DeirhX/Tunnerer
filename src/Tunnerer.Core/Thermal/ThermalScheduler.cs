namespace Tunnerer.Core.Thermal;

using Tunnerer.Core.Config;

/// <summary>What the terrain heat solver does this tick.</summary>
internal enum ThermalStep
{
    /// <summary>Skip the tick; activity tracking is kept.</summary>
    Sleep,

    /// <summary>Skip the tick and forget the active tiles: the last step barely changed anything.</summary>
    SleepAndForgetActivity,

    /// <summary>Step every cell of the grid.</summary>
    Full,

    /// <summary>Step only the regions around active tiles.</summary>
    Sparse,
}

/// <summary>
/// Decides, once per tick, whether the terrain heat solver runs and over how much of the grid. It sleeps when
/// nothing is active or the last step was quiet (until something wakes it), throttles when most of the grid has
/// been active for a while, and falls back from sparse regions to the full grid when coverage is high.
/// It only counts frames and compares settings, so it is deterministic.
/// </summary>
internal sealed class ThermalScheduler
{
    private int _frameCounter;
    private int _noActiveTileFrameCount;
    private int _saturatedCoverageFrameCount;
    private bool _wakeRequested = true;
    private float _lastMaxDelta;

    /// <summary>Something changed the fields from outside the solver: step on the next tick.</summary>
    public void RequestWake() => _wakeRequested = true;

    public ThermalStep Decide(SimulationSettings settings, int activeTiles, int tileCount, bool artificialCooling)
    {
        _frameCounter++;
        float coverage = tileCount <= 0 ? 1f : activeTiles / (float)tileCount;

        if (activeTiles == 0)
            _noActiveTileFrameCount++;
        else
            _noActiveTileFrameCount = 0;

        if (coverage >= settings.ThermalSaturationCoverageThreshold)
            _saturatedCoverageFrameCount++;
        else
            _saturatedCoverageFrameCount = 0;

        bool sleepNoActive =
            !artificialCooling &&
            _noActiveTileFrameCount >= settings.ThermalNoActiveSleepFrames;
        bool sleepQuietDelta =
            !_wakeRequested &&
            _lastMaxDelta < settings.ThermalMaxDeltaSleepThreshold;
        bool throttleSaturation =
            _saturatedCoverageFrameCount >= settings.ThermalSaturationFramesBeforeThrottle &&
            (_frameCounter % Math.Max(1, settings.ThermalSaturationStepIntervalFrames)) != 0;

        if (sleepQuietDelta)
            return ThermalStep.SleepAndForgetActivity;
        if (sleepNoActive || throttleSaturation)
            return ThermalStep.Sleep;

        bool full = activeTiles == 0 || coverage >= settings.ThermalSparseFallbackCoverage;
        return full ? ThermalStep.Full : ThermalStep.Sparse;
    }

    /// <summary>Records the largest temperature change of the step that just ran.</summary>
    public void RecordStep(float maxAbsDelta)
    {
        _lastMaxDelta = maxAbsDelta;
        _wakeRequested = false;
    }
}
