namespace Emberward.Core.Entities.Tanks;

using Emberward.Core.Collision;
using Emberward.Core.Config;
using Emberward.Core.Resources;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

/// <summary>
/// The tank against the terrain: moving, bumping into it (and into other tanks and machines) with its sprite shape, digging and torching through it,
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
    /// along the move it may get through in the same tick. Another tank or another side's machine stops it
    /// (<see cref="CollisionSolver.BlocksTank"/>).
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

        int dir = TankSpriteShape.DirectionOf(speed);
        var newPos = _tank.Position + speed;

        if (CollisionSolver.TestTankTerrain(ctx.Terrain, newPos, dir) != StepObstacle.None)
        {
            DigTunnel(ctx.Terrain, ctx.Heat, newPos, torchUse, ctx.Events);

            int torchDir = TankSpriteShape.DirectionOf(new Offset(
                (int)MathF.Round(torchHeading.X),
                (int)MathF.Round(torchHeading.Y)));

            if (!(torchUse && torchDir == dir))
                return;

            if (CollisionSolver.TestTankTerrain(ctx.Terrain, newPos, dir) != StepObstacle.None)
                return;
        }

        // Another tank or another side's machine in the way stops the step; the ground in front is dug all the same.
        if (ctx.IsTankBlocked?.Invoke(_tank, newPos, dir) == true)
            return;

        _tank.Direction = dir;
        _tank.Position = newPos;
        ctx.Events?.Record(new StepEvent(StepEventKind.TankMoved, newPos, _tank.Color));
        _thermals.AddMoveHeat(ctx.Heat.Ledger);
    }

    /// <summary>Picks a random 8-direction index, skipping the unused center index (4).</summary>
    public int RandomDirection()
    {
        int dir = _rng.NextInt(8);
        return dir >= 4 ? dir + 1 : dir;
    }

    private void DigTunnel(ITerrainPixelAccessor terrain, ITerrainHeatSource heat, Position center, bool torchUse,
        StepEventLog? events)
    {
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
        if (_tank.Hold.Room < units)
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
        _tank.Hold.Add(units);
        events?.Record(new StepEvent(StepEventKind.OreCollected, worldPos, _tank.Color, Count: units));
        return true;
    }

    /// <summary>Mixes the digger's generator and its move credit, for the sync digest.</summary>
    internal ulong MixLatent(ulong hash) => Fnv1a.Mix(Fnv1a.Mix(hash, _rng.State), _moveCredit);
}
