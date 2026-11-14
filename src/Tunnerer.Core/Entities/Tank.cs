namespace Tunnerer.Core.Entities;

using Tunnerer.Core.Types;
using Tunnerer.Core.Config;
using Tunnerer.Core.Resources;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Collision;
using Tunnerer.Core.Input;
using Tunnerer.Core.Rendering;
using Tunnerer.Core.Entities.Projectiles;
using Tunnerer.Core.Thermal;
using Tunnerer.Core.Entities.Machines;

public class Tank
{
    private static readonly TankHeatEngine HeatEngine = new();

    public Position Position { get; set; }
    public int Color { get; }
    public int Direction { get; set; }
    public Reactor Reactor { get; }
    public MaterialContainer Resources { get; }
    public int LivesLeft { get; set; }
    public bool IsDead => Reactor.Health <= 0;
    public TankBase? Base { get; }
    public TankTurret Turret { get; }
    public float Heat { get; set; }

    private TimeSpan _respawnTimer;
    private bool _respawning;
    private int _frameDugPixels;
    private bool _frameShotFired;
    private FastRandom _rng;
    private readonly MachineMaterializer _machineMaterializer;
    private bool _buildHeld;

    /// <summary>True while the tank carries a machine (a relay) it has built but not planted yet.</summary>
    public bool IsCarryingMachine => _machineMaterializer.IsCarrying;

    public Tank(int color, TankBase tankBase, int rngSeed = 0)
    {
        Color = color;
        Base = tankBase;
        Position = tankBase.Position;
        LivesLeft = Tweaks.Tank.MaxLives;
        Reactor = new Reactor(
            initial: new ReactorState(Tweaks.Tank.InitialHeat, Tweaks.Tank.InitialHealth),
            capacity: new ReactorState(Tweaks.Tank.HeatCapacity, Tweaks.Tank.HealthCapacity));
        Resources = new MaterialContainer(
            initial: new MaterialAmount(0, 0),
            capacity: new MaterialAmount(Tweaks.Tank.ResourceDirtCapacity, Tweaks.Tank.ResourceMineralsCapacity));
        Turret = new TankTurret(color);
        _machineMaterializer = new MachineMaterializer(color);
        int fallbackSeed = Environment.TickCount ^ HashSeed(color, tankBase.Position);
        _rng = new FastRandom(rngSeed != 0 ? rngSeed : fallbackSeed);
        Direction = RandomDirection();
        Heat = Tweaks.Tank.InitialHeat;
    }

    public void Advance(World world, ControllerOutput input)
    {
        if (IsDead)
        {
            AdvanceDeath(world);
            return;
        }

        Heat += Tweaks.Tank.IdleHeatGain;
        world.Terrain.Ledger.RecordInjected(Tweaks.Tank.IdleHeatGain * Tweaks.Tank.TankHeatCapacity);
        Reactor.Current.Heat = new Heat((int)MathF.Round(Heat));
        AdvanceBaseInteraction(world);
        AdvanceTurretAim(input);
        Turret.Update(input.ShootPrimary);
        HandleMove(world.Terrain, input.MoveSpeed, Turret.Direction, input.ShootPrimary);
        AdvanceShooting(input, world);
        CollectItems(world.Terrain);
        AdvanceBuilding(world, input);
        AdvanceHeat(world);
    }

    /// <summary>
    /// Build key (edge triggered): with no machine in hand it builds a relay from the tank's resources and
    /// carries it; pressed again outside a base it plants the relay where the tank stands.
    /// </summary>
    private void AdvanceBuilding(World world, ControllerOutput input)
    {
        bool pressed = input.BuildPrimary && !_buildHeld;
        _buildHeld = input.BuildPrimary;

        if (pressed)
        {
            if (_machineMaterializer.IsCarrying)
            {
                if (world.TankBases.CheckBaseCollision(Position) == null)
                {
                    var machine = _machineMaterializer.ReleaseCarried();
                    if (machine != null)
                        world.PlantMachine(machine, Base);
                }
            }
            else
            {
                _machineMaterializer.TryBuild(MachineType.Relay, Position, Resources, world.Machines);
            }
        }

        _machineMaterializer.UpdatePosition(Position);
    }

