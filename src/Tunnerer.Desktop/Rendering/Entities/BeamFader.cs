namespace Tunnerer.Desktop.Rendering.Entities;

using Tunnerer.Core.Types;

/// <summary>
/// Gives link, charge and tether beams a life across frames, so they do not pop on and off with the simulation.
/// Each frame the emitters <see cref="Submit"/> the beams that exist now. A new beam grows from its source to its
/// end over <see cref="ConnectSeconds"/>; one that stops being submitted keeps its last shape and, after a short
/// grace (so a one-step blip does not break it), dissolves over <see cref="DissolveSeconds"/>. A beam that comes
/// back after dissolving grows again from its source. Presentation only: nothing here feeds back into the simulation.
/// </summary>
public sealed class BeamFader
{
    /// <summary>How long a new beam takes to reach its end.</summary>
    public const float ConnectSeconds = 0.9f;

    /// <summary>How long a broken beam takes to dissolve.</summary>
    public const float DissolveSeconds = 0.7f;

    /// <summary>A beam missing for less than this is drawn as if it were still there.</summary>
    public const float GraceSeconds = 0.12f;

    /// <summary>A frame gap longer than this (a hitch, a breakpoint) advances the transitions by this much only.</summary>
    private const float MaxFrameSeconds = 0.1f;

    private readonly Dictionary<BeamKey, int> _index = new();
    private readonly List<Track> _tracks = new();
    private float _lastSeconds = float.NaN;

    /// <summary>Beams being tracked: connecting, steady or dissolving.</summary>
    public int Count => _tracks.Count;

    /// <summary>
    /// Starts a frame at visual time <paramref name="seconds"/>: advances every transition by the time since the last
    /// frame and forgets dissolved beams. Every beam present this frame is submitted after.
    /// </summary>
    public void Begin(float seconds)
    {
        float dt = float.IsNaN(_lastSeconds) ? 0f : Math.Clamp(seconds - _lastSeconds, 0f, MaxFrameSeconds);
        _lastSeconds = seconds;
        for (int i = _tracks.Count - 1; i >= 0; i--)
        {
            Track track = _tracks[i];
            if (track.Present)
            {
                track.Connect = Math.Min(1f, track.Connect + dt / ConnectSeconds);
            }
            else
            {
                track.Missing += dt;
                if (track.Missing > GraceSeconds)
                    track.Dissolve -= dt / DissolveSeconds;
            }
            track.Present = false;
            _tracks[i] = track;
            if (track.Dissolve <= 0f)
                RemoveAt(i);
        }
    }

    /// <summary>
    /// A beam that exists this frame, from cell (<paramref name="x0"/>, <paramref name="y0"/>) to
    /// (<paramref name="x1"/>, <paramref name="y1"/>), identified across frames by <paramref name="key"/>.
    /// <paramref name="flow"/> is the shape's strength parameter; <paramref name="blocked"/> marks a blocked link.
    /// </summary>
    public void Submit(in BeamKey key, float x0, float y0, float x1, float y1, float flow, float halfWidth, bool blocked = false)
    {
        var shape = new BeamShape(x0, y0, x1, y1, flow, halfWidth, blocked);
        if (_index.TryGetValue(key, out int i))
        {
            Track track = _tracks[i];
            if (track.Dissolve < 1f)
            {
                // It had started to dissolve: reconnecting takes the full grow again.
                track.Connect = 0f;
                track.Dissolve = 1f;
            }
            track.Present = true;
            track.Missing = 0f;
            track.Shape = shape;
            _tracks[i] = track;
            return;
        }
        _index.Add(key, _tracks.Count);
        _tracks.Add(new Track(key, shape) { Present = true });
    }

    /// <summary>
    /// Draws every tracked beam. The shape's last parameter carries the transition (see <see cref="Transition"/>);
    /// a blocked link adds 4 to it.
    /// </summary>
    public void Emit(EntityQuads quads)
    {
        float scale = quads.View.PixelScale;
        foreach (Track track in _tracks)
        {
            BeamShape s = track.Shape;
            float dx = s.X1 - s.X0, dy = s.Y1 - s.Y0;
            float length = MathF.Sqrt(dx * dx + dy * dy);
            if (length < 0.5f)
                continue;
            float fx = quads.ScreenX(s.X0);
            float fy = quads.ScreenY(s.Y0);
            if (quads.IsOffScreen(fx + dx * 0.5f * scale, fy + dy * 0.5f * scale, (length * 0.5f + 3f) * scale))
                continue;
            float transition = Transition(track.Connect, track.Dissolve) + (s.Blocked ? 4f : 0f);
            quads.Add(fx, fy, dx / length, dy / length, -1.5f, length + 1.5f, -s.HalfWidth, s.HalfWidth,
                track.Key.Kind, length, s.Flow, quads.Phase, transition);
        }
    }

    /// <summary>The transition <see cref="Emit"/> would draw <paramref name="key"/> with, or null if it is not tracked.</summary>
    public float? TransitionOf(in BeamKey key) =>
        _index.TryGetValue(key, out int i) ? Transition(_tracks[i].Connect, _tracks[i].Dissolve) : null;

    /// <summary>
    /// The transition the beam shaders read (Entity/Beams.hlsli, <c>BeamTransition</c>): in (0, 1] a beam growing
    /// toward its end (1 = whole and steady); in [-1, 0) one dissolving (-1 = just broken, near 0 = almost gone).
    /// </summary>
    public static float Transition(float connect, float dissolve) =>
        dissolve < 1f ? -Math.Max(dissolve, 1e-3f) : Math.Max(connect, 1e-3f);

    private void RemoveAt(int i)
    {
        int last = _tracks.Count - 1;
        _index.Remove(_tracks[i].Key);
        if (i != last)
        {
            _tracks[i] = _tracks[last];
            _index[_tracks[i].Key] = i;
        }
        _tracks.RemoveAt(last);
    }

    private record struct Track(BeamKey Key, BeamShape Shape)
    {
        /// <summary>0 to 1: how far the beam has grown toward its end.</summary>
        public float Connect { get; set; }

        /// <summary>1 while it stands; falls to 0 as it dissolves, then the track is forgotten.</summary>
        public float Dissolve { get; set; } = 1f;

        /// <summary>Seconds since it was last submitted.</summary>
        public float Missing { get; set; }

        /// <summary>Submitted this frame.</summary>
        public bool Present { get; set; }
    }

    private readonly record struct BeamShape(float X0, float Y0, float X1, float Y1, float Flow, float HalfWidth, bool Blocked);
}

/// <summary>Which beam this is across frames: its shape kind and the two cells it joins, in flow order.</summary>
public readonly record struct BeamKey(float Kind, Position From, Position To);
