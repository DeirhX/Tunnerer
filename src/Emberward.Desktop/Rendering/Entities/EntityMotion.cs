namespace Emberward.Desktop.Rendering.Entities;

using Emberward.Core.Rendering;
using Emberward.Core.Types;
using Emberward.Desktop.Config;

/// <summary>
/// Where tanks and machines are drawn. Every moving thing has a drawn position <c>start + motion * alpha</c>, where
/// alpha is the progress through the current simulation step (0..1); the entity vertex shader evaluates it, so the
/// display can run faster than the simulation, and a frame can be drawn again at a later alpha without a new step.
/// A tank's start is where it was last drawn (so it never jumps), and its end is predicted one step ahead from its
/// last three positions, so it is shown with no lag. A machine slides from its previous cell to its latest. Also turns
/// drawn headings smoothly toward the simulation's step directions, and notices respawns so a tank is not slid
/// across the map.
/// </summary>
public sealed class EntityMotion
{
    /// <summary>A tank that moved further than this in one step was respawned, not driven: do not slide it.</summary>
    public const float TeleportDistanceCells = 3f;

    /// <summary>How fast the drawn hull swings round to a new heading (radians per second).</summary>
    public const float HullTurnRate = 16f;

    /// <summary>How fast a drawn harvester turns to a new crawl direction (radians per second).</summary>
    public const float HarvesterTurnRate = 6f;

    /// <summary>A machine that jumped further than this (cells, Manhattan) in one step is not slid.</summary>
    private const int MachineSlideCells = 2;

    private TankRenderState[] _tanks = [];
    private PositionF[] _start = [];
    private PositionF[] _motion = [];
    private Position[] _previous = [];
    private Position[] _beforePrevious = [];
    private int[] _history = [];
    private float[] _travelStart = [];
    private float[] _travelMotion = [];
    private float[] _hullHeading = [];
    private bool[] _hullHeadingKnown = [];
    private PositionF[] _drawn = [];
    private int _tankCount;

    private MachineRenderState[] _machines = [];
    private PositionF[] _machineStart = [];
    private PositionF[] _machineMotion = [];
    private float[] _machineHeading = [];
    private int _machineCount;

    /// <summary>
    /// How much of the predicted lead the tanks get (1 = a whole step ahead, 0 = drawn where the step left them,
    /// which shows them a step late). For tuning by eye.
    /// </summary>
    public float Lead { get; set; } = DesktopScreenTweaks.MotionLead;

    /// <summary>The alpha the entities were last placed at; the next step's motion starts from there.</summary>
    public float Alpha { get; private set; }

    /// <summary>The tanks after the latest step, indexed like the world's tank list.</summary>
    public ReadOnlySpan<TankRenderState> Tanks => _tanks.AsSpan(0, _tankCount);

    /// <summary>The machines after the latest step, indexed like the world's machine list.</summary>
    public ReadOnlySpan<MachineRenderState> Machines => _machines.AsSpan(0, _machineCount);