    private void AdvanceBaseInteraction(World world)
    {
        var baseColl = world.TankBases.CheckBaseCollision(Position);
        if (baseColl == null) return;

        baseColl.RechargeTank(Reactor, Color);
        bool atOwnBase = baseColl.Color == Color;
        int baseCooldown = atOwnBase ? Tweaks.Base.HomeCooldownHeat : Tweaks.Base.ForeignCooldownHeat;
        if (baseCooldown > 0)
        {
            // Cool only the local environment; tank follows naturally via thermal exchange in AdvanceHeat.
            int half = Math.Max(1, Tweaks.Base.BaseSize / 2);
            int dist2 = global::Tunnerer.Core.Types.Position.DistanceSquared(Position, baseColl.Position);
            float radial = 1f - MathF.Sqrt(dist2) / half;
            float centerWeight = Math.Clamp(radial, 0f, 1f);
            float centerBonus = 1f + Tweaks.Tank.HeatBaseCoolBonus * centerWeight;
            int sinkAmount = (int)MathF.Round(Math.Max(0f, baseCooldown * centerBonus));
            if (sinkAmount > 0)
            {
                // The base destroys this energy (it is a sink, not a transfer), so it goes to the ledger.
                // Only heat that is actually present can be removed.
                double terrainShare = Math.Round(sinkAmount * 0.40);
                double airShare = sinkAmount - terrainShare;
                int radius = Math.Max(1, Tweaks.Tank.DigRadius);
                double removed =
                    world.Terrain.ExtractEnergyInRadiusArea(Position, radius, terrainShare, TerrainGrid.ThermalField.Terrain) +
                    world.Terrain.ExtractEnergyInRadiusArea(Position, radius, airShare, TerrainGrid.ThermalField.Air);
                world.Terrain.Ledger.RecordSunkBase(removed);
            }
        }

        if (atOwnBase)
            baseColl.AbsorbResources(Resources, new MaterialAmount(Tweaks.Base.HomeAbsorbDirt, Tweaks.Base.HomeAbsorbMinerals));
    }

    private void AdvanceTurretAim(ControllerOutput input)
    {
        if (input.AimDirection.X != 0 || input.AimDirection.Y != 0)
        {
            Turret.SetDirection(input.AimDirection);
        }
        else if (input.MoveSpeed.X != 0 || input.MoveSpeed.Y != 0)
        {
            float dx = input.MoveSpeed.X, dy = input.MoveSpeed.Y;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            Turret.SetDirection(new DirectionF(dx / len, dy / len));
        }
    }

    private void AdvanceShooting(ControllerOutput input, World world)
    {
        if (!input.ShootPrimary) return;
        var bullet = Turret.TryShoot(Position, world.Projectiles);
        if (bullet != null)
        {
            _frameShotFired = true;
        }
    }

    private void HandleMove(TerrainGrid terrain, Offset speed, DirectionF torchHeading, bool torchUse)
    {
        if (speed.X == 0 && speed.Y == 0) return;

        int dir = OffsetToDirection(speed);
        var newPos = Position + speed;

        var collision = TestCollision(terrain, newPos, dir);
        if (collision != CollisionType.None)
        {
            DigTunnel(terrain, newPos, torchUse);

            int torchDir = OffsetToDirection(new Offset(
                (int)MathF.Round(torchHeading.X),
                (int)MathF.Round(torchHeading.Y)));

            if (!(torchUse && torchDir == dir))
                return;

            collision = TestCollision(terrain, newPos, dir);
            if (collision != CollisionType.None)
                return;
        }

        Direction = dir;
        Position = newPos;
        Heat += Tweaks.Tank.MoveHeatGain;
        terrain.Ledger.RecordInjected(Tweaks.Tank.MoveHeatGain * Tweaks.Tank.TankHeatCapacity);
    }

