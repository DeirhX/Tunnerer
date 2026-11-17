namespace Tunnerer.Core.Entities.Tanks;

using Tunnerer.Core.Types;
using Tunnerer.Core.Config;
using Tunnerer.Core.Resources;
using Tunnerer.Core.Input;
using Tunnerer.Core.Entities.Projectiles;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Thermal;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Network;

/// <summary>
/// A player's tank. It owns the tank's state and the order of one tick; the rules live in its collaborators:
/// <see cref="TankThermals"/> (heat), <see cref="TankDigger"/> (moving and digging), <see cref="TankBuildController"/>
/// (build keys) and <see cref="TankLifecycle"/> (damage, death, respawn).
/// </summary>
public class Tank
{
    private readonly TankThermals _thermals;
    private readonly TankBuildController _build;
    private readonly TankDigger _digger;
    private readonly TankLifecycle _lifecycle;

    public Position Position { get; set; }
    public int Color { get; }
    public int Direction { get; set; }
    public Reactor Reactor { get; }

    /// <summary>The tank's ore hold (DESIGN.md §6.3): what it mines, carries home and may build with.</summary>
    public OreStore Hold { get; }
    public int LivesLeft { get; set; }
    public bool IsDead => Reactor.Health <= 0;
    public TankBase? Base { get; }

    /// <summary>
    /// True while the tank is alive and inside its own base: it builds from the base store, is cooled and
    /// unloads its ore there. Bases never overlap, so this is the same as asking which base the tank is in.
    /// </summary>
    public bool IsDockedAtOwnBase => !IsDead && Base != null && Base.IsInside(Position);
    public TankTurret Turret { get; }
    public float Heat { get; set; }

    /// <summary>Heat the tank can still take before it overheats (<c>HeatSafeMax - Heat</c>).</summary>
    public float HeatBudget => Tweaks.Tank.HeatSafeMax - Heat;

    /// <summary>
    /// <see cref="HeatBudget"/> of a tank fully cooled at its base (at 0). A colder tank has more room, but docking never
    /// cools below 0, so this is the room the bot plans with.
    /// </summary>
    public static float HeatBudgetCapacity => Tweaks.Tank.HeatSafeMax;

    /// <summary>Fraction of its normal speed the tank moves at; below 1 when it is cold (DESIGN.md §4.7).</summary>
    public float SpeedFactor => TankHeatModel.ColdSpeedFactor(Heat);

    /// <summary>True while the tank took frostbite damage during its last tick.</summary>
    public bool IsFreezing => _thermals.IsFreezing;

    /// <summary>Heat capacity of the tank, from the settings its heat engine uses: tank energy is <c>Heat * ThermalCapacity</c>.</summary>
    public float ThermalCapacity => _thermals.ThermalCapacity;

    /// <summary>True on ticks where the tank took overheat damage.</summary>
    public bool IsOverheating => _thermals.IsOverheating;

    /// <summary>
    /// The network node cooling the tank, as of its last tick. None while it is inside a base (the base cools it there,
    /// not the network) or off network.
    /// </summary>
    public TankCoverage Coverage => _thermals.Coverage;

    /// <summary>True when the coverage rule is on and the tank is outside its network: its reactor is heating up.</summary>
    public bool IsOffNetwork => _thermals.IsOffNetwork;

    /// <summary>
    /// The lowest the tank's heat can fall while off network. It rises steadily outside coverage and is pulled back
    /// down inside it (and reset by docking), so it is the clock on how long the tank can stay out; work heat above
    /// it still sheds into the surroundings as usual.
    /// </summary>
    public float OffNetworkFloor => _thermals.OffNetworkFloor;

    /// <summary>Seconds until the rising off-network heat floor reaches the safe maximum (zero once it has).</summary>
    public float OffNetworkSecondsToOverheat =>
        MathF.Max(0f, (Tweaks.Tank.HeatSafeMax - OffNetworkFloor) / OffNetworkHeatPerSecond);

    /// <summary>How fast the off-network floor rises for this tank, per second (its base's insulation slows it).</summary>
    public float OffNetworkHeatPerSecond => Tweaks.Tank.OffNetworkHeatPerSecond * (Base?.Upgrades.OffNetworkHeatFactor ?? 1f);

