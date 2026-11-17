namespace Tunnerer.Desktop.Rendering;

using System.Collections.Concurrent;

/// <summary>
/// Runs a per-row loop on all cores once there is enough work to pay for it, in bands of rows so each task
/// gets a useful amount of work. Small jobs (a tank-sized dirty rect) stay on the calling thread.
/// </summary>
internal static class RowParallel
{
    /// <summary>Below this many cells the thread hand-off costs more than it saves.</summary>
    private const int MinParallelCells = 2048;
    private const int MinBandCells = 512;

    /// <param name="body">Called with an inclusive-exclusive row range [from, to).</param>
    public static void For(int fromRow, int toRowExclusive, int cellsPerRow, Action<int, int> body)
    {
        int rows = toRowExclusive - fromRow;
        if (rows <= 0) return;
        long cells = (long)rows * Math.Max(1, cellsPerRow);
        if (OperatingSystem.IsBrowser() || rows == 1 || cells < MinParallelCells)
        {
            body(fromRow, toRowExclusive);
            return;
        }

        int bands = (int)Math.Min(Environment.ProcessorCount * 2L, Math.Max(2L, cells / MinBandCells));
        int bandRows = Math.Max(1, (rows + bands - 1) / bands);
        Parallel.ForEach(Partitioner.Create(fromRow, toRowExclusive, bandRows), range => body(range.Item1, range.Item2));
    }
}