    /// <summary>
    /// Records the tanks after a simulation step. Each tank's motion starts where it was last drawn and ends at the
    /// position predicted for one step on: <c>1.25 * now + 0.5 * before - 0.75 * beforeThat</c> is exactly one step
    /// ahead in steady motion, and stays steady through a move-and-dig alternation.
    /// </summary>
    public void CaptureTanks(ReadOnlySpan<TankRenderState> tanks)
    {
        int count = tanks.Length;
        if (_tanks.Length < count)
        {
            Array.Resize(ref _tanks, count);
            Array.Resize(ref _start, count);
            Array.Resize(ref _motion, count);
            Array.Resize(ref _previous, count);
            Array.Resize(ref _beforePrevious, count);
            Array.Resize(ref _history, count);
            Array.Resize(ref _travelStart, count);
            Array.Resize(ref _travelMotion, count);
            Array.Resize(ref _hullHeading, count);
            Array.Resize(ref _hullHeadingKnown, count);
            Array.Resize(ref _drawn, count);
        }

        bool sameTanks = _tankCount == count && count > 0 && _history[0] > 0;
        for (int i = 0; i < count; i++)
        {
            Position now = tanks[i].Position;
            if (!sameTanks || IsTeleport(_tanks[i], tanks[i]))
            {
                _start[i] = new PositionF(now.X, now.Y);
                _motion[i] = default;
                _previous[i] = _beforePrevious[i] = now;
                _history[i] = 1;
                _travelStart[i] = 0f;
                _travelMotion[i] = 0f;
                continue;
            }

            // Where it was last drawn: continuity, so a changed prediction never makes it jump.
            PositionF drawn = DrawnTank(i, Alpha);
            _travelStart[i] += _travelMotion[i] * Alpha;
            _beforePrevious[i] = _previous[i];
            _previous[i] = _tanks[i].Position;
            _history[i] = Math.Min(3, _history[i] + 1);

            PositionF end = new(now.X, now.Y);
            if (!tanks[i].IsDead)
            {
                float px, py;
                if (_history[i] >= 3)
                {
                    px = 1.25f * now.X + 0.5f * _previous[i].X - 0.75f * _beforePrevious[i].X;
                    py = 1.25f * now.Y + 0.5f * _previous[i].Y - 0.75f * _beforePrevious[i].Y;
                }
                else
                {
                    px = 2f * now.X - _previous[i].X;
                    py = 2f * now.Y - _previous[i].Y;
                }
                end = new PositionF(now.X + Lead * (px - now.X), now.Y + Lead * (py - now.Y));
            }
            _start[i] = drawn;
            _motion[i] = new PositionF(end.X - drawn.X, end.Y - drawn.Y);
            _travelMotion[i] = MathF.Sqrt(_motion[i].X * _motion[i].X + _motion[i].Y * _motion[i].Y);
        }
        tanks.CopyTo(_tanks);
        _tankCount = count;
        // A new step is drawn from its start until the next frame says otherwise.
        Alpha = 0f;
    }

    /// <summary>Records the machines after a simulation step; each slides from where it was to where it is.</summary>
    public void CaptureMachines(ReadOnlySpan<MachineRenderState> machines)
    {
        int count = machines.Length;
        if (_machines.Length < count)
        {
            int size = Math.Max(count, 16);
            Array.Resize(ref _machines, size);
            Array.Resize(ref _machineStart, size);
            Array.Resize(ref _machineMotion, size);
            Array.Resize(ref _machineHeading, size);
        }
        // The machine list only appends, or drops dead machines; while its size is unchanged, index i is the same
        // machine as in the previous step.
        bool sameMachines = count == _machineCount;
        for (int i = 0; i < count; i++)
        {
            Position now = machines[i].Position;
            Position from = sameMachines ? _machines[i].Position : now;
            int jump = Math.Abs(now.X - from.X) + Math.Abs(now.Y - from.Y);
            if (sameMachines && _machines[i].Type == machines[i].Type && jump <= MachineSlideCells)
            {
                _machineStart[i] = new PositionF(from.X, from.Y);
                _machineMotion[i] = new PositionF(now.X - from.X, now.Y - from.Y);
            }
            else
            {
                _machineStart[i] = new PositionF(now.X, now.Y);
                _machineMotion[i] = default;
            }
        }
        machines.CopyTo(_machines);
        _machineCount = count;
    }

    /// <summary>
    /// Freezes everything where it is drawn now (the world takes no step: a pause, or a network tick that carried
    /// none), so nothing keeps moving ahead of a world that stands still.
    /// </summary>
    public void Hold()
    {
        for (int i = 0; i < _tankCount; i++)
        {
            _start[i] = DrawnTank(i, Alpha);
            _motion[i] = default;
            _travelStart[i] += _travelMotion[i] * Alpha;
            _travelMotion[i] = 0f;
        }
        for (int i = 0; i < _machineCount; i++)
        {
            _machineStart[i] = new PositionF(_machineStart[i].X + _machineMotion[i].X * Alpha,
                _machineStart[i].Y + _machineMotion[i].Y * Alpha);
            _machineMotion[i] = default;
        }
        Alpha = 0f;
    }

