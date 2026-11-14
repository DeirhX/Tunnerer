namespace Tunnerer.Desktop;

using System.Diagnostics;
using Tunnerer.Desktop.Input;

public sealed class GameFrameCoordinator
{
    private readonly GameLoopScheduler _loopScheduler;
    private readonly Stopwatch _frameTimer = Stopwatch.StartNew();

    // How long a Thread.Sleep(1) really takes on this machine (up to a full 15.6 ms timer tick on Windows). Starts
    // pessimistic and is measured as the loop runs.
    private double _sleepCostMs = 16.0;

    public GameFrameCoordinator(TimeSpan targetFrameTime, int maxCatchUpSteps)
    {
        _loopScheduler = new GameLoopScheduler(targetFrameTime, maxCatchUpSteps);
    }

    public void Run(
        Func<bool> isRunning,
        Action requestStop,
        Func<bool> pollEvents,
        Func<bool> isGameOver,
        Action onBeforeSimulationBatch,
        Func<FrameInputSnapshot> captureFrameInput,
        Action<FrameInputSnapshot> advanceOneSimulationStep,
        Action composeFrame,
        Action<FrameInputSnapshot> renderFrame,
        Action<TimeSpan> onFrameMeasured,
        Func<bool>? isPaused = null)
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

            if (isGameOver())
            {
                requestStop();
                break;
            }

            if (!_loopScheduler.HasReadyStep)
            {
                WaitForNextStep();
                continue;
            }

            var totalFrameWatch = Stopwatch.StartNew();
            onBeforeSimulationBatch();
            FrameInputSnapshot frameInput = captureFrameInput();
            if (isPaused?.Invoke() == true)
            {
                // Keep drawing (the pause menu needs frames) but leave the simulation where it is.
                _loopScheduler.DiscardReadySteps();
            }
            else
            {
                _loopScheduler.ConsumeReadySteps(
                    onStep: () => advanceOneSimulationStep(frameInput),
                    canContinue: isRunning);
            }
            composeFrame();
            renderFrame(frameInput);
            onFrameMeasured(totalFrameWatch.Elapsed);
        }
    }

    /// <summary>
    /// Gives the CPU back while no simulation step is due, instead of spinning a core at 100%. It only sleeps when
    /// the measured cost of a sleep fits in the time left, so frame pacing is not disturbed.
    /// </summary>
    private void WaitForNextStep()
    {
        double remainingMs = _loopScheduler.TimeUntilNextStep.TotalMilliseconds - _frameTimer.Elapsed.TotalMilliseconds;
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
