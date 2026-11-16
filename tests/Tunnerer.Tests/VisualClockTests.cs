using Tunnerer.Core.Config;
using Tunnerer.Desktop;

namespace Tunnerer.Tests;

public class VisualClockTests
{
    [Fact]
    public void StepLocked_IsFrameCountTimesStep()
    {
        var clock = VisualClock.StepLocked(Tweaks.World.AdvanceStep);
        Assert.True(clock.IsStepLocked);
        Assert.Equal(0f, clock.SecondsAt(0));
        Assert.Equal((float)(240 * Tweaks.World.AdvanceStep.TotalSeconds), clock.SecondsAt(240));
    }

    [Fact]
    public void StepLocked_DoesNotDependOnTheWallClock()
    {
        var first = VisualClock.StepLocked(Tweaks.World.AdvanceStep);
        var second = VisualClock.StepLocked(Tweaks.World.AdvanceStep);
        for (int frame = 0; frame < 1000; frame += 37)
            Assert.Equal(first.SecondsAt(frame), second.SecondsAt(frame));
    }

    [Fact]
    public void RealTime_IgnoresTheFrameCount()
    {
        var clock = VisualClock.RealTime();
        Assert.False(clock.IsStepLocked);
        Assert.InRange(clock.SecondsAt(1_000_000), 0f, 60f);
    }
}
