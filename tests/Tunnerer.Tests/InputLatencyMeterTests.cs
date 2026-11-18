namespace Tunnerer.Tests;

using System.Diagnostics;
using Tunnerer.Desktop.Net;

public class InputLatencyMeterTests
{
    private static long AtMs(double ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);

    [Fact]
    public void AnInputSentAndAppliedTwoTicksLaterIsAStepOfTwo()
    {
        var meter = new InputLatencyMeter();
        meter.Sent(input: 7, tick: 10, timestamp: AtMs(0));

        var sample = meter.Applied(input: 7, tick: 12, timestamp: AtMs(90));

        Assert.NotNull(sample);
        Assert.Equal(10, sample.Value.SentTick);
        Assert.Equal(12, sample.Value.AppliedTick);
        Assert.Equal(2, sample.Value.Ticks);
        Assert.Equal(90, sample.Value.Elapsed.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public void AnInputThatStaysTheSameProducesNoSampleAfterTheFirst()
    {
        var meter = new InputLatencyMeter();
        meter.Sent(input: 3, tick: 1, timestamp: AtMs(0));
        Assert.NotNull(meter.Applied(input: 3, tick: 2, timestamp: AtMs(40)));

        meter.Sent(input: 3, tick: 2, timestamp: AtMs(41));
        Assert.Null(meter.Applied(input: 3, tick: 3, timestamp: AtMs(80)));
    }

    [Fact]
    public void AnInputTheRelayNeverCarriedGivesNoSample()
    {
        var meter = new InputLatencyMeter();
        meter.Sent(input: 5, tick: 4, timestamp: AtMs(0));

        // The relay carried a different input (6) instead: nothing matches, and the pending send of 5 is dropped.
        Assert.Null(meter.Applied(input: 6, tick: 5, timestamp: AtMs(40)));
        Assert.Null(meter.Applied(input: 5, tick: 6, timestamp: AtMs(80)));
    }

    [Fact]
    public void AnOlderUnmatchedSendIsSkippedInFavourOfTheOneThatArrived()
    {
        var meter = new InputLatencyMeter();
        meter.Sent(input: 1, tick: 0, timestamp: AtMs(0));
        meter.Sent(input: 2, tick: 1, timestamp: AtMs(40));
        meter.Sent(input: 1, tick: 2, timestamp: AtMs(80));

        // The bundle carries 2, so the send of 1 at tick 0 was never applied; the match is the send of 2.
        var sample = meter.Applied(input: 2, tick: 5, timestamp: AtMs(200));

        Assert.NotNull(sample);
        Assert.Equal(1, sample.Value.SentTick);
        Assert.Equal(4, sample.Value.Ticks);
    }
}