    /// <summary>Off network and close enough to overheating that the warning should be urgent.</summary>
    public bool IsOffNetworkUrgent => OffNetworkSecondsToOverheat < Tweaks.Tank.OffNetworkUrgentSeconds;

    /// <summary>Hotter than the safe maximum: overheating burns the shield.</summary>
    public bool IsAboveSafeHeat => Heat > Tweaks.Tank.HeatSafeMax;

    /// <summary>Still warmer than the base cools it to: a docked tank is still being cooled down toward 0.</summary>
    public bool IsCoolingDown => Heat > Tweaks.Tank.FullyCooledMargin;

    /// <summary>Coldest heat on the heat gauge: past the point where frostbite starts.</summary>
    public static float HeatGaugeMin => Tweaks.Tank.FrostbiteTemperature * Tweaks.Tank.HeatGaugeFrostMargin;

    /// <summary>Hottest heat on the heat gauge: a margin past the safe maximum.</summary>
    public static float HeatGaugeMax => Tweaks.Tank.HeatSafeMax * Tweaks.Tank.HeatGaugeOverheatMargin;

    /// <summary>
    /// The running-hot warning, latched: on above <see cref="Tweaks.Tank.RunningHotOnFraction"/> of the safe maximum,
    /// off again only below <see cref="Tweaks.Tank.RunningHotOffFraction"/>. <paramref name="latched"/> is the
    /// caller's previous answer.
    /// </summary>
    public bool IsRunningHot(bool latched)
    {
        float heat = Heat / Tweaks.Tank.HeatSafeMax;
        return latched ? heat > Tweaks.Tank.RunningHotOffFraction : heat > Tweaks.Tank.RunningHotOnFraction;
    }

    /// <summary>
    /// The low-shield warning, latched: on below <see cref="Tweaks.Tank.ShieldLowOnFraction"/> of capacity, off again
    /// only above <see cref="Tweaks.Tank.ShieldLowOffFraction"/>. <paramref name="latched"/> is the caller's previous
    /// answer.
    /// </summary>
    public bool IsShieldLow(bool latched)
    {
        float shield = ShieldFraction;
        return latched ? shield < Tweaks.Tank.ShieldLowOffFraction : shield < Tweaks.Tank.ShieldLowOnFraction;
    }

    /// <summary>How full the shield (reactor health) is, 0 to 1.</summary>
    public float ShieldFraction =>
        Math.Clamp(Math.Max(0, (int)Reactor.Health) / (float)Math.Max(1, (int)Reactor.HealthCapacity), 0f, 1f);

    /// <summary>The shield gauge: Ok above half, Bad at or below the low-shield threshold.</summary>
    public StatusLevel ShieldLevel
    {
        get
        {
            float shield = ShieldFraction;
            return shield > Tweaks.Tank.ShieldWarnFraction ? StatusLevel.Ok
                : shield > Tweaks.Tank.ShieldLowOnFraction ? StatusLevel.Warn
                : StatusLevel.Bad;
        }
    }

    /// <summary>How full the tank's ore hold is, 0 to 1.</summary>
    public float HoldFill => Math.Clamp(Hold.Ore / (float)Math.Max(1, Hold.Capacity), 0f, 1f);

    /// <summary>Emberite in the tank's hold, in units (DESIGN.md §6.3).</summary>
    public int OreHeld => Hold.Ore;

    /// <summary>How much emberite the hold takes (<see cref="Tweaks.Ore.TankHold"/> plus the base's cargo hold upgrades).</summary>
    public int OreHoldCapacity => Hold.Capacity;

    /// <summary>True when the hold has no room for another unit: the tank's dig leaves ore in the ground.</summary>
    public bool IsOreHoldFull => Hold.IsFull;

    /// <summary>
    /// True while the live tank is docked at its own base or inside the coverage of its own linked network (as of its
    /// last tick): there the base store pays for what it builds, and its hold unloads (DESIGN.md §6.4).
    /// </summary>
    public bool IsInOwnNetwork => IsDockedAtOwnBase || (!IsDead && Coverage.Covered);

    /// <summary>True on ticks the hold is emptying into the base store: docked, or through the network.</summary>
    public bool IsUnloadingOre { get; private set; }

