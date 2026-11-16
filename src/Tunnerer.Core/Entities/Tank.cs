namespace Tunnerer.Core.Entities;

using Tunnerer.Core.Types;
using Tunnerer.Core.Config;
using Tunnerer.Core.Resources;
using Tunnerer.Core.Input;
using Tunnerer.Core.Rendering;
using Tunnerer.Core.Entities.Projectiles;
using Tunnerer.Core.Thermal;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Tanks;

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
    public MaterialContainer Resources { get; }
    public int LivesLeft { get; set; }
    public bool IsDead => Reactor.Health <= 0;
    public TankBase? Base { get; }

    /// <summary>
    /// True while the tank is alive and inside its own base: it builds from the base store, is cooled and
    /// unloads its dirt there. Bases never overlap, so this is the same as asking which base the tank is in.
    /// </summary>
    public bool IsDockedAtOwnBase => !IsDead && Base != null && Base.IsInside(Position);
    public TankTurret Turret { get; }
    public float Heat { get; set; }

    /// <summary>Heat the tank can still take before it overheats (<c>HeatSafeMax - Heat</c>); more than HeatSafeMax when pre-cooled.</summary>
    public float HeatBudget => Tweaks.Tank.HeatSafeMax - Heat;

    /// <summary>Largest possible <see cref="HeatBudget"/>: a full coolant charge on top of the safe heat range.</summary>
    public static float HeatBudgetCapacity => Tweaks.Tank.HeatSafeMax + Tweaks.Tank.CoolantCapacity;

    /// <summary>Fraction of the coolant charge left (1 = fully pre-cooled, 0 = at ambient or warmer).</summary>
    public float CoolantFraction => Math.Clamp(-Heat / Tweaks.Tank.CoolantCapacity, 0f, 1f);

    /// <summary>Heat capacity of the tank, from the settings its heat engine uses: tank energy is <c>Heat * ThermalCapacity</c>.</summary>
    public float ThermalCapacity => _thermals.ThermalCapacity;

    /// <summary>True on ticks where the tank took overheat damage.</summary>
    public bool IsOverheating => _thermals.IsOverheating;

    /// <summary>
    /// The network node cooling the tank, as of its last tick. None while it is inside a base (the base cools it there,
    /// not the network) or off network.
    /// </summary>
    public Links.TankCoverage Coverage => _thermals.Coverage;

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
        MathF.Max(0f, (Tweaks.Tank.HeatSafeMax - OffNetworkFloor) / Tweaks.Tank.OffNetworkHeatPerSecond);

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

    /// <summary>
    /// Shots this tank has fired since the match began. Nothing in the simulation reads it; presentation code
    /// compares it between ticks to learn that a shot was fired.
    /// </summary>
    public int ShotsFired { get; private set; }

    /// <summary>Counts down after the tank refused to plant a relay that would not link.</summary>
    public int PlantRefusedTicks => _build.PlantRefusedTicks;

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
        Resources = new MaterialContainer(
            initial: new MaterialAmount(0, 0),
            capacity: new MaterialAmount(Tweaks.Tank.ResourceDirtCapacity, Tweaks.Tank.ResourceMineralsCapacity));
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

        if (IsDead)
        {
            _lifecycle.AdvanceDeath(ctx.Projectiles, ctx.Heat.Ledger);
            return;
        }

        _thermals.AddIdleHeat(ctx.Heat.Ledger);
        AdvanceBaseInteraction(in ctx);
        _thermals.AdvanceNetworkCoverage(in ctx);
        AdvanceTurretAim(input);
        Turret.Update(input.ShootPrimary);
        _digger.Move(in ctx, input.MoveSpeed, Turret.Direction, input.ShootPrimary);
        _lifecycle.UpdateSpawnProtection(ctx.CheckBaseCollision(Position) == Base, input.ShootPrimary);
        AdvanceShooting(input, ctx.Projectiles);
        _digger.CollectPickups(ctx.Terrain, ctx.Heat, ctx.Settings);
        _build.Advance(in ctx, input);
        _thermals.Advance(ctx.Heat, ctx.Settings);
    }

    /// <summary>Takes armor off the tank and records why, so the cause can be shown to the player.</summary>
    public void TakeDamage(int amount, DamageCause cause, VectorF hitVelocity = default) =>
        _lifecycle.TakeDamage(amount, cause, hitVelocity);

    /// <summary>
    /// Dirt available to build with: the tank's own, or the base store if that is larger while docked at the own
    /// base (docking moves the tank's dirt into the store). A machine is affordable when its cost fits.
    /// </summary>
    public int BuildableDirt(bool dockedAtOwnBase) => _build.BuildableDirt(dockedAtOwnBase);

    /// <summary>The store that pays for <paramref name="type"/>, or null if neither can.</summary>
    public MaterialContainer? BuildPayer(MachineType type, bool dockedAtOwnBase) => _build.BuildPayer(type, dockedAtOwnBase);

    /// <summary>
    /// Any base the tank is in recharges it and sinks heat around it; its own base also takes its dirt and
    /// refrigerates it.
    /// </summary>
    private void AdvanceBaseInteraction(in SimulationContext ctx)
    {
        var baseColl = ctx.CheckBaseCollision(Position);
        if (baseColl == null) return;

        baseColl.RechargeTank(Reactor, Color);
        bool atOwnBase = baseColl.Color == Color;
        _thermals.SinkAtBase(ctx.Heat, baseColl, atOwnBase);

        if (atOwnBase)
        {
            baseColl.AbsorbResources(Resources, new MaterialAmount(Tweaks.Base.HomeAbsorbDirt, Tweaks.Base.HomeAbsorbMinerals));
            _thermals.RefrigerateAtBase(baseColl);
        }
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
            _thermals.RecordShot();
            ShotsFired++;
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

    /// <summary>Destroys the tank: it loses a life, drops what it carries and explodes.</summary>
    public void Die(ProjectileList? projectiles = null) => _lifecycle.Die(projectiles);

    /// <summary>CPU drawing of the tank's sprite shape and turret.</summary>
    public void Draw(Surface surface)
    {
        if (IsDead) return;

        TankSpriteShape.ForEachPixel(Direction, Position, (spriteVal, worldPos) =>
        {
            if (!surface.IsInside(worldPos.X, worldPos.Y))
                return true;
            surface.Pixels[worldPos.X + worldPos.Y * surface.Width] = TankSprites.GetPixelColor(spriteVal, Color).ToArgb();
            return true;
        });

        Turret.Draw(surface, Position);
    }

    private static int HashSeed(int color, Position pos)
    {
        uint mixed = (uint)(color * 73856093) ^ (uint)(pos.X * 19349663) ^ (uint)(pos.Y * 83492791);
        return (int)FastRandom.Hash32(mixed);
    }
}
