namespace Emberward.Core.Entities.Bases;

using Emberward.Core.Config;
using Emberward.Core.Types;

/// <summary>What stands at a cell of a base (<see cref="BaseLayout.PartAt"/>).</summary>
public enum BasePart
{
    /// <summary>Not a cell of the base.</summary>
    Outside,
    /// <summary>Open ground inside: the courtyard, and the ring between the baffles and the core.</summary>
    Floor,
    /// <summary>The outer wall or a baffle plate: nothing passes and nothing damages it.</summary>
    Wall,
    /// <summary>A gate in the outer wall: open to the owner, shut to the enemy while the gate shield holds.</summary>
    Gate,
    /// <summary>The core: the only part of a base that takes damage.</summary>
    Core,
}

/// <summary>
/// The shape of every base (DESIGN.md §4.9), in cells from its centre: a square of walls with a gate in the middle of
/// each side, the round core at the centre, and a baffle plate in front of each gate. The plates hide the core from every
/// straight line that enters through a gate, so a shot reaches the core only through the gap two plates leave at a
/// corner, and so only from the corner of the courtyard, inside the walls.
/// </summary>
public static class BaseLayout
{
    /// <summary>Cells from the centre to the outer wall.</summary>
    public const int Half = Tweaks.Base.BaseSize / 2;
    /// <summary>Cells a gate reaches to either side of the middle of its wall.</summary>
    public const int GateHalf = Tweaks.Keep.GateSize / 2;
    /// <summary>Cells the core reaches from the centre, along an axis.</summary>
    public const int CoreHalf = Tweaks.Keep.CoreSize / 2;
    /// <summary>Cells from the centre to the middle of the courtyard, where a tank drives round the keep.</summary>
    public const int Lane = Tweaks.Keep.LaneDistance;

    /// <summary>The four ways out, in the order the gates are numbered.</summary>
    public static readonly Offset[] GateDirections = { new(0, -1), new(0, 1), new(-1, 0), new(1, 0) };

    /// <summary>What stands <paramref name="dx"/>, <paramref name="dy"/> cells from a base's centre.</summary>
    public static BasePart PartAt(int dx, int dy)
    {
        int ax = Math.Abs(dx), ay = Math.Abs(dy);
        if (ax > Half || ay > Half)
            return BasePart.Outside;
        if (ax == Half || ay == Half)
            return Math.Min(ax, ay) <= GateHalf ? BasePart.Gate : BasePart.Wall;
        if (IsCore(dx, dy))
            return BasePart.Core;
        bool plate = (ay == Tweaks.Keep.BaffleDistance && ax <= Tweaks.Keep.BaffleHalfLength) ||
                     (ax == Tweaks.Keep.BaffleDistance && ay <= Tweaks.Keep.BaffleHalfLength);
        return plate ? BasePart.Wall : BasePart.Floor;
    }

    /// <summary>
    /// Whether the cell is one of the core's: the core is a disc <see cref="Tweaks.Keep.CoreSize"/> cells across, the
    /// cells whose centres lie within half that of the base's centre.
    /// </summary>
    public static bool IsCore(int dx, int dy) => 4 * (dx * dx + dy * dy) <= Tweaks.Keep.CoreSize * Tweaks.Keep.CoreSize;

    /// <summary>
    /// The core's centre and its outermost cell in each of the eight directions, as offsets from the base's centre:
    /// the points an attacker tries a shot at.
    /// </summary>
    public static readonly Offset[] CoreAims = BuildCoreAims();

    private static Offset[] BuildCoreAims()
    {
        var aims = new List<Offset> { new(0, 0) };
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                    continue;
                int reach = 0;
                while (IsCore(dx * (reach + 1), dy * (reach + 1)))
                    reach++;
                if (reach > 0)
                    aims.Add(new Offset(dx * reach, dy * reach));
            }
        return aims.ToArray();
    }

    /// <summary>
    /// Where a tank stands in the courtyard just inside the gate that faces <paramref name="direction"/>, as an offset
    /// from the centre.
    /// </summary>
    public static Offset InsideGate(Offset direction) => new(direction.X * Lane, direction.Y * Lane);

    /// <summary>
    /// The side of a base at <paramref name="at"/> that faces into a map of <paramref name="map"/>: away from the
    /// nearest border, which is the one a generated base backs onto (<c>LevelGen.EdgeSpawns</c>). Between a
    /// horizontal and a vertical border equally near, it faces up or down.
    /// </summary>
    public static Offset FrontOf(Size map, Position at)
    {
        int top = at.Y, bottom = map.Y - 1 - at.Y, left = at.X, right = map.X - 1 - at.X;
        Offset front;
        if (Math.Min(top, bottom) <= Math.Min(left, right))
            front = new Offset(0, top <= bottom ? 1 : -1);
        else
            front = new Offset(left <= right ? 1 : -1, 0);
        return front;
    }

    /// <summary>
    /// The courtyard corner nearest to <paramref name="dx"/>, <paramref name="dy"/> from the centre, as an offset from
    /// it: the only places a shot at the core can be fired from.
    /// </summary>
    public static Offset NearestCorner(int dx, int dy) => new(dx < 0 ? -Lane : Lane, dy < 0 ? -Lane : Lane);
}
