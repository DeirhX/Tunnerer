namespace Emberward.Core.Entities.Machines;

using Emberward.Core.Types;
using Emberward.Core.Config;
using Emberward.Core.Resources;
using Emberward.Core.Terrain;
using Emberward.Core.Network;
using Emberward.Core.Entities.Bases;

public enum MachineType { Harvester, Relay, Generator, Turret, Gatling }
public enum MachineState { Template, Transporting, Planted }

public class Machine
{
    private Position _position;

    /// <summary>Where the machine is. The list it belongs to files it by position, so a move is reported to it.</summary>
    public Position Position
    {
        get => _position;
        set
        {
            if (value == _position)
                return;
            Position from = _position;
            _position = value;
            List?.NoteMoved(this, from);
        }
    }

    /// <summary>The list this machine was added to, or null.</summary>
    internal MachineList? List { get; set; }

    /// <summary>Its place in the order of <see cref="MachineList.Machines"/>: a machine added later has a higher one.</summary>
    public int Sequence { get; internal set; }

    public MachineType Type { get; }
    public MachineBehavior Behavior { get; }
    public MachineState State { get; set; } = MachineState.Template;
    public Reactor Reactor { get; }
    public int OwnerColor { get; }
    public BoundingBox BoundingBox { get; } = new(Tweaks.Machine.BoundingBoxHalfSize, Tweaks.Machine.BoundingBoxHalfSize);
    public bool IsAlive { get; set; } = true;

    /// <summary>Planted in the ground and not destroyed: a machine that acts, links, sees and can be hit.</summary>
    public bool IsStanding => IsAlive && State == MachineState.Planted;

    /// <summary>Standing (<see cref="IsStanding"/>) and belonging to <paramref name="owner"/>.</summary>
    public bool StandsFor(TankBase owner) => IsStanding && OwnerBase == owner;

    /// <summary>Node in the link network once planted; <see cref="LinkPoint.IsPowered"/> says whether a base can reach it.</summary>
    public LinkPoint? LinkPoint { get; set; }

    /// <summary>Base this machine delivers to (relays pump heat into its reactor).</summary>
    public TankBase? OwnerBase { get; set; }

    /// <summary>Energy a relay moved into its base during its last action (0 when unpowered or the base is full).</summary>
    public double LastPumpedEnergy { get; set; }

    /// <summary>
    /// Charge this generator stored, or potential charge the base engine made from this relay's heat, last tick.
    /// </summary>
    public double LastChargeMade { get; set; }

    /// <summary>True when a relay could not pump its full rate because the base reservoir was nearly full.</summary>
    public bool LastPumpLimitedByBase { get; set; }

    /// <summary>
    /// A relay: the ground's <see cref="ITerrainHeatSource.QuietHeatStamp"/> when it last found no heat to pump, or
    /// negative. While the ground still gives that number there is still none, and it does not look.
    /// </summary>
    internal long ColdGroundStamp { get; set; } = -1;

    /// <summary>
    /// True when a harvester skipped its last action because the ground around it was too hot, or a turret's ground is
    /// hot enough to slow its fire.
    /// </summary>
    public bool IsStalled { get; set; }

    /// <summary>
    /// A harvester's ground temperature at its last action, as a fraction of its stall temperature; a turret's, as a
    /// fraction of the temperature that slows it most.
    /// </summary>
    public float SenseHeat { get; set; }

    /// <summary>A turret or a gatling: ticks until it looks for a target again (<see cref="TurretAction"/>).</summary>
    public int TurretCooldown { get; internal set; }

    /// <summary>Where a harvester was planted: it roams and mines within <c>HarvestRange</c> of this point.</summary>
    public Position Anchor { get; set; }

    /// <summary>
    /// The last step a harvester crawled (zero before it first moves), or where a turret last aimed: the way it faces.
    /// </summary>
    public Offset Heading { get; set; }

    /// <summary>
    /// Charge in the machine's capacitor on the energy network (DESIGN.md §4.9), as of its last settling
    /// (<see cref="EnergyNetwork"/>); ask <see cref="World.MachinePowerOf"/> for the level now. Zero without the network.
    /// </summary>
    public double Charge { get; set; }

    /// <summary>
    /// Capacitor size: on the energy network every planted machine has its full capacitor (<see cref="World.PlantMachine"/>);
    /// without it no machine has one.
    /// </summary>
    public double ChargeCapacity { get; internal set; }

    public float ChargeFraction => ChargeCapacity > 0.0 ? (float)(Charge / ChargeCapacity) : 0f;

