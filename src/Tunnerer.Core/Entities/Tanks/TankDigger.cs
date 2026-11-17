namespace Tunnerer.Core.Entities.Tanks;

using Tunnerer.Core.Collision;
using Tunnerer.Core.Config;
using Tunnerer.Core.Resources;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

/// <summary>
/// The tank against the terrain: moving, bumping into it with its sprite shape, digging and torching through it,
/// and picking items up. Owns the tank's random stream (direction at birth, torching through rock).
/// </summary>
internal sealed class TankDigger
{
    private readonly Tank _tank;
    private readonly TankThermals _thermals;
    private FastRandom _rng;

    // Fractional moves carried between ticks while the cold slows the tank.
    private float _moveCredit;

    public TankDigger(Tank tank, TankThermals thermals, int seed)
    {
        _tank = tank;
        _thermals = thermals;
        _rng = new FastRandom(seed);
    }

    /// <summary>
    /// Moves one step by <paramref name="speed"/>. Blocked by terrain, the tank digs instead; with the torch firing
    /// along the move it may get through in the same tick.
    /// </summary>
    public void Move(in SimulationContext ctx, Offset speed, DirectionF torchHeading, bool torchUse)
    {
        if (speed.X == 0 && speed.Y == 0)
        {
            _moveCredit = 0f;
            return;
        }

        // A cold tank only gets to act on some ticks (moving and digging alike), so it crawls (DESIGN.md §4.7).
        // The remainder carries over (credit stays below 1 after a move), so a factor of 0.8 moves 4 ticks in 5.
        _moveCredit += _tank.SpeedFactor;
        if (_moveCredit < 1f)
            return;
        _moveCredit -= 1f;

        int dir = OffsetToDirection(speed);
        var newPos = _tank.Position + speed;

        var collision = TestCollision(ctx.Terrain, newPos, dir);
        if (collision != CollisionType.None)
        {
            DigTunnel(ctx.Terrain, ctx.Heat, newPos, torchUse, ctx.Events);

            int torchDir = OffsetToDirection(new Offset(
                (int)MathF.Round(torchHeading.X),
                (int)MathF.Round(torchHeading.Y)));

            if (!(torchUse && torchDir == dir))
                return;

            collision = TestCollision(ctx.Terrain, newPos, dir);
            if (collision != CollisionType.None)
                return;
        }

        _tank.Direction = dir;
        _tank.Position = newPos;
        ctx.Events?.Record(new StepEvent(StepEventKind.TankMoved, newPos, _tank.Color));
        _thermals.AddMoveHeat(ctx.Heat.Ledger);
    }

    /// <summary>Picks up items under the tank's sprite shape: gold cools the tank.</summary>
    public void CollectPickups(ITerrainPixelAccessor terrain, ITerrainHeatSource heat, StepEventLog? events)
    {
        TankSpriteShape.ForEachPixel(_tank.Direction, _tank.Position, (_, worldPos) =>
        {
            if (!terrain.IsInside(worldPos)) return true;

            var pix = terrain.GetPixelRaw(worldPos);
            if (Pixel.IsGold(pix))
            {
                terrain.SetPixel(worldPos, TerrainPixel.Blank);
                _thermals.CoolFromPickup(Tweaks.Tank.CoolingPickupGold, heat.Ledger);
                events?.Record(new StepEvent(StepEventKind.GoldCollected, worldPos, _tank.Color));
            }
            return true;
        });
    }

    /// <summary>Picks a random 8-direction index, skipping the unused center index (4).</summary>
    public int RandomDirection()
    {
        int dir = _rng.NextInt(8);
        return dir >= 4 ? dir + 1 : dir;
    }

    private static CollisionType TestCollision(ITerrainPixelAccessor terrain, Position pos, int dir)
    {
        var result = CollisionType.None;
        TankSpriteShape.ForEachPixel(dir, pos, (_, worldPos) =>
        {
            var pix = terrain.GetPixel(worldPos);
            if (Pixel.IsBlockingCollision(pix)) { result = CollisionType.Blocked; return false; }
            if (Pixel.IsSoftCollision(pix)) result = CollisionType.Dirt;
            return true;
        });
        return result;
    }

