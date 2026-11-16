namespace Tunnerer.Desktop;

using System.Diagnostics;

/// <summary>
/// The shader animation clock (seconds); terrain regrow stamps are written in it too. Interactive play reads a
/// real-time stopwatch. Automated runs (screenshots, perf captures) display one frame per simulation step, so their
/// clock is the frame count times the step length instead, and the same run draws the same pictures every time.
/// </summary>
public sealed class VisualClock
{
    private readonly Stopwatch? _realTime;
    private readonly double _stepSeconds;

    private VisualClock(Stopwatch? realTime, double stepSeconds)
    {
        _realTime = realTime;
        _stepSeconds = stepSeconds;
    }

    /// <summary>Seconds since the clock was created, by the wall clock.</summary>
    public static VisualClock RealTime() => new(Stopwatch.StartNew(), 0);

    /// <summary>Seconds derived from the frame count: <c>frame * step</c>.</summary>
    public static VisualClock StepLocked(TimeSpan step) => new(null, step.TotalSeconds);

    public bool IsStepLocked => _realTime is null;

    /// <param name="frame">The game's frame counter (simulation steps, plus script frames while paused).</param>
    public float SecondsAt(int frame) =>
        _realTime is { } stopwatch ? (float)stopwatch.Elapsed.TotalSeconds : (float)(frame * _stepSeconds);
}
