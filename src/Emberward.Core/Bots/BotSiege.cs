namespace Emberward.Core.Bots;

using Emberward.Core.Collision;
using Emberward.Core.Config;
using Emberward.Core.Entities.Bases;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

/// <summary>
/// How the bot attacks a base's keep (DESIGN.md §4.9): it shoots a shut gate to wear the gate shield down, and once
/// the gates are open it drives in to a corner of the courtyard, the only place the core can be hit from. It decides
/// nothing by itself: <see cref="BotTankAI"/> asks it what to shoot and where to go.
/// </summary>
internal static class BotSiege
{
    /// <summary>Cells from a base's centre, by either axis, within which the bot works on the base instead of roaming.</summary>
    private const int SiegeRange = Tweaks.Weapon.BotBaseAttackRange + BaseLayout.Half;

    /// <summary>Cells in front of a gate where the bot lines up before it drives at it.</summary>
    private const int GateApproach = BaseLayout.Half + 6;

    /// <summary>How far off the middle of a gate the tank's centre may be and still fit through.</summary>
    private const int GateSlack = BaseLayout.GateHalf - TankSpriteShape.Size / 2;

    /// <summary>
    /// The cell of <paramref name="target"/> a straight shot from the tank hits first and wears down, or null: the
    /// core, or a gate while the gates are shut. The core is aimed at first, then the gates' cells.
    /// </summary>
    public static Position? FindTarget(Tank tank, TankBase target, World world)
    {
        var from = tank.Position;
        int range = Tweaks.Weapon.BotBaseAttackRange;
        if (Position.DistanceSquared(from, target.Position) > range * range)
            return null;
        bool gatesShut = world.AreGatesShut(target);

        foreach (var aim in BaseLayout.CoreAims)
            if (ClearShotAtCore(from, target.Position + aim, target, world) is Position core)
                return core;
        if (!gatesShut)
            return null;
        foreach (var gate in BaseLayout.GateDirections)
        {
            for (int i = -BaseLayout.GateHalf; i <= BaseLayout.GateHalf; i++)
            {
                var cell = target.Position + new Offset(gate.X * BaseLayout.Half + gate.Y * i, gate.Y * BaseLayout.Half + gate.X * i);
                if (FirstHit(from, cell, target, world, gatesShut) is Position hit)
                    return hit;
            }
        }
        return null;
    }

    /// <summary>
    /// The core cell a shot from <paramref name="from"/> aimed at <paramref name="aim"/> lands on, when the line has
    /// room to spare. The gap between two baffles is narrow and a shell starts at the barrel's tip, up to a cell off
    /// the line from the tank's centre, so the same shot from each cell beside the tank must reach the core as well.
    /// </summary>
    private static Position? ClearShotAtCore(Position from, Position aim, TankBase target, World world)
    {
        if (FirstHit(from, aim, target, world, gatesShut: false) is not Position core || !Pixel.IsBaseCore(world.Terrain.GetPixelRaw(core)))
            return null;
        var shift = aim - from;
        foreach (var beside in Beside)
            if (FirstHit(from + beside, from + beside + shift, target, world, gatesShut: false) is null)
                return null;
        return core;
    }

    private static readonly Offset[] Beside = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };

    /// <summary>
    /// What a shot from <paramref name="from"/> aimed at <paramref name="aim"/> hits first, if that wears the base
    /// down: it walks the cells a bullet fired that way crosses, which passes open ground, floor and open gates, and
    /// stops at a shut gate or at the first cell that collides (a bullet does the same and goes on past the aim).
    /// </summary>
    private static Position? FirstHit(Position from, Position aim, TankBase target, World world, bool gatesShut)
    {
        // A base's far wall is within its width of the centre, so this walk reaches it from anywhere in range.
        int reach = Tweaks.Weapon.BotBaseAttackRange + Tweaks.Base.BaseSize;
        var step = BotGunner.AimTowardsOffset(aim - from).ToVector(1f);
        foreach (var pos in Raycaster.CastRay((PositionF)from, step, reach))
        {
            if (pos == from)
                continue;
            if (!world.Terrain.IsInside(pos))
                return null;
            var pixel = world.Terrain.GetPixelRaw(pos);
            if (gatesShut && Pixel.IsBaseGate(pixel) && target.OccupiesCell(pos))
                return pos;
            if (!Pixel.IsAnyCollision(pixel))
                continue;
            return Pixel.IsBaseCore(pixel) && target.OccupiesCell(pos) ? pos : null;
        }
        return null;
    }

    /// <summary>
    /// Where the bot heads next to get at <paramref name="target"/>'s core, or null when the base is too far to work
    /// on. Outside the walls: the front of the gate on the tank's side, then the gate itself (shut, it is shot open
    /// from there). Inside: the nearest corner of the courtyard.
    /// </summary>
    public static Position? Waypoint(Tank tank, TankBase target, World world)
    {
        int dx = tank.Position.X - target.Position.X, dy = tank.Position.Y - target.Position.Y;
        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) > SiegeRange)
            return null;
        if (target.IsInside(tank.Position))
            return target.Position + BaseLayout.NearestCorner(dx, dy);

        var gate = Math.Abs(dy) >= Math.Abs(dx) ? new Offset(0, dy < 0 ? -1 : 1) : new Offset(dx < 0 ? -1 : 1, 0);
        int across = gate.X != 0 ? dy : dx;
        if (Math.Abs(across) > GateSlack)
            return target.Position + new Offset(gate.X * GateApproach, gate.Y * GateApproach);
        return world.AreGatesShut(target)
            ? target.Position + new Offset(gate.X * BaseLayout.Half, gate.Y * BaseLayout.Half)
            : target.Position + BaseLayout.InsideGate(gate);
    }
}
