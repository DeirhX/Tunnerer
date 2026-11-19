namespace Emberward.Browser.Client;

using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Network;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

/// <summary>
/// A busy world for measuring the browser build (<c>?machines=N</c>, docs/browser.md): every base's network is grown
/// outward the way a player builds one. From a base's doors and then from each new relay it digs a tunnel through
/// dirt (giving up on a line that meets rock, as a player without the torch would) and plants a relay at its end
/// where the placement preview shows a live link (<see cref="World.PreviewPlacement"/>). Every third relay gets a
/// harvester and a pylon beside it. The world steps between rounds, so new relays are powered before the next round
/// links to them. Nothing here is part of a normal match.
/// </summary>
public static class BrowserStress
{
    /// <summary>Steps between rounds: enough for the link map to power what the round planted.</summary>
    private const int StepsPerRound = 12;

    /// <summary>How many of a base's newest relays (or doors) a round grows from.</summary>
    private const int NodesPerRound = 6;

    /// <summary>How far from a relay the next one is tried, as shares of the longest live link.</summary>
    private static readonly float[] Reaches = [0.6f, 0.4f, 0.25f];

    private const int Directions = 16;

    /// <summary>Half the width of a dug tunnel, in cells: a tank's tunnel is about this wide.</summary>
    private const int TunnelRadius = 1;

    /// <summary>Plants up to <paramref name="machines"/> machines and returns how many it planted.</summary>
    public static int GrowNetworks(World world, int machines)
    {
        ArgumentNullException.ThrowIfNull(world);
        var owners = world.TankList.Tanks.Where(t => t.Base != null).ToArray();
        // A base's walls block a link's line of sight; its doors, at the middle of its top and bottom walls, do not.
        int doorReach = Tweaks.Base.BaseSize / 2;
        var frontiers = owners.Select(t => new Queue<(Position From, Position Link)>(
        [
            (t.Base!.Position + new Offset(0, doorReach), t.Base.Position),
            (t.Base.Position - new Offset(0, doorReach), t.Base.Position),
        ])).ToArray();
        int placed = 0, relays = 0;
        for (int round = 0; placed < machines && round < 4 * machines + 50; round++)
        {
            for (int i = 0; i < owners.Length && placed < machines; i++)
            {
                for (int n = Math.Min(NodesPerRound, frontiers[i].Count); n > 0 && placed < machines; n--)
                {
                    var node = frontiers[i].Dequeue();
                    int planted = GrowFrom(world, owners[i], node.From, frontiers[i], machines - placed, ref relays);
                    placed += planted;
                    // A relay that found room may find more next round.
                    if (planted > 0)
                        frontiers[i].Enqueue(node);
                }
            }
            if (frontiers.All(f => f.Count == 0))
                break;
            for (int s = 0; s < StepsPerRound; s++)
                world.Advance(_ => default);
        }
        return placed;
    }

    /// <summary>
    /// Digs to and plants a relay around <paramref name="from"/>, and beside every third relay (<paramref name="relays"/>
    /// counts them across all bases) a harvester and a pylon.
    /// </summary>
    private static int GrowFrom(World world, Tank owner, Position from, Queue<(Position, Position)> frontier, int room,
        ref int relays)
    {
        int start = (from.X * 7 + from.Y * 13) & (Directions - 1);
        foreach (float share in Reaches)
        {
            float reach = Tweaks.World.MaximumLiveLinkDistance * share;
            for (int k = 0; k < Directions; k++)
            {
                double angle = 2 * Math.PI * ((start + k) % Directions) / Directions;
                var at = new Position(from.X + (int)Math.Round(Math.Cos(angle) * reach),
                    from.Y + (int)Math.Round(Math.Sin(angle) * reach));
                if (!IsFreeSite(world, at) || !Dig(world, from, at) ||
                    world.PreviewPlacement(MachineType.Relay, at).Link.Status != LinkPreviewStatus.Live)
                    continue;
                Plant(world, owner, MachineType.Relay, at);
                frontier.Enqueue((at, at));
                int planted = 1;
                if (++relays % 3 == 0)
                {
                    foreach (var (type, offset) in new[]
                             {
                                 (MachineType.Harvester, new Offset(8, 0)), (MachineType.Pylon, new Offset(0, 8)),
                             })
                    {
                        if (planted < room && IsFreeSite(world, at + offset) && Dig(world, at, at + offset))
                        {
                            Plant(world, owner, type, at + offset);
                            planted++;
                        }
                    }
                }
                return planted;
            }
        }
        return 0;
    }

    /// <summary>Inside the map, outside every base, with no machine close.</summary>
    private static bool IsFreeSite(World world, Position at)
    {
        int margin = Tweaks.Machine.BoundingBoxHalfSize + TunnelRadius + 1;
        var size = world.Terrain.Size;
        if (at.X < margin || at.Y < margin || at.X >= size.X - margin || at.Y >= size.Y - margin)
            return false;
        if (world.IsInsideAnyBase(at))
            return false;
        var near = new List<Machine>();
        world.Machines.CollectNear(at, Tweaks.Machine.BoundingBoxHalfSize * 3, near, inListOrder: false);
        return near.Count == 0;
    }

    /// <summary>
    /// Digs a tunnel from <paramref name="from"/> to <paramref name="to"/> through what a tank digs, through
    /// <see cref="TerrainGrid.SetPixel"/>; false, digging nothing, when rock is in the way. Base cells are left as they are.
    /// </summary>
    private static bool Dig(World world, Position from, Position to)
    {
        var terrain = world.Terrain;
        var cells = Line(from, to);
        foreach (var cell in cells)
            if (Pixel.IsRock(terrain.GetPixel(cell)))
                return false;
        foreach (var cell in cells)
        {
            for (int dy = -TunnelRadius; dy <= TunnelRadius; dy++)
            {
                for (int dx = -TunnelRadius; dx <= TunnelRadius; dx++)
                {
                    var at = new Position(cell.X + dx, cell.Y + dy);
                    var pixel = terrain.GetPixel(at);
                    if (Pixel.IsDiggable(pixel) && !Pixel.IsRock(pixel) && !Pixel.IsAnyBaseCell(pixel))
                        terrain.SetPixel(at, TerrainPixel.Blank);
                }
            }
        }
        return true;
    }

    private static List<Position> Line(Position from, Position to)
    {
        int steps = Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));
        var cells = new List<Position>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            float t = steps == 0 ? 0f : i / (float)steps;
            cells.Add(new Position(from.X + (int)MathF.Round((to.X - from.X) * t),
                from.Y + (int)MathF.Round((to.Y - from.Y) * t)));
        }
        return cells;
    }

    private static void Plant(World world, Tank owner, MachineType type, Position at)
    {
        var machine = new Machine(at, type, owner.Color);
        world.Machines.Add(machine);
        world.PlantMachine(machine, owner.Base);
    }
}
