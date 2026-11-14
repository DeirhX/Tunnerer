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
    private readonly TankHeatEngine _heatEngine;

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

    /// <summary>Heat the tank can still take before it overheats (<c>HeatSafeMax - Heat</c>); more than HeatSafeMax when pre-cooled.</summary>
    public float HeatBudget => Tweaks.Tank.HeatSafeMax - Heat;

    /// <summary>Largest possible <see cref="HeatBudget"/>: a full coolant charge on top of the safe heat range.</summary>
    public static float HeatBudgetCapacity => Tweaks.Tank.HeatSafeMax + Tweaks.Tank.CoolantCapacity;

    /// <summary>Fraction of the coolant charge left (1 = fully pre-cooled, 0 = at ambient or warmer).</summary>
    public float CoolantFraction => Math.Clamp(-Heat / Tweaks.Tank.CoolantCapacity, 0f, 1f);

    /// <summary>Ticks a hit flash lasts after the tank is struck by enemy fire.</summary>
    public const int HitFlashDuration = 18;

    /// <summary>Cause of the most recent armor loss.</summary>
    public DamageCause LastDamageCause { get; private set; }

    /// <summary>What destroyed the tank; <see cref="DamageCause.None"/> while it is alive.</summary>
    public DamageCause DeathCause { get; private set; }

    /// <summary>Counts down from <see cref="HitFlashDuration"/> after a hit by enemy fire.</summary>
    public int HitFlashTicks { get; private set; }

    /// <summary>Velocity of the last projectile that hit the tank (zero if it was never hit).</summary>
    public VectorF LastHitVelocity { get; private set; }

    /// <summary>True on ticks where the tank took overheat damage.</summary>
    public bool IsOverheating { get; private set; }

    /// <summary>Time left until the tank respawns; zero while alive or when no lives remain.</summary>
    public TimeSpan RespawnRemaining => _respawning && LivesLeft > 0 ? _respawnTimer : TimeSpan.Zero;

    /// <summary>Ticks of protection from enemy fire after a respawned tank leaves its base.</summary>
    public const int SpawnProtectionDuration = 48;

    /// <summary>Longest a respawn shield can last, even if the tank stays in its base.</summary>
    public const int SpawnProtectionCap = 10 * Tweaks.Perf.TargetFps;

    private bool _protectedUntilExit;

    /// <summary>Counts down after a respawn; while positive, enemy fire does no damage.</summary>
    public int SpawnProtectionTicks { get; private set; }

    /// <summary>Counts down after the tank refused to plant a relay that would not link.</summary>
    public int PlantRefusedTicks { get; private set; }

    /// <summary>
    /// The respawn shield holds while the tank is in its own base, then lasts <see cref="SpawnProtectionDuration"/>
    /// more ticks after it leaves; it never exceeds <see cref="SpawnProtectionCap"/> and firing ends it at once.
    /// </summary>
    private void UpdateSpawnProtection(bool inOwnBase, bool firing)
    {
        if (SpawnProtectionTicks <= 0)
            return;
        if (firing)
        {
            SpawnProtectionTicks = 0;
            _protectedUntilExit = false;
            return;
        }
        if (_protectedUntilExit && !inOwnBase)
        {
            _protectedUntilExit = false;
            SpawnProtectionTicks = Math.Min(SpawnProtectionTicks, SpawnProtectionDuration);
        }
        SpawnProtectionTicks--;
    }

    /// <summary>Takes armor off the tank and records why, so the cause can be shown to the player.</summary>
    public void TakeDamage(int amount, DamageCause cause, VectorF hitVelocity = default)
    {
        if (amount <= 0 || IsDead)
            return;
        if (cause == DamageCause.EnemyFire && SpawnProtectionTicks > 0)
            return;
        Reactor.Exhaust(new ReactorState(0, amount));
        LastDamageCause = cause;
        if (cause == DamageCause.EnemyFire)
        {
            HitFlashTicks = HitFlashDuration;
            LastHitVelocity = hitVelocity;
        }
        if (IsDead)
            DeathCause = cause;
    }

    private TimeSpan _respawnTimer;
    private bool _respawning;
    private int _frameDugPixels;
    private bool _frameShotFired;
    private FastRandom _rng;
    private readonly MachineMaterializer _machineMaterializer;
    private bool _buildHeld;

    /// <summary>True while the tank carries a machine (a relay) it has built but not planted yet.</summary>
    public bool IsCarryingMachine => _machineMaterializer.IsCarrying;

    public Tank(int color, TankBase tankBase, int rngSeed = 0, SimulationSettings? settings = null)
    {
        _heatEngine = new TankHeatEngine(settings ?? SimulationSettings.FromTweaks());
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

    public void Advance(in SimulationContext ctx, ControllerOutput input)
    {
        if (HitFlashTicks > 0)
            HitFlashTicks--;
        if (PlantRefusedTicks > 0)
            PlantRefusedTicks--;
        IsOverheating = false;

        if (IsDead)
        {
            AdvanceDeath(ctx.Projectiles, ctx.Heat.Ledger);
            return;
        }

        Heat += Tweaks.Tank.IdleHeatGain;
        ctx.Heat.Ledger.RecordInjected(Tweaks.Tank.IdleHeatGain * Tweaks.Tank.TankHeatCapacity);
        Reactor.Current.Heat = new Heat((int)MathF.Round(Heat));
        AdvanceBaseInteraction(in ctx);
        AdvanceTurretAim(input);
        Turret.Update(input.ShootPrimary);
        HandleMove(in ctx, input.MoveSpeed, Turret.Direction, input.ShootPrimary);
        UpdateSpawnProtection(ctx.CheckBaseCollision(Position) == Base, input.ShootPrimary);
        AdvanceShooting(input, ctx.Projectiles);
        CollectItems(ctx.Terrain, ctx.Heat, ctx.Settings);
        AdvanceBuilding(in ctx, input);
        AdvanceHeat(ctx.Heat, ctx.Settings);
    }

    /// <summary>
    /// Build key (edge triggered): with no machine in hand it builds a relay from the tank's resources and
    /// carries it; pressed again outside a base it plants the relay where the tank stands.
    /// </summary>
    private void AdvanceBuilding(in SimulationContext ctx, ControllerOutput input)
    {
        bool pressed = input.BuildPrimary && !_buildHeld;
        _buildHeld = input.BuildPrimary;

        if (pressed && ctx.Machines != null && ctx.PlantMachine != null)
        {
            if (_machineMaterializer.IsCarrying)
            {
                if (ctx.CheckBaseCollision(Position) == null && ctx.CanPlantRelayAt?.Invoke(Position) == false)
                {
                    PlantRefusedTicks = 48;
                }
                else if (ctx.CheckBaseCollision(Position) == null)
                {
                    var machine = _machineMaterializer.ReleaseCarried();
                    if (machine != null)
                        ctx.PlantMachine(machine, Base);
                }
            }
            else
            {
                // Docked at its own base, a tank that is short of dirt builds from the base store, since
                // docking deposits the tank's dirt there.
                var cost = MachineBehaviors.Get(MachineType.Relay).BuildCost;
                var payer = !Resources.CanPay(cost) && Base != null && ctx.CheckBaseCollision(Position) == Base &&
                            Base.Materials.CanPay(cost)
                    ? Base.Materials
                    : Resources;
                _machineMaterializer.TryBuild(MachineType.Relay, Position, payer, ctx.Machines);
            }
        }

        _machineMaterializer.UpdatePosition(Position);
    }

    private void AdvanceBaseInteraction(in SimulationContext ctx)
    {
        var baseColl = ctx.CheckBaseCollision(Position);
        if (baseColl == null) return;

        baseColl.RechargeTank(Reactor, Color);
        bool atOwnBase = baseColl.Color == Color;
        int baseCooldown = atOwnBase ? Tweaks.Base.HomeCooldownHeat : Tweaks.Base.ForeignCooldownHeat;
        if (baseCooldown > 0)
        {
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
                    ctx.Heat.ExtractEnergyInRadiusArea(Position, radius, terrainShare, TerrainGrid.ThermalField.Terrain) +
                    ctx.Heat.ExtractEnergyInRadiusArea(Position, radius, airShare, TerrainGrid.ThermalField.Air);
                ctx.Heat.Ledger.RecordSunkBase(removed);
            }
        }

        if (atOwnBase)
        {
            baseColl.AbsorbResources(Resources, new MaterialAmount(Tweaks.Base.HomeAbsorbDirt, Tweaks.Base.HomeAbsorbMinerals));
            RefrigerateAtBase(baseColl);
        }
    }

    /// <summary>
    /// Pre-cooling: a tank docked at its own base is chilled toward a full coolant charge, handing its heat to the
    /// base reservoir. This is an exact transfer (the tank goes colder, the reservoir fills), and it stops when the
    /// reservoir is full.
    /// </summary>
    private void RefrigerateAtBase(TankBase tankBase)
    {
        float target = -Tweaks.Tank.CoolantCapacity;
        if (Heat <= target)
            return;

        double wanted = Math.Min(Tweaks.Tank.DockCoolingPerTick, Heat - target) * (double)Tweaks.Tank.TankHeatCapacity;
        double accepted = tankBase.AcceptTankHeat(wanted);
        Heat -= (float)(accepted / Tweaks.Tank.TankHeatCapacity);
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

    private void AdvanceShooting(ControllerOutput input, ProjectileList projectiles)
    {
        if (!input.ShootPrimary) return;
        var bullet = Turret.TryShoot(Position, projectiles);
        if (bullet != null)
        {
            _frameShotFired = true;
        }
    }

    private void HandleMove(in SimulationContext ctx, Offset speed, DirectionF torchHeading, bool torchUse)
    {
        if (speed.X == 0 && speed.Y == 0) return;

        int dir = OffsetToDirection(speed);
        var newPos = Position + speed;

        var collision = TestCollision(ctx.Terrain, newPos, dir);
        if (collision != CollisionType.None)
        {
            DigTunnel(ctx.Terrain, ctx.Heat, newPos, torchUse);

            int torchDir = OffsetToDirection(new Offset(
                (int)MathF.Round(torchHeading.X),
                (int)MathF.Round(torchHeading.Y)));

            if (!(torchUse && torchDir == dir))
                return;

            collision = TestCollision(ctx.Terrain, newPos, dir);
            if (collision != CollisionType.None)
                return;
        }

        Direction = dir;
        Position = newPos;
        Heat += Tweaks.Tank.MoveHeatGain;
        ctx.Heat.Ledger.RecordInjected(Tweaks.Tank.MoveHeatGain * Tweaks.Tank.TankHeatCapacity);
    }

    private CollisionType TestCollision(ITerrainPixelAccessor terrain, Position pos, int dir)
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

    private void DigTunnel(ITerrainPixelAccessor terrain, ITerrainHeatSource heat, Position center, bool torchUse)
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
                        heat.AddHeat(worldPos, Tweaks.Tank.TorchTerrainHeatAmount);
                    }
                    if (Pixel.IsDirt(pix))
                        Resources.Add(new MaterialAmount(1, 0));
                }
                else if (Pixel.IsTorchable(pix) && torchUse &&
                         _rng.Chance1000(Tweaks.World.DigThroughRockChance))
                {
                    terrain.SetPixel(worldPos, TerrainPixel.Blank);
                    _frameDugPixels += 2;
                    heat.AddHeatRadius(worldPos, Tweaks.Tank.TorchRockHeatAmount, Tweaks.Tank.TorchRockHeatRadius);
                    if (Pixel.IsMineral(pix))
                        Resources.Add(new MaterialAmount(0, 1));
                }
            }
    }

    private void CollectItems(ITerrainPixelAccessor terrain, ITerrainHeatSource heat, SimulationSettings settings)
    {
        ForEachSpritePixel(Direction, Position, (_, worldPos) =>
        {
            if (!terrain.IsInside(worldPos)) return true;

            var pix = terrain.GetPixelRaw(worldPos);
            if (Pixel.IsGold(pix))
            {
                terrain.SetPixel(worldPos, TerrainPixel.Blank);
                int cooling = Tweaks.Tank.CoolingPickupGold;
                float cooledHeat = TankHeatModel.ClampHeat(in settings, Heat - cooling);
                heat.Ledger.RecordSunkTank((Heat - cooledHeat) * Tweaks.Tank.TankHeatCapacity);
                Heat = cooledHeat;
            }
            return true;
        });
    }

    private void AdvanceHeat(ITerrainHeatSource heat, SimulationSettings settings)
    {
        int radius = Tweaks.Tank.DigRadius;
        float sampledTerrainTemperature = heat.SampleAverageHeat(Position, radius) * 255f;
        float sampledAirTemperature = heat.SampleAverageAirTemperature(Position, radius);
        var step = _heatEngine.Advance(
            currentHeat: Heat,
            frameDugPixels: _frameDugPixels,
            frameShotFired: _frameShotFired,
            sampledTerrainTemperature: sampledTerrainTemperature,
            sampledAirTemperature: sampledAirTemperature,
            terrainCapacity: heat.SumHeatCapacityInRadiusArea(Position, radius, TerrainGrid.ThermalField.Terrain),
            airCapacity: heat.SumHeatCapacityInRadiusArea(Position, radius, TerrainGrid.ThermalField.Air));

        // Exchange with the grid is an exact transfer: whatever the tank lost, the cells under it gained, and a tank
        // can only take heat that is actually there (cells never drop below ambient).
        float nextHeatAfterExchange = step.NextHeat;
        nextHeatAfterExchange -= ExchangeWithGrid(heat, radius, step.EnergyToTerrain, TerrainGrid.ThermalField.Terrain);
        nextHeatAfterExchange -= ExchangeWithGrid(heat, radius, step.EnergyToAir, TerrainGrid.ThermalField.Air);

        // Digging and shooting create heat from nothing; the heat floor can also create a little.
        heat.Ledger.RecordInjected(step.ActionHeat * Tweaks.Tank.TankHeatCapacity);
        float nextHeat = TankHeatModel.ClampHeat(in settings, nextHeatAfterExchange);
        heat.Ledger.RecordSunkTank((nextHeatAfterExchange - nextHeat) * Tweaks.Tank.TankHeatCapacity);

        if (step.OverheatDamage > 0)
        {
            IsOverheating = true;
            TakeDamage(step.OverheatDamage, DamageCause.Overheat);
        }

        Heat = nextHeat;
        Reactor.Current.Heat = new Heat((int)MathF.Round(Heat));

        _frameDugPixels = 0;
        _frameShotFired = false;
    }

    /// <summary>
    /// Moves heat between the tank and the grid for one field. A positive <paramref name="energyToField"/> is given to
    /// the cells; a negative one is taken from the cells' heat above ambient, and if they hold less than wanted the
    /// tank gets less. Returns how much tank heat must be taken back for that shortfall (0 when everything moved).
    /// </summary>
    private float ExchangeWithGrid(ITerrainHeatSource terrain, int radius, double energyToField, TerrainGrid.ThermalField field)
    {
        if (energyToField > 0.0)
        {
            terrain.AddEnergyInRadiusArea(Position, radius, energyToField, field);
            return 0f;
        }

        if (energyToField < 0.0)
        {
            double wanted = -energyToField;
            double taken = terrain.ExtractEnergyInRadiusArea(Position, radius, wanted, field);
            return (float)((wanted - taken) / Tweaks.Tank.TankHeatCapacity);
        }

        return 0f;
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
        DeathCause = DamageCause.None;
        LastDamageCause = DamageCause.None;
        HitFlashTicks = 0;
        SpawnProtectionTicks = SpawnProtectionCap;
        _protectedUntilExit = true;
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

    private void AdvanceDeath(ProjectileList projectiles, ThermalLedger ledger)
    {
        if (!_respawning)
            Die(projectiles);

        if (LivesLeft <= 0)
            return;

        _respawnTimer -= Tweaks.World.AdvanceStep;
        if (_respawnTimer > TimeSpan.Zero)
            return;

        Spawn(ledger);
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