    /// <summary>
    /// True once the live tank has been outside every base at the end of a tick: the player has driven out of the
    /// base at least once this match.
    /// </summary>
    public bool HasLeftBase { get; private set; }

    /// <summary>Ticks a hit flash lasts after the tank is struck by enemy fire.</summary>
    public const int HitFlashDuration = 18;

    /// <summary>Ticks of protection from enemy fire after a respawned tank leaves its base.</summary>
    public const int SpawnProtectionDuration = 48;

    /// <summary>Longest a respawn shield can last, even if the tank stays in its base.</summary>
    public const int SpawnProtectionCap = 10 * Tweaks.Perf.TargetFps;

    /// <summary>Cause of the most recent armor loss.</summary>
    public DamageCause LastDamageCause => _lifecycle.LastDamageCause;

    /// <summary>What destroyed the tank; <see cref="DamageCause.None"/> while it is alive.</summary>
    public DamageCause DeathCause => _lifecycle.DeathCause;

    /// <summary>Counts down from <see cref="HitFlashDuration"/> after a hit by enemy fire.</summary>
    public int HitFlashTicks => _lifecycle.HitFlashTicks;

    /// <summary>Velocity of the last projectile that hit the tank (zero if it was never hit).</summary>
    public VectorF LastHitVelocity => _lifecycle.LastHitVelocity;

    /// <summary>Time left until the tank respawns; zero while alive or when no lives remain.</summary>
    public TimeSpan RespawnRemaining => _lifecycle.RespawnRemaining;

    /// <summary>Counts down after a respawn; while positive, enemy fire does no damage.</summary>
    public int SpawnProtectionTicks => _lifecycle.SpawnProtectionTicks;

    /// <summary>Counts down after a build key press was refused; <see cref="LastBuildRefusal"/> says why.</summary>
    public int BuildRefusedTicks => _build.RefusedTicks;

    /// <summary>Why the last refused build key press did nothing (valid while <see cref="BuildRefusedTicks"/> runs).</summary>
    public BuildRefusal LastBuildRefusal => _build.LastRefusal;

    /// <summary>Counts down after the tank refused to plant a relay that would not link.</summary>
    public int PlantRefusedTicks => LastBuildRefusal == BuildRefusal.WillNotLink ? BuildRefusedTicks : 0;

    /// <summary>True while the tank carries a machine (a relay) it has built but not planted yet.</summary>
    public bool IsCarryingMachine => _build.IsCarrying;
    public MachineType? CarriedMachineType => _build.CarriedType;

    public Tank(int color, TankBase tankBase, int rngSeed = 0, SimulationSettings? settings = null)
    {
        _thermals = new TankThermals(this, settings ?? SimulationSettings.FromTweaks());
        Color = color;
        Base = tankBase;
        Position = tankBase.Position;
        LivesLeft = Tweaks.Tank.MaxLives;
        Reactor = new Reactor(
            initial: new ReactorState(Tweaks.Tank.InitialHeat, Tweaks.Tank.InitialHealth),
            capacity: new ReactorState(Tweaks.Tank.HeatCapacity, Tweaks.Tank.HealthCapacity));
        Hold = new OreStore(0, tankBase.Upgrades.TankHoldCapacity);
        Turret = new TankTurret(color);
        _build = new TankBuildController(this, color);
        _lifecycle = new TankLifecycle(this, _build);
        _digger = new TankDigger(this, _thermals, rngSeed != 0 ? rngSeed : HashSeed(color, tankBase.Position));
        Direction = _digger.RandomDirection();
        Heat = Tweaks.Tank.InitialHeat;
    }

