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

    /// <summary>Heat the tank can still take before it overheats (<c>HeatSafeMax - Heat</c>).</summary>
    public float HeatBudget => Tweaks.Tank.HeatSafeMax - Heat;

    /// <summary>Fraction of its normal speed the tank moves at; below 1 when it is cold (DESIGN.md §4.7).</summary>
    public float SpeedFactor => TankHeatModel.ColdSpeedFactor(Heat);

    /// <summary>True while the tank took frostbite damage during its last tick.</summary>
    public bool IsFreezing { get; private set; }

    // Fractional moves carried between ticks while the cold slows the tank.
    private float _moveCredit;

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

    /// <summary>
    /// Shots this tank has fired since the match began. Nothing in the simulation reads it; presentation code
    /// compares it between ticks to learn that a shot was fired.
    /// </summary>
    public int ShotsFired { get; private set; }

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
    private bool _buildSecondaryHeld;
    private bool _buildTertiaryHeld;

    /// <summary>True while the tank carries a machine (a relay) it has built but not planted yet.</summary>
    public bool IsCarryingMachine => _machineMaterializer.IsCarrying;
    public MachineType? CarriedMachineType => _machineMaterializer.CarriedType;

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
        IsFreezing = false;

        if (IsDead)
        {
            AdvanceDeath(ctx.Projectiles, ctx.Heat.Ledger);
            return;
        }

        Heat += Tweaks.Tank.IdleHeatGain;
        ctx.Heat.Ledger.RecordInjected(Tweaks.Tank.IdleHeatGain * Tweaks.Tank.TankHeatCapacity);
        Reactor.Current.Heat = new Heat((int)MathF.Round(Heat));
        AdvanceBaseInteraction(in ctx);
        AdvanceNetworkCoverage(in ctx);
        AdvanceTurretAim(input);
        Turret.Update(input.ShootPrimary);
        HandleMove(in ctx, input.MoveSpeed, Turret.Direction, input.ShootPrimary);
        UpdateSpawnProtection(ctx.CheckBaseCollision(Position) == Base, input.ShootPrimary);
        AdvanceShooting(input, ctx.Projectiles);
        CollectItems(ctx.Terrain, ctx.Heat, ctx.Settings);
        AdvanceBuilding(in ctx, input);
        AdvanceUpgrades(in ctx, input);
        AdvanceHeat(ctx.Heat, ctx.Settings);
    }

    /// <summary>
    /// Build keys (edge triggered): with no machine in hand the primary key builds a relay, the secondary key a
    /// harvester and the tertiary key a pylon from the tank's resources and carries it; any build key pressed again
    /// outside a base plants it where the tank stands.
    /// </summary>
    private void AdvanceBuilding(in SimulationContext ctx, ControllerOutput input)
    {
        bool primaryPressed = input.BuildPrimary && !_buildHeld;
        bool secondaryPressed = input.BuildSecondary && !_buildSecondaryHeld;
        bool tertiaryPressed = input.BuildTertiary && !_buildTertiaryHeld;
        _buildHeld = input.BuildPrimary;
        _buildSecondaryHeld = input.BuildSecondary;
        _buildTertiaryHeld = input.BuildTertiary;
        bool pressed = primaryPressed || secondaryPressed || tertiaryPressed;
        var buildType = primaryPressed ? MachineType.Relay
            : secondaryPressed ? MachineType.Harvester
            : MachineType.Pylon;

        if (pressed && ctx.Machines != null && ctx.PlantMachine != null)
        {
            if (_machineMaterializer.IsCarrying)
            {
                // Relays and pylons work through the link network, so they need a live link to be worth planting;
                // a harvester works anywhere outside a base.
                if (ctx.CheckBaseCollision(Position) == null && _machineMaterializer.CarriedType != MachineType.Harvester &&
                    ctx.CanPlantRelayAt?.Invoke(Position) == false)
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
                var payer = BuildPayer(buildType, dockedAtOwnBase: Base != null && ctx.CheckBaseCollision(Position) == Base);
                if (payer != null)
                    _machineMaterializer.TryBuild(buildType, Position, payer, ctx.Machines);
            }
        }

        _machineMaterializer.UpdatePosition(Position);
    }

    /// <summary>Ticks left on the "could not buy that upgrade" feedback after a refused purchase.</summary>
    public int UpgradeRefusedTicks { get; private set; }

    private int _upgradeHeld;

    /// <summary>
    /// Upgrade keys (edge triggered): docked at its own base, the tank buys the next level of the chosen upgrade from
    /// the base's dirt store. Anywhere else, or when the store cannot pay, the press is refused.
    /// </summary>
    private void AdvanceUpgrades(in SimulationContext ctx, ControllerOutput input)
    {
        if (UpgradeRefusedTicks > 0)
            UpgradeRefusedTicks--;
        bool pressed = input.Upgrade != 0 && input.Upgrade != _upgradeHeld;
        _upgradeHeld = input.Upgrade;
        if (!pressed || input.Upgrade < 1 || input.Upgrade > BaseUpgrades.Count)
            return;
        bool docked = Base != null && ctx.CheckBaseCollision(Position) == Base;
        if (!docked || !Base!.TryBuyUpgrade((BaseUpgrade)(input.Upgrade - 1)))
            UpgradeRefusedTicks = 48;
    }

    /// <summary>
    /// Dirt available to build with: the tank's own, or the base store if that is larger while docked at the own
    /// base (docking moves the tank's dirt into the store). A machine is affordable when its cost fits.
    /// </summary>
    public int BuildableDirt(bool dockedAtOwnBase) =>
        dockedAtOwnBase && Base != null ? Math.Max(Resources.Dirt, Base.Materials.Dirt) : Resources.Dirt;

    /// <summary>The store that pays for <paramref name="type"/>, or null if neither can.</summary>
    public MaterialContainer? BuildPayer(MachineType type, bool dockedAtOwnBase)
    {
        var cost = MachineBehaviors.Get(type).BuildCost;
        if (Resources.CanPay(cost))
            return Resources;
        if (dockedAtOwnBase && Base != null && Base.Materials.CanPay(cost))
            return Base.Materials;
        return null;
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
            PumpHeatAtBase(baseColl);
        }
    }

    /// <summary>
    /// The network node cooling the tank, as of its last tick. None while it is inside a base (the base cools it there,
    /// not the network) or off network.
    /// </summary>
    public Links.TankCoverage Coverage { get; private set; }

    /// <summary>True when the coverage rule is on and the tank is outside its network: its reactor is heating up.</summary>
    public bool IsOffNetwork { get; private set; }

    /// <summary>
    /// The lowest the tank's heat can fall while off network. It rises steadily outside coverage and is pulled back
    /// down inside it (and reset by docking), so it is the clock on how long the tank can stay out; work heat above
    /// it still sheds into the surroundings as usual.
    /// </summary>
    public float OffNetworkFloor { get; private set; }

    /// <summary>How fast the off-network floor rises for this tank, per second (its base's insulation slows it).</summary>
    public float OffNetworkHeatPerSecond => Tweaks.Tank.OffNetworkHeatPerSecond * (Base?.Upgrades.OffNetworkHeatFactor ?? 1f);

    private float OffNetworkHeatPerTick(SimulationSettings settings) =>
        settings.TankOffNetworkHeatPerTick * (Base?.Upgrades.OffNetworkHeatFactor ?? 1f);

    /// <summary>
    /// Network coverage (DESIGN.md §4.6). Inside its base's coverage the tank sheds heat down the links into the base
    /// reactor (an exact transfer, limited by the reactor's room, never below zero heat),
    /// and its heat floor falls at the same rate. Outside it, the floor rises at a steady rate and the tank cannot cool
    /// below it (<see cref="AdvanceHeat"/>), so the time it can stay out is set by how far the floor has to climb.
    /// </summary>
    private void AdvanceNetworkCoverage(in SimulationContext ctx)
    {
        IsOffNetwork = false;
        if (ctx.CoverageFor == null || Base == null)
        {
            Coverage = Links.TankCoverage.None;
            return;
        }

        Coverage = ctx.CoverageFor(this);
        if (ctx.CheckBaseCollision(Position) != null)
        {
            // Docked or passing through a base: the base itself handles cooling, so the network is not cooling the tank.
            Coverage = Links.TankCoverage.None;
            OffNetworkFloor = Math.Min(Heat, 0f);
            return;
        }

        float capacity = Tweaks.Tank.TankHeatCapacity;
        if (Coverage.Covered)
        {
            float cooling = ctx.Settings.TankNetworkCoolingPerTick * Coverage.Strength;
            // The floor falls at the network's cooling rate (so a moment in coverage is not a full reset), but never
            // below the tank's own sub-zero heat: coverage only takes heat away, it does not bank cold.
            OffNetworkFloor = Math.Min(Math.Max(OffNetworkFloor - cooling, Math.Min(Heat, 0f)), Heat);
            if (Heat <= 0f)
                return;
            double wanted = Math.Min(cooling, Heat) * (double)capacity;
            double accepted = Base.AcceptTankHeat(wanted);
            Heat -= (float)(accepted / capacity);
        }
        else
        {
            IsOffNetwork = true;
            OffNetworkFloor = Math.Min(OffNetworkFloor, Heat) + OffNetworkHeatPerTick(ctx.Settings);
        }
    }

    /// <summary>
    /// A tank docked at its own base has a share of its heat above the base temperature pumped into the base reservoir
    /// each tick (<c>DockHeatPumpRate</c>), so it cools fast while hot and levels off near 0, never below. This is an
    /// exact transfer (the tank cools, the reservoir fills), and it stops when the reservoir is full. A cold tank is
    /// warmed back toward 0 by ordinary conduction with the pinned base floor.
    /// </summary>
    private void PumpHeatAtBase(TankBase tankBase)
    {
        float excess = Heat - Tweaks.World.ThermalFixedBaseTemperature;
        if (excess <= 0f)
            return;

        double wanted = excess * Tweaks.Tank.DockHeatPumpRate * (double)Tweaks.Tank.TankHeatCapacity;
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
            ShotsFired++;
        }
    }

    private void HandleMove(in SimulationContext ctx, Offset speed, DirectionF torchHeading, bool torchUse)
    {
        if (speed.X == 0 && speed.Y == 0)
        {
            _moveCredit = 0f;
            return;
        }

        // A cold tank only gets to act on some ticks (moving and digging alike), so it crawls.
        _moveCredit = Math.Min(_moveCredit + SpeedFactor, 1f);
        if (_moveCredit < 1f)
            return;
        _moveCredit -= 1f;

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
                // A pickup only takes heat away; it never chills the tank below ambient.
                float cooledHeat = Math.Max(Heat - cooling, Math.Min(Heat, 0f));
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

        // Digging and shooting create heat from nothing.
        heat.Ledger.RecordInjected(step.ActionHeat * Tweaks.Tank.TankHeatCapacity);
        float nextHeat = nextHeatAfterExchange;

        if (step.OverheatDamage > 0)
        {
            IsOverheating = true;
            TakeDamage(step.OverheatDamage, DamageCause.Overheat);
        }
        if (step.FrostbiteDamage > 0)
        {
            IsFreezing = true;
            TakeDamage(step.FrostbiteDamage, DamageCause.Frostbite);
        }

        // Off network the tank cannot cool below its rising heat floor by shedding into ordinary ground; holding it there
        // is reactor heat, created here. Sub-zero surroundings (a frost pocket) are a real cold sink, though: there the
        // floor follows the tank down, so cooling off at a frost pocket buys back time.
        if (IsOffNetwork && (sampledTerrainTemperature < 0f || sampledAirTemperature < 0f))
            OffNetworkFloor = Math.Min(OffNetworkFloor, nextHeat);
        if (IsOffNetwork && nextHeat < OffNetworkFloor)
        {
            heat.Ledger.RecordInjected((OffNetworkFloor - nextHeat) * (double)Tweaks.Tank.TankHeatCapacity);
            nextHeat = OffNetworkFloor;
        }

        Heat = nextHeat;
        Reactor.Current.Heat = new Heat((int)MathF.Round(Heat));

        _frameDugPixels = 0;
        _frameShotFired = false;
    }

    /// <summary>
    /// Moves heat between the tank and the grid for one field: a positive <paramref name="energyToField"/> is given to
    /// the cells, a negative one taken from them (which can chill them below ambient: a cold tank draws heat from its
    /// surroundings). The flow is bounded by the temperature difference, so this is an exact transfer. Returns how
    /// much tank heat must be taken back because the area had no cells to exchange with (0 when everything moved).
    /// </summary>
    private float ExchangeWithGrid(ITerrainHeatSource terrain, int radius, double energyToField, TerrainGrid.ThermalField field)
    {
        if (energyToField == 0.0)
            return 0f;
        double moved = terrain.AddEnergyInRadiusArea(Position, radius, energyToField, field);
        // The tank already changed by -energyToField; whatever did not move goes back to it (the caller subtracts).
        return (float)((moved - energyToField) / Tweaks.Tank.TankHeatCapacity);
    }

    public void Spawn(ThermalLedger? ledger = null)
    {
        if (Base == null) return;
        Reactor.Add(new ReactorState(0, Reactor.HealthCapacity));
        Position = Base.Position;
        // A fresh tank starts cold: whatever heat the wreck still held is removed from the system.
        ledger?.RecordSunkTank(Heat * Tweaks.Tank.TankHeatCapacity);
        Heat = 0f;
        OffNetworkFloor = 0f;
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