    private void DigTunnel(ITerrainPixelAccessor terrain, ITerrainHeatSource heat, Position center, bool torchUse,
        StepEventLog? events)
    {
        var resources = _tank.Resources;
        int cleared = 0;
        int r = Tweaks.Tank.DigRadius;
        for (int ty = center.Y - r; ty <= center.Y + r; ty++)
            for (int tx = center.X - r; tx <= center.X + r; tx++)
            {
                if ((tx == center.X - r || tx == center.X + r) &&
                    (ty == center.Y - r || ty == center.Y + r))
                    continue;

                var worldPos = new Position(tx, ty);
                if (!terrain.IsInside(worldPos)) continue;

                var pix = terrain.GetPixelRaw(worldPos);
                if (Pixel.IsOre(pix))
                {
                    if (TryMineOre(terrain, heat, worldPos, pix, torchUse, events))
                        cleared++;
                }
                else if (Pixel.IsDiggable(pix))
                {
                    terrain.SetPixel(worldPos, TerrainPixel.Blank);
                    cleared++;
                    if (torchUse)
                    {
                        _thermals.RecordDugPixels(1);
                        heat.AddHeat(worldPos, Tweaks.Tank.TorchTerrainHeatAmount);
                    }
                    if (Pixel.IsDirt(pix))
                        resources.Add(new MaterialAmount(1, 0));
                }
                else if (Pixel.IsTorchable(pix) && torchUse &&
                         _rng.Chance1000(Tweaks.World.DigThroughRockChance))
                {
                    terrain.SetPixel(worldPos, TerrainPixel.Blank);
                    cleared++;
                    _thermals.RecordDugPixels(2);
                    heat.AddHeatRadius(worldPos, Tweaks.Tank.TorchRockHeatAmount, Tweaks.Tank.TorchRockHeatRadius);
                }
            }
        if (cleared > 0)
            events?.Record(new StepEvent(StepEventKind.TankDug, center, _tank.Color, Count: cleared, Torch: torchUse));
    }

    /// <summary>
    /// Digs one ore cell into the hold (DESIGN.md §6.3). Plain ore yields to the dig, slower than dirt; a core cell
    /// needs the torch, like rock, and heats the ground as rock does. A cell whose units do not all fit in the hold
    /// stays in the ground, so ore is never lost to mining.
    /// </summary>
    private bool TryMineOre(ITerrainPixelAccessor terrain, ITerrainHeatSource heat, Position worldPos, TerrainPixel pix,
        bool torchUse, StepEventLog? events)
    {
        int units = Pixel.OreUnits(pix);
        if (_tank.Resources.OreRoom < units)
            return false;
        bool core = Pixel.IsOreCore(pix);
        if (core ? !(torchUse && _rng.Chance1000(Tweaks.Ore.TorchCoreChance)) : !_rng.Chance1000(Tweaks.Ore.DigOreChance))
            return false;

        terrain.SetPixel(worldPos, TerrainPixel.Blank);
        if (core)
        {
            _thermals.RecordDugPixels(2);
            heat.AddHeatRadius(worldPos, Tweaks.Tank.TorchRockHeatAmount, Tweaks.Tank.TorchRockHeatRadius);
        }
        else if (torchUse)
        {
            _thermals.RecordDugPixels(1);
            heat.AddHeat(worldPos, Tweaks.Tank.TorchTerrainHeatAmount);
        }
        _tank.Resources.Add(new MaterialAmount(0, units));
        events?.Record(new StepEvent(StepEventKind.OreCollected, worldPos, _tank.Color, Count: units));
        return true;
    }

    /// <summary>
    /// Maps a signed (dx,dy) offset to one of 8 sprite directions.
    /// Layout: 0=NE, 1=N, 2=NW, 3=E, 5=W, 6=SE, 7=S, 8=SW (index 4 is unused).
    /// </summary>
    private static int OffsetToDirection(Offset speed)
    {
        int dx = Math.Sign(speed.X), dy = Math.Sign(speed.Y);
        return (dx, dy) switch
        {
            (1, -1) => 0,
            (0, -1) => 1,
            (-1, -1) => 2,
            (1, 0) => 3,
            (-1, 0) => 5,
            (1, 1) => 6,
            (0, 1) => 7,
            (-1, 1) => 8,
            _ => 1,
        };
    }
}