    /// <summary>One simulation tick. The order of the steps is part of the simulation's result; keep it.</summary>
    public void Advance(in SimulationContext ctx, ControllerOutput input)
    {
        _lifecycle.BeginTick();
        _build.BeginTick();
        _thermals.BeginTick();
        IsUnloadingOre = false;
        SyncHoldCapacity();

        if (IsDead)
        {
            _lifecycle.AdvanceDeath(ctx.Projectiles, ctx.Terrain, ctx.Heat.Ledger, ctx.Events, ctx.IsOccupied);
            return;
        }

        _thermals.AddIdleHeat(ctx.Heat.Ledger);
        AdvanceBaseInteraction(in ctx);
        _thermals.AdvanceNetworkCoverage(in ctx);
        AdvanceNetworkUnload(in ctx);
        AdvanceTurretAim(input);
        Turret.Update(input.ShootPrimary);
        _digger.Move(in ctx, input.MoveSpeed, Turret.Direction, input.ShootPrimary);
        _lifecycle.UpdateSpawnProtection(ctx.CheckBaseCollision(Position) == Base, input.ShootPrimary);
        AdvanceShooting(input, in ctx);
        _digger.CollectPickups(ctx.Terrain, ctx.Heat, ctx.Events);
        _build.Advance(in ctx, input);
        AdvanceUpgrades(input);
        _thermals.Advance(ctx.Heat);
    }

    /// <summary>
    /// Takes armor off the tank and records why, so the cause can be shown to the player. Returns false when nothing
    /// was taken (no damage, already dead, or enemy fire against the respawn shield).
    /// </summary>
    public bool TakeDamage(int amount, DamageCause cause, VectorF hitVelocity = default) =>
        _lifecycle.TakeDamage(amount, cause, hitVelocity);

    /// <summary>Ticks left on the "could not buy that upgrade" feedback after a refused purchase.</summary>
    public int UpgradeRefusedTicks { get; private set; }

    /// <summary>Why the last refused upgrade press bought nothing (valid while <see cref="UpgradeRefusedTicks"/> runs).</summary>
    public UpgradeRefusal LastUpgradeRefusal { get; private set; }

    private int _upgradeHeld;

    /// <summary>
    /// Upgrade keys (edge triggered): docked at its own base, the tank buys the next level of the chosen upgrade from
    /// the base's ore store. Anywhere else, for a maxed upgrade, or when the store cannot pay, the press is refused
    /// and <see cref="LastUpgradeRefusal"/> says why.
    /// </summary>
    private void AdvanceUpgrades(ControllerOutput input)
    {
        if (UpgradeRefusedTicks > 0)
            UpgradeRefusedTicks--;
        bool pressed = input.Upgrade != 0 && input.Upgrade != _upgradeHeld;
        _upgradeHeld = input.Upgrade;
        if (!pressed || input.Upgrade < 1 || input.Upgrade > BaseUpgrades.Count)
            return;
        var upgrade = (BaseUpgrade)(input.Upgrade - 1);
        UpgradeRefusal refusal = !IsDockedAtOwnBase ? UpgradeRefusal.NotDocked
            : Base!.Upgrades.IsMaxed(upgrade) ? UpgradeRefusal.Maxed
            : Base.TryBuyUpgrade(upgrade) ? UpgradeRefusal.None
            : UpgradeRefusal.NotEnoughOre;
        SyncHoldCapacity();
        // A successful press clears any earlier refusal, so its message does not linger over the purchase.
        LastUpgradeRefusal = refusal;
        UpgradeRefusedTicks = refusal != UpgradeRefusal.None ? 48 : 0;
    }

    /// <summary>
    /// What building <paramref name="type"/> here would cost and which stock would pay (DESIGN.md §6.4): in the own
    /// network (<see cref="IsInOwnNetwork"/>) the base store, else, or when the store is short, the hold. The HUD
    /// asks this; the build key follows the same answer.
    /// </summary>
    public BuildQuote QuoteBuild(MachineType type) => _build.Quote(type);

    /// <summary>The store <paramref name="quote"/> is paid from, or null when nothing can pay.</summary>
    internal OreStore? PayerStore(in BuildQuote quote) => quote.Payer switch
    {
        OrePayer.Hold => Hold,
        OrePayer.BaseStore => Base?.Store,
        _ => null,
    };

    /// <summary>The hold takes what the cargo hold upgrades of the base allow.</summary>
    private void SyncHoldCapacity()
    {
        if (Base != null)
            Hold.SetCapacity(Base.Upgrades.TankHoldCapacity);
    }

    /// <summary>End of a world tick: latches <see cref="HasLeftBase"/> once the live tank is outside every base.</summary>
    internal void ObserveBaseExit(TankBases bases)
    {
        if (!IsDead && bases.CheckBaseCollision(Position) == null)
            HasLeftBase = true;
    }

    private bool _wasDocked = true;

