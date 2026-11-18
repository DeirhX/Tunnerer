namespace Tunnerer.Core;

using Tunnerer.Core.Snapshots;

/// <summary>
/// How long each system of <see cref="World.Advance"/> took, averaged and reported every 100 steps when
/// <see cref="ReportSink"/> is set. The sections are the world's systems, by label and in step order, so a new system
/// shows up here without a change to this class. It only adds up times: <see cref="World"/> does the timing.
/// </summary>
[NotSaved("diagnostics: wall-clock timings, which differ between machines")]
public sealed class SimulationProfile
{
    private string[] _labels = [];
    private TimeSpan[] _sections = [];

    public Action<string>? ReportSink { get; set; }

    /// <summary>
    /// Adds the <c>[ProfileE]</c> line to each report: the system's thermal energy and how far it strays from the
    /// ledger over the report window. Off by default, because it sums every cell of the map.
    /// </summary>
    public bool AuditEnergy { get; set; }

    // The heat solver's own parts, for [Profile+] and [Profile++]; the heat system fills them.
    public TimeSpan CooldownSim;
    public TimeSpan CooldownApply;
    public long CooldownActiveTiles;
    public long CooldownSteppedTiles;
    public int CooldownSteppedFrames;
    public int CooldownParallelFrames;
    public double ThermalEnergy;
    public double ThermalLedgerResidual;
    public TimeSpan Total;
    public int FrameCount;

    /// <summary>The labels of the sections, in step order.</summary>
    public IReadOnlyList<string> Sections => _labels;

    /// <summary>Time spent in the section labelled <paramref name="label"/> since the last report; zero if there is none.</summary>
    public TimeSpan SectionTime(string label)
    {
        int index = Array.IndexOf(_labels, label);
        return index < 0 ? TimeSpan.Zero : _sections[index];
    }

    /// <summary>Sets the sections to <paramref name="labels"/>, in step order, and starts their times from zero.</summary>
    internal void SetSections(IEnumerable<string> labels)
    {
        _labels = labels.ToArray();
        _sections = new TimeSpan[_labels.Length];
    }

    internal void AddSectionTime(int index, TimeSpan elapsed) => _sections[index] += elapsed;

    public void Report()
    {
        if (FrameCount == 0) return;
        if (ReportSink is null)
        {
            Reset();
            return;
        }

        var line = new System.Text.StringBuilder("[Profile]");
        for (int i = 0; i < _labels.Length; i++)
            line.Append(System.Globalization.CultureInfo.InvariantCulture, $" {_labels[i]}={Avg(_sections[i]):F3}");
        line.Append(System.Globalization.CultureInfo.InvariantCulture,
            $" | total={Avg(Total):F3} ms (avg over {FrameCount} frames)");
        ReportSink(line.ToString());
        ReportSink($"[Profile+] coolSim={Avg(CooldownSim):F3} coolApply={Avg(CooldownApply):F3} ms");
        ReportSink($"[Profile++] activeTiles={AvgCount(CooldownActiveTiles):F1} steppedTiles={AvgCount(CooldownSteppedTiles):F1} " +
            $"steppedFrames={CooldownSteppedFrames}/{FrameCount} parallelFrames={CooldownParallelFrames}/{FrameCount}");
        if (AuditEnergy)
            ReportSink($"[ProfileE] thermal={ThermalEnergy:F3} ledgerResidual={ThermalLedgerResidual:F5} (over {FrameCount} frames)");
        Reset();
    }

    private double Avg(TimeSpan ts) => ts.TotalMilliseconds / FrameCount;
    private double AvgCount(long count) => FrameCount == 0 ? 0 : (double)count / FrameCount;

    public void Reset()
    {
        Array.Clear(_sections);
        CooldownSim = CooldownApply = Total = TimeSpan.Zero;
        CooldownActiveTiles = 0;
        CooldownSteppedTiles = 0;
        CooldownSteppedFrames = 0;
        CooldownParallelFrames = 0;
        ThermalEnergy = 0;
        ThermalLedgerResidual = 0;
        FrameCount = 0;
    }
}
