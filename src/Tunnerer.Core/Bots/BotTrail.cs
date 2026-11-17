namespace Tunnerer.Core.Bots;

using Tunnerer.Core.Types;

/// <summary>
/// The way the bot came since it left its base, as waypoints a few cells apart. Heading home it retraces them: the
/// tank has driven every stretch, so nothing the torch would be needed for lies on it (dirt may have grown back,
/// which the plain dig clears). A trail that crosses itself drops the loop in between.
/// </summary>
internal sealed class BotTrail
{
    /// <summary>
    /// Cells between waypoints (along either axis). Less than half the tank's width, so the straight line between two
    /// of them stays inside the tunnel the tank dug.
    /// </summary>
    private const int Spacing = 3;

    /// <summary>A waypoint counts as reached within this many cells (along either axis).</summary>
    private const int Reach = 1;

    private readonly List<Position> _waypoints = new();

    public void Clear() => _waypoints.Clear();

    /// <summary>Notes that the tank is at <paramref name="at"/> on its way out.</summary>
    public void Record(Position at)
    {
        if (_waypoints.Count > 0 && Distance(at, _waypoints[^1]) < Spacing)
            return;

        // Back near an earlier waypoint: everything after it was a loop, and the way home skips it.
        for (int i = 0; i < _waypoints.Count - 1; i++)
        {
            if (Distance(at, _waypoints[i]) < Spacing)
            {
                _waypoints.RemoveRange(i + 1, _waypoints.Count - i - 1);
                return;
            }
        }

        _waypoints.Add(at);
    }

    /// <summary>
    /// The next waypoint on the way home from <paramref name="at"/>, forgetting those already reached; null when
    /// the trail has run out.
    /// </summary>
    public Position? NextHomeward(Position at)
    {
        while (_waypoints.Count > 0 && Distance(at, _waypoints[^1]) <= Reach)
            _waypoints.RemoveAt(_waypoints.Count - 1);
        return _waypoints.Count > 0 ? _waypoints[^1] : null;
    }

    private static int Distance(Position a, Position b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
