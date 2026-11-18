namespace Tunnerer.Desktop.Net;

using System.Diagnostics;

/// <summary>
/// How long the local player's input takes to come back in the relay's bundles: from the tick this client sent it in,
/// to the tick of the first bundle whose local slot carries it. Development only (`TUNNERER_NET_LATENCY_LOG=1`): the
/// numbers show what the input path costs before its buffering is changed.
/// </summary>
/// <remarks>
/// Inputs are matched by value, so this is an estimate. A change the relay never carried (a tap shorter than a tick, or
/// a repeat it made itself) produces no sample, and the sends it skipped are dropped.
/// </remarks>
public sealed class InputLatencyMeter
{
    /// <summary>A send is kept this long for a match; older unmatched sends are dropped.</summary>
    private const int MaxPending = 64;

    /// <summary>One input that came back: sent in <see cref="SentTick"/>, applied in <see cref="AppliedTick"/>.</summary>
    public readonly record struct Sample(int SentTick, int AppliedTick, TimeSpan Elapsed)
    {
        /// <summary>The ticks the input spent on the way: at least 1 with a relay that closes a tick per send.</summary>
        public int Ticks => AppliedTick - SentTick;
    }

    private readonly record struct Pending(uint Input, int Tick, long Timestamp);

    private readonly Queue<Pending> _pending = new();
    private uint _lastApplied;

    /// <summary>Records an input sent at <paramref name="tick"/>. <paramref name="timestamp"/> is a <see cref="Stopwatch.GetTimestamp"/> value.</summary>
    public void Sent(uint input, int tick, long timestamp)
    {
        _pending.Enqueue(new Pending(input, tick, timestamp));
        if (_pending.Count > MaxPending)
            _pending.Dequeue();
    }

    /// <summary>
    /// Called with the local slot's input from each bundle as it is simulated. Returns a sample when that input differs
    /// from the one before and matches a send that is still pending.
    /// </summary>
    public Sample? Applied(uint input, int tick, long timestamp)
    {
        if (input == _lastApplied)
            return null;
        _lastApplied = input;

        while (_pending.Count > 0)
        {
            Pending sent = _pending.Dequeue();
            if (sent.Input == input)
                return new Sample(sent.Tick, tick, Stopwatch.GetElapsedTime(sent.Timestamp, timestamp));
        }
        return null;
    }
}