    private CollisionType TestCollision(TerrainGrid terrain, Position pos, int dir)
    {
        var result = CollisionType.None;
        ForEachSpritePixel(dir, pos, (_, worldPos) =>
        {
            var pix = terrain.GetPixel(worldPos);
            if (Pixel.IsBlockingCollision(pix)) { result = CollisionType.Blocked; return false; }
            if (Pixel.IsSoftCollision(pix)) result = CollisionType.Dirt;
            return true;
        });
        return result;
    }

    private void DigTunnel(TerrainGrid terrain, Position center, bool torchUse)
    {
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
                if (Pixel.IsDiggable(pix))
                {
                    terrain.SetPixel(worldPos, TerrainPixel.Blank);
                    if (torchUse)
                    {
                        _frameDugPixels++;
                        terrain.AddHeat(worldPos, Tweaks.Tank.TorchTerrainHeatAmount);
                    }
                    if (Pixel.IsDirt(pix))
                        Resources.Add(new MaterialAmount(1, 0));
                }
                else if (Pixel.IsTorchable(pix) && torchUse &&
                         _rng.Chance1000(Tweaks.World.DigThroughRockChance))
                {
                    terrain.SetPixel(worldPos, TerrainPixel.Blank);
                    _frameDugPixels += 2;
                    terrain.AddHeatRadius(worldPos, Tweaks.Tank.TorchRockHeatAmount, Tweaks.Tank.TorchRockHeatRadius);
                    if (Pixel.IsMineral(pix))
                        Resources.Add(new MaterialAmount(0, 1));
                }
            }
    }

    private void CollectItems(TerrainGrid terrain)
    {
        ForEachSpritePixel(Direction, Position, (_, worldPos) =>
        {
            if (!terrain.IsInside(worldPos)) return true;

            var pix = terrain.GetPixelRaw(worldPos);
            if (Pixel.IsEnergy(pix))
            {
                terrain.SetPixel(worldPos, TerrainPixel.Blank);
                int cooling = pix switch
                {
                    TerrainPixel.EnergyMedium => Tweaks.Tank.CoolingPickupMedium,
                    TerrainPixel.EnergyHigh => Tweaks.Tank.CoolingPickupHigh,
                    _ => Tweaks.Tank.CoolingPickupLow,
                };
                float cooledHeat = TankHeatModel.ClampHeat(Heat - cooling);
                terrain.Ledger.RecordSunkTank((Heat - cooledHeat) * Tweaks.Tank.TankHeatCapacity);
                Heat = cooledHeat;
            }
            return true;
        });
    }

    private void AdvanceHeat(World world)
    {
        var terrain = world.Terrain;
        int radius = Tweaks.Tank.DigRadius;
        float sampledTerrainTemperature = terrain.SampleAverageHeat(Position, radius) * 255f;
        float sampledAirTemperature = terrain.SampleAverageAirTemperature(Position, radius);
        var step = HeatEngine.Advance(
            currentHeat: Heat,
            frameDugPixels: _frameDugPixels,
            frameShotFired: _frameShotFired,
            sampledTerrainTemperature: sampledTerrainTemperature,
            sampledAirTemperature: sampledAirTemperature,
            terrainCapacity: terrain.SumHeatCapacityInRadiusArea(Position, radius, TerrainGrid.ThermalField.Terrain),
            airCapacity: terrain.SumHeatCapacityInRadiusArea(Position, radius, TerrainGrid.ThermalField.Air));

        // Exchange with the grid is an exact transfer: whatever the tank lost, the cells under it gained.
        terrain.AddEnergyInRadiusArea(Position, radius, step.EnergyToTerrain, TerrainGrid.ThermalField.Terrain);
        terrain.AddEnergyInRadiusArea(Position, radius, step.EnergyToAir, TerrainGrid.ThermalField.Air);

        // Digging and shooting create heat from nothing; the heat floor can also create a little.
        terrain.Ledger.RecordInjected(step.ActionHeat * Tweaks.Tank.TankHeatCapacity);
        float nextHeat = TankHeatModel.ClampHeat(step.NextHeat);
        terrain.Ledger.RecordSunkTank((step.NextHeat - nextHeat) * Tweaks.Tank.TankHeatCapacity);

        if (step.OverheatDamage > 0)
            Reactor.Exhaust(new ReactorState(0, step.OverheatDamage));

        Heat = nextHeat;
        Reactor.Current.Heat = new Heat((int)MathF.Round(Heat));

        _frameDugPixels = 0;
        _frameShotFired = false;
    }

    public void Spawn(ThermalLedger? ledger = null)
    {
        if (Base == null) return;
        Reactor.Add(new ReactorState(0, Reactor.HealthCapacity));
        Position = Base.Position;
        // A fresh tank starts cold: whatever heat the wreck still held is removed from the system.
        ledger?.RecordSunkTank(Heat * Tweaks.Tank.TankHeatCapacity);
        Heat = 0f;
        Reactor.Current.Heat = new Heat(0);
        _respawning = false;
    }

    public void Die(ProjectileList? projectiles = null)
    {
        if (_respawning)
            return;

        Reactor.Exhaust(new ReactorState(Reactor.Heat, Reactor.Health));
        _machineMaterializer.DiscardCarried();
        LivesLeft = Math.Max(0, LivesLeft - 1);
        _respawning = true;
        _respawnTimer = LivesLeft > 0 ? Tweaks.Tank.RespawnDelay : TimeSpan.Zero;

        projectiles?.AddExplosion(Position, Tweaks.Explosion.Death, Tweaks.Explosion.DeathHeatScale);
    }

    private void AdvanceDeath(World world)
    {
        if (!_respawning)
            Die(world.Projectiles);

        if (LivesLeft <= 0)
            return;

        _respawnTimer -= Tweaks.World.AdvanceStep;
        if (_respawnTimer > TimeSpan.Zero)
            return;

        Spawn(world.Terrain.Ledger);
    }

    public void Draw(Surface surface)
    {
        if (IsDead) return;

        ForEachSpritePixel(Direction, Position, (spriteVal, worldPos) =>
        {
            if (!surface.IsInside(worldPos.X, worldPos.Y))
                return true;
            surface.Pixels[worldPos.X + worldPos.Y * surface.Width] = TankSprites.GetPixelColor(spriteVal, Color).ToArgb();
            return true;
        });

        Turret.Draw(surface, Position);
    }

    /// <summary>
    /// Iterates all non-transparent pixels of the sprite for the given direction,
    /// calling <paramref name="visitor"/> with (spriteByteValue, worldPosition).
    /// Return false from visitor to break early.
    /// </summary>
    private static void ForEachSpritePixel(int dir, Position origin, Func<byte, Position, bool> visitor)
    {
        dir = Math.Clamp(dir, 0, TankSprites.DirectionCount - 1);
        var sprite = TankSprites.Sprites[dir];
        int w = TankSprites.SpriteWidth, h = TankSprites.SpriteHeight;
        int cx = w / 2, cy = h / 2;

        for (int sy = 0; sy < h; sy++)
            for (int sx = 0; sx < w; sx++)
            {
                byte val = sprite[sx + sy * w];
                if (val == 0) continue;
                var worldPos = new Position(origin.X - cx + sx, origin.Y - cy + sy);
                if (!visitor(val, worldPos)) return;
            }
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
            ( 1, -1) => 0,
            ( 0, -1) => 1,
            (-1, -1) => 2,
            ( 1,  0) => 3,
            (-1,  0) => 5,
            ( 1,  1) => 6,
            ( 0,  1) => 7,
            (-1,  1) => 8,
            _ => 1,
        };
    }

    /// <summary>
    /// Picks a random 8-direction index, skipping the unused center index (4).
    /// </summary>
    private int RandomDirection()
    {
        int dir = _rng.NextInt(8);
        return dir >= 4 ? dir + 1 : dir;
    }

    private static int HashSeed(int color, Position pos)
    {
        uint mixed = (uint)(color * 73856093) ^ (uint)(pos.X * 19349663) ^ (uint)(pos.Y * 83492791);
        return (int)FastRandom.Hash32(mixed);
    }
}
