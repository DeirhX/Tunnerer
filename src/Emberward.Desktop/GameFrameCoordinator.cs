namespace Emberward.Desktop;

using System.Diagnostics;
using Emberward.Desktop.Input;

/// <summary>
/// Owns the main loop. The simulation advances in fixed steps; frames are displayed either once per step
/// (step-locked, for automated runs) or as often as the display allows, with each frame told how far it is
/// between the last two steps so moving things can be drawn in between.
/// </summary>
public sealed class GameFrameCoordinator
{
    private readonly GameLoopScheduler _loopScheduler;
    private readonly bool _renderBetweenSteps;
    private readonly TimeSpan _minRenderInterval;
    private readonly Stopwatch _frameTimer = Stopwatch.StartNew();
    private readonly Stopwatch _sinceRender = Stopwatch.StartNew();

    // How long a Thread.Sleep(1) really takes on this machine (up to a full 15.6 ms timer tick on Windows). Starts
    // pessimistic and is measured as the loop runs.
    private double _sleepCostMs = 16.0;

    /// <param name="renderBetweenSteps">
    /// Display frames between simulation steps, at most one per <paramref name="minRenderInterval"/>. When false a
    /// frame is displayed only after a step, showing exactly that step.
    /// </param>
    public GameFrameCoordinator(
        TimeSpan targetFrameTime, int maxCatchUpSteps, bool renderBetweenSteps, TimeSpan minRenderInterval)
    {
        _loopScheduler = new GameLoopScheduler(targetFrameTime, maxCatchUpSteps);
        _renderBetweenSteps = renderBetweenSteps;
        _minRenderInterval = minRenderInterval;
    }

    /// <param name="composeFrame">Called after the simulation batch of an iteration in which a step was due.</param>
    /// <param name="renderFrame">
    /// Displays a frame. The float is the step progress: 1 shows the latest step, lower values lie between the
    /// previous step (0) and the latest.
    /// </param>
    public void Run(
        Func<bool> isRunning,
        Action requestStop,
        Func<bool> pollEvents,
        Action onBeforeSimulationBatch,
        Func<FrameInputSnapshot> captureFrameInput,
        Action<FrameInputSnapshot> advanceOneSimulationStep,
        Action composeFrame,
        Action<FrameInputSnapshot, float> renderFrame,
        Action<TimeSpan> onFrameMeasured,
        Func<bool>? isPaused = null,
        Action? onPausedFrame = null)
    {
        while (isRunning())
        {
            _loopScheduler.AddElapsed(_frameTimer.Elapsed);
            _frameTimer.Restart();

            if (!pollEvents())
            {
                requestStop();
                break;
            }

            bool stepReady = _loopScheduler.HasReadyStep;
            if (!stepReady)
            {
                double untilStepMs =
                    _loopScheduler.TimeUntilNextStep.TotalMilliseconds - _frameTimer.Elapsed.TotalMilliseconds;
                double untilRenderMs = _renderBetweenSteps
                    ? (_minRenderInterval - _sinceRender.Elapsed).TotalMilliseconds
                    : double.MaxValue;
                if (untilRenderMs > 0)
                {
                    Wait(Math.Min(untilStepMs, untilRenderMs));
                    continue;
                }
            }

            var totalFrameWatch = Stopwatch.StartNew();
            onBeforeSimulationBatch();
            FrameInputSnapshot frameInput = captureFrameInput();
            bool paused = isPaused?.Invoke() == true;
            if (stepReady)
            {
                if (paused)
                {
                    // Keep drawing (the pause menu needs frames) but leave the simulation where it is.
                    _loopScheduler.DiscardReadySteps();
                    onPausedFrame?.Invoke();
                }
                else
                {
                    _loopScheduler.ConsumeReadySteps(
                        onStep: () => advanceOneSimulationStep(frameInput),
                        canContinue: isRunning);
                }
                composeFrame();
            }

            // While paused nothing moves, so show the latest step rather than a point drifting towards it.
            float stepProgress = _renderBetweenSteps && !paused ? _loopScheduler.StepProgress : 1f;
            _sinceRender.Restart();
            renderFrame(frameInput, stepProgress);
            onFrameMeasured(totalFrameWatch.Elapsed);
        }
    }

    /// <summary>
    /// Gives the CPU back while nothing is due, instead of spinning a core at 100%. It only sleeps when the measured
    /// cost of a sleep fits in the time left, so frame pacing is not disturbed.
    /// </summary>
    private void Wait(double remainingMs)
    {
        if (remainingMs <= _sleepCostMs + 1.0)
        {
            Thread.Yield();
            return;
        }

        long start = Stopwatch.GetTimestamp();
        Thread.Sleep(1);
        double tookMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _sleepCostMs = Math.Max(tookMs, _sleepCostMs * 0.9 + tookMs * 0.1);
    }
}
