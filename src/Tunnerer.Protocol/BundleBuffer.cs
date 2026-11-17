namespace Tunnerer.Protocol;

using System.Diagnostics.CodeAnalysis;

/// <summary>
/// A client's bundles that have arrived and not yet been simulated, in tick order, and the rule for how many to
/// simulate now.
/// </summary>
public sealed class BundleBuffer
{
    /// <summary>
    /// Bundles left waiting on purpose, so that one arriving a little late does not leave a step with nothing to
    /// simulate. More than this means the client is behind and should catch up.
    /// </summary>
    public const int TargetDepth = 1;

    private readonly Queue<TickBundle> _bundles = new();

    /// <summary>The tick the next added bundle must carry.</summary>
    public int NextTick { get; private set; }

    public int Count => _bundles.Count;

    /// <summary>Adds the next bundle. The connection is ordered, so any other tick means a broken stream.</summary>
    public void Add(TickBundle bundle)
    {
        if (bundle.Tick != NextTick)
            throw new InvalidDataException($"Expected the bundle for tick {NextTick}, got tick {bundle.Tick}.");
        _bundles.Enqueue(bundle);
        NextTick++;
    }

    public bool TryTake([NotNullWhen(true)] out TickBundle? bundle) => _bundles.TryDequeue(out bundle);

    /// <summary>
    /// How many steps to simulate now: the steps the local clock says are due, as far as bundles are there, plus
    /// extra ones while more than <see cref="TargetDepth"/> would be left waiting, never more than
    /// <paramref name="maxSteps"/> at once.
    /// </summary>
    public int StepsToTake(int dueByClock, int maxSteps)
    {
        int surplus = Math.Max(0, Count - dueByClock - TargetDepth);
        return Math.Min(Count, Math.Min(maxSteps, dueByClock + surplus));
    }
}