    /// <summary>End of a world tick: records docking at and leaving the own base. A respawn is not a docking.</summary>
    internal void ObserveDocking(StepEventLog events)
    {
        bool docked = IsDead || IsDockedAtOwnBase;
        if (docked != _wasDocked && !IsDead)
            events.Record(new StepEvent(docked ? StepEventKind.Docked : StepEventKind.Undocked, Position, Color));
        _wasDocked = docked;
    }

    /// <summary>
    /// Any base the tank is in recharges it and sinks heat around it; its own base also banks its ore and
    /// pumps its heat into the base reservoir.
    /// </summary>
    private const float BaseServicedHeatDrop = 0.05f;

    private void AdvanceBaseInteraction(in SimulationContext ctx)
    {
        var baseColl = ctx.CheckBaseCollision(Position);
        if (baseColl == null) return;

        (int health, float heat) = (Reactor.Health, Heat);
        baseColl.RechargeTank(Reactor, Color);
        bool atOwnBase = baseColl.Color == Color;
        _thermals.SinkAtBase(ctx.Heat, baseColl, atOwnBase);

        if (atOwnBase)
        {
            int banked = Hold.MoveTo(baseColl.Store, Tweaks.Base.HomeAbsorbOre);
            IsUnloadingOre = banked > 0;
            _thermals.PumpHeatAtBase(baseColl);
            // A tank that is only shedding the last fraction of a degree near 0 is not being serviced any more.
            if (Reactor.Health > health || Heat < heat - BaseServicedHeatDrop || banked > 0)
                ctx.Events?.Record(new StepEvent(StepEventKind.BaseServiced, Position, Color));
        }
    }

    // Unload rate credit, in units times ticks per second: a unit moves each time it reaches TargetFps.
    private int _unloadCredit;

    /// <summary>
    /// Inside the coverage of its own linked network (not in a base, where docking banks faster) the hold unloads
    /// into the base store at <see cref="Tweaks.Ore.NetworkUnloadPerSecond"/>, one unit at a time, as long as the
    /// store has room. Each unit travels home along the links from the covering node.
    /// </summary>
    private void AdvanceNetworkUnload(in SimulationContext ctx)
    {
        var store = Base?.Store;
        if (store == null || !Coverage.Covered || Hold.Ore == 0 || store.IsFull)
        {
            _unloadCredit = 0;
            return;
        }
        IsUnloadingOre = true;
        _unloadCredit += Tweaks.Ore.NetworkUnloadPerSecond;
        if (_unloadCredit < Tweaks.Perf.TargetFps)
            return;
        _unloadCredit -= Tweaks.Perf.TargetFps;
        int moved = Hold.MoveTo(store, 1);
        LinkMap.CarryOre(Coverage.Point);
        ctx.Events?.Record(new StepEvent(StepEventKind.OreUnloaded, Position, Color, Count: moved));
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

    private void AdvanceShooting(ControllerOutput input, in SimulationContext ctx)
    {
        if (!input.ShootPrimary) return;
        var bullet = Turret.TryShoot(Position, ctx.Projectiles);
        if (bullet != null)
        {
            _thermals.RecordShot();
            ctx.Events?.Record(new StepEvent(StepEventKind.ShotFired, Position, Color));
        }
    }

    /// <summary>Puts the tank back at its base with full armor, cold, and shielded until it leaves.</summary>
    public void Spawn(ThermalLedger? ledger = null)
    {
        if (Base == null) return;
        Reactor.Add(new ReactorState(0, Reactor.HealthCapacity));
        Position = Base.Position;
        _thermals.ResetForSpawn(ledger);
        _lifecycle.OnSpawned();
    }

    /// <summary>
    /// Destroys the tank: it loses a life, drops what it carries and explodes. The ore in its hold spills into
    /// <paramref name="terrain"/> around it (lost when there is none).
    /// </summary>
    public void Die(ProjectileList? projectiles = null, ITerrainPixelAccessor? terrain = null) =>
        _lifecycle.Die(projectiles, terrain);

    private static int HashSeed(int color, Position pos)
    {
        uint mixed = (uint)(color * 73856093) ^ (uint)(pos.X * 19349663) ^ (uint)(pos.Y * 83492791);
        return (int)FastRandom.Hash32(mixed);
    }
}
