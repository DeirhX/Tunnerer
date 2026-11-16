namespace Tunnerer.Desktop.Rendering.Entities;

using Tunnerer.Core.Rendering;
using Tunnerer.Core.Types;

/// <summary>
/// Keeps the tank and machine snapshots of the last two simulation steps and places them in between, so they move
/// smoothly when the display refreshes faster than the simulation ticks. Also turns drawn headings smoothly toward
/// the simulation's step directions, and notices respawns so a tank is not slid across the map.
/// </summary>
public sealed class EntityInterpolator
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
    private TankRenderState[] _previousTanks = [];
    private float[] _treadTravel = [];
    private float[] _previousTreadTravel = [];
    private float[] _hullHeading = [];
    private bool[] _hullHeadingKnown = [];
    private PositionF[] _tankPositions = [];
    private int _tankCount;
    private bool _hasPreviousTanks;

    private MachineRenderState[] _machines = [];
    private MachineRenderState[] _previousMachines = [];
    private float[] _machineHeading = [];
    private int _machineCount;
    private int _previousMachineCount;

    /// <summary>The tanks after the latest step, indexed like the world's tank list.</summary>
    public ReadOnlySpan<TankRenderState> Tanks => _tanks.AsSpan(0, _tankCount);

    /// <summary>The machines after the latest step, indexed like the world's machine list.</summary>
    public ReadOnlySpan<MachineRenderState> Machines => _machines.AsSpan(0, _machineCount);

    /// <summary>Records the tanks after a simulation step; the previous record becomes the interpolation start.</summary>
    public void CaptureTanks(ReadOnlySpan<TankRenderState> tanks)
    {
        int tankCount = tanks.Length;
        if (_tanks.Length < tankCount)
        {
            Array.Resize(ref _tanks, tankCount);
            Array.Resize(ref _previousTanks, tankCount);
            Array.Resize(ref _treadTravel, tankCount);
            Array.Resize(ref _previousTreadTravel, tankCount);
            Array.Resize(ref _hullHeading, tankCount);
            Array.Resize(ref _hullHeadingKnown, tankCount);
            Array.Resize(ref _tankPositions, tankCount);
        }

        bool sameTanks = _hasPreviousTanks && tankCount == _tankCount;
        if (sameTanks)
        {
            Array.Copy(_tanks, _previousTanks, tankCount);
            Array.Copy(_treadTravel, _previousTreadTravel, tankCount);
        }
        tanks.CopyTo(_tanks);
        _tankCount = tankCount;
        for (int i = 0; i < _tankCount; i++)
        {
            if (!sameTanks || IsTeleport(_previousTanks[i], _tanks[i]))
            {
                _previousTanks[i] = _tanks[i];
                _previousTreadTravel[i] = _treadTravel[i];
                continue;
            }
            float dx = _tanks[i].Position.X - _previousTanks[i].Position.X;
            float dy = _tanks[i].Position.Y - _previousTanks[i].Position.Y;
            _treadTravel[i] = _previousTreadTravel[i] + MathF.Sqrt(dx * dx + dy * dy);
        }
        _hasPreviousTanks = true;
    }

    /// <summary>Records the machines after a simulation step; the previous record becomes the interpolation start.</summary>
    public void CaptureMachines(ReadOnlySpan<MachineRenderState> machines)
    {
        int count = machines.Length;
        if (_machines.Length < count)
        {
            int size = Math.Max(count, 16);
            Array.Resize(ref _machines, size);
            Array.Resize(ref _previousMachines, size);
            Array.Resize(ref _machineHeading, size);
        }
        Array.Copy(_machines, _previousMachines, Math.Min(_machineCount, _previousMachines.Length));
        _previousMachineCount = _machineCount;
        machines.CopyTo(_machines);
        _machineCount = count;
    }

    /// <summary>
    /// Tank positions (cell coordinates) between the previous step (<paramref name="alpha"/> 0) and the latest
    /// (<paramref name="alpha"/> 1), indexed like the world's tank list.
    /// </summary>
    public ReadOnlySpan<PositionF> InterpolateTankPositions(float alpha)
    {
        for (int i = 0; i < _tankCount; i++)
        {
            Position from = _previousTanks[i].Position;
            Position to = _tanks[i].Position;
            _tankPositions[i] = new PositionF(
                from.X + (to.X - from.X) * alpha,
                from.Y + (to.Y - from.Y) * alpha);
        }
        return _tankPositions.AsSpan(0, _tankCount);
    }

    /// <summary>How far tank <paramref name="index"/>'s treads have rolled (cells), between the last two steps.</summary>
    public float TreadTravel(int index, float alpha) =>
        _previousTreadTravel[index] + (_treadTravel[index] - _previousTreadTravel[index]) * alpha;

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

    /// <summary>
    /// Where machine <paramref name="index"/> is drawn (cell coordinates). A crawling harvester slides between steps;
    /// a machine that is new, replaced or jumped stays on its latest cell.
    /// </summary>
    public PositionF MachinePosition(int index, float alpha)
    {
        ref readonly MachineRenderState machine = ref _machines[index];
        float x = machine.Position.X, y = machine.Position.Y;
        // The machine list only appends, or drops dead machines; while its size is unchanged, index i is the same
        // machine as in the previous step.
        if (_previousMachineCount == _machineCount && _previousMachines[index].Type == machine.Type)
        {
            Position from = _previousMachines[index].Position;
            int jump = Math.Abs(machine.Position.X - from.X) + Math.Abs(machine.Position.Y - from.Y);
            if (jump <= MachineSlideCells)
            {
                x = from.X + (machine.Position.X - from.X) * alpha;
                y = from.Y + (machine.Position.Y - from.Y) * alpha;
            }
        }
        return new PositionF(x, y);
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