    /// <summary>A harvester: out of its base's network coverage at its last action, so it held its ore work.</summary>
    public bool IsOffNetwork { get; set; }

    /// <summary>A harvester: no ore left in its work area at its last action.</summary>
    public bool HasNoOre { get; set; }

    /// <summary>
    /// A harvester: at its last action the only ore left in its work area was lump core, which only the tank's torch
    /// takes. Implies <see cref="HasNoOre"/>.
    /// </summary>
    public bool HasOnlyCoreOre { get; set; }

    /// <summary>Ore in its work area is guarded by a nest with creatures left, which it passes over (<see cref="MachineContext.IsOreGuarded"/>).</summary>
    public bool OreGuarded { get; set; }

    /// <summary>A harvester: actions of work done on <see cref="OreWorkCell"/> (a cell takes a few).</summary>
    public int OreWork { get; set; }

    /// <summary>A harvester: the ore cell its <see cref="OreWork"/> belongs to. Work never carries to another cell.</summary>
    public Position? OreWorkCell { get; set; }

    private TimeSpan _actionAccumulator;

    public Machine(Position position, MachineType type, int ownerColor)
    {
        _position = position;
        Type = type;
        Behavior = MachineBehaviors.Get(type);
        OwnerColor = ownerColor;
        Reactor = new Reactor(
            initial: new ReactorState(0, Tweaks.Machine.ReactorHealthCapacity),
            capacity: new ReactorState(Tweaks.Machine.ReactorHeatCapacity, Tweaks.Machine.ReactorHealthCapacity));
    }

    public bool IsBlockingCollision => State == MachineState.Planted;

    /// <summary>
    /// Turns up to <paramref name="amount"/> of the capacitor's charge into heat in the ground around the machine,
    /// booked in the ledger as charge turned back into heat.
    /// </summary>
    internal void DischargeAsHeat(ITerrainAccess terrain, double amount) => Charge -= ReleaseChargeAsHeat(terrain, amount);

    /// <summary>
    /// Puts <paramref name="charge"/> into the ground around the machine as heat, booked in the ledger as charge turned
    /// back into heat. Returns the heat put there; the caller takes exactly that from the store it came from.
    /// </summary>
    internal double ReleaseChargeAsHeat(ITerrainAccess terrain, double charge)
    {
        double heat = terrain.AddEnergyInRadiusArea(Position, Tweaks.Machine.HarvesterHeatRadius, charge,
            TerrainGrid.ThermalField.Terrain);
        terrain.Ledger.RecordFromCharge(heat);
        return heat;
    }
    public bool TestCollide(Position pos) => BoundingBox.IsInside(pos, Position);

    /// <summary>
    /// Takes <paramref name="amount"/> from the machine's health; at zero it dies, and <see cref="MachineList.Advance"/>
    /// wrecks it on the same tick. Returns true when this hit destroyed it.
    /// </summary>
    public bool TakeDamage(int amount)
    {
        if (amount <= 0 || !IsStanding)
            return false;
        Reactor.Exhaust(new ReactorState(0, amount));
        if (Reactor.Health > 0)
            return false;
        IsAlive = false;
        return true;
    }

    public void Advance(in MachineContext ctx, TimeSpan dt)
    {
        if (!IsStanding) return;

        var interval = Behavior.ActionInterval;
        if (interval > TimeSpan.Zero)
        {
            _actionAccumulator += dt;
            if (_actionAccumulator < interval) return;
            _actionAccumulator -= interval;
        }

        Behavior.PerformAction?.Invoke(this, ctx);
    }

    /// <summary>
    /// Mixes the machine's state that <see cref="WorldStateHash.Compute"/> does not read and that changes later
    /// results: when it next acts, a harvester's memory, a relay's cold-ground stamp.
    /// </summary>
    internal ulong MixLatent(ulong hash)
    {
        hash = Fnv1a.Mix(hash, _actionAccumulator);
        hash = Fnv1a.Mix(Fnv1a.Mix(hash, Anchor.X), Anchor.Y);
        hash = Fnv1a.Mix(Fnv1a.Mix(hash, Heading.X), Heading.Y);
        hash = Fnv1a.Mix(hash, OreWork);
        hash = OreWorkCell is Position cell
            ? Fnv1a.Mix(Fnv1a.Mix(Fnv1a.Mix(hash, 1), cell.X), cell.Y)
            : Fnv1a.Mix(hash, 0);
        // Mixed only when set, so the digest of a world without turrets is what it always was.
        if (TurretCooldown != 0)
            hash = Fnv1a.Mix(hash, TurretCooldown);
        return Fnv1a.Mix(hash, (ulong)ColdGroundStamp);
    }
}