    /// <summary>Notes the alpha this frame is drawn at; the next step's motion starts from what that drew.</summary>
    public void SetAlpha(float alpha) => Alpha = alpha;

    /// <summary>Tank <paramref name="index"/>'s drawn start and motion (cells per step): drawn at <c>start + motion * alpha</c>.</summary>
    public (PositionF Start, PositionF Motion) TankMotion(int index) => (_start[index], _motion[index]);

    /// <summary>Tank positions (cell coordinates) drawn at <paramref name="alpha"/>, indexed like the world's tank list.</summary>
    public ReadOnlySpan<PositionF> TankPositions(float alpha)
    {
        for (int i = 0; i < _tankCount; i++)
            _drawn[i] = DrawnTank(i, alpha);
        return _drawn.AsSpan(0, _tankCount);
    }

    /// <summary>How far tank <paramref name="index"/>'s treads have rolled (cells), at its drawn place at <paramref name="alpha"/>.</summary>
    public float TreadTravel(int index, float alpha) => _travelStart[index] + _travelMotion[index] * alpha;

    /// <summary>Machine <paramref name="index"/>'s drawn start and motion (cells per step).</summary>
    public (PositionF Start, PositionF Motion) MachineMotion(int index) => (_machineStart[index], _machineMotion[index]);

    private PositionF DrawnTank(int i, float alpha) =>
        new(_start[i].X + _motion[i].X * alpha, _start[i].Y + _motion[i].Y * alpha);

    /// <summary>
    /// Returns the drawn hull heading (radians) of tank <paramref name="index"/>, turning it towards the simulation's
    /// 8-way <paramref name="direction"/> by at most <see cref="HullTurnRate"/> times <paramref name="turnSeconds"/>;
    /// <paramref name="snap"/> (and a tank's first heading) turns it at once.
    /// </summary>
    public float UpdateHullHeading(int index, int direction, float turnSeconds, bool snap)
    {
        // Directions are a 3x3 grid with east on the left (0=NE, 1=N, 2=NW, 3=E, 5=W, 6=SE, 7=S, 8=SW); the middle
        // one means "no direction". See Tank.OffsetToDirection.
        int stepX = 1 - direction % 3;
        int stepY = direction / 3 - 1;
        if (stepX == 0 && stepY == 0)
            return _hullHeading[index];

        float target = MathF.Atan2(stepY, stepX);
        if (!_hullHeadingKnown[index] || snap)
        {
            _hullHeadingKnown[index] = true;
            return _hullHeading[index] = target;
        }

        float difference = MathF.IEEERemainder(target - _hullHeading[index], MathF.Tau);
        float maxTurn = HullTurnRate * MathF.Max(0f, turnSeconds);
        _hullHeading[index] += Math.Clamp(difference, -maxTurn, maxTurn);
        return _hullHeading[index];
    }

    /// <summary>The drawn heading of machine <paramref name="index"/>, turning smoothly to its crawl direction.</summary>
    public float UpdateMachineHeading(int index, Offset step, float turnSeconds, bool snap)
    {
        if (step == default)
            return _machineHeading[index];
        float target = MathF.Atan2(step.Y, step.X);
        if (snap)
            return _machineHeading[index] = target;
        float difference = MathF.IEEERemainder(target - _machineHeading[index], MathF.Tau);
        float maxTurn = HarvesterTurnRate * MathF.Max(0f, turnSeconds);
        _machineHeading[index] += Math.Clamp(difference, -maxTurn, maxTurn);
        return _machineHeading[index];
    }

    private static bool IsTeleport(in TankRenderState from, in TankRenderState to)
    {
        if (from.IsDead != to.IsDead)
            return true;
        int dx = to.Position.X - from.Position.X;
        int dy = to.Position.Y - from.Position.Y;
        return dx * dx + dy * dy > TeleportDistanceCells * TeleportDistanceCells;
    }
}
