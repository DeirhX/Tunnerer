namespace Tunnerer.Core.Entities.Machines;

using Tunnerer.Core.Types;
using Tunnerer.Core.Config;
using Tunnerer.Core.Resources;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Network;

public enum MachineType { Harvester, Pylon, Relay }
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

    /// <summary>Node in the link network once planted; <see cref="LinkPoint.IsPowered"/> says whether a base can reach it.</summary>
    public LinkPoint? LinkPoint { get; set; }

    /// <summary>Base this machine delivers to (relays pump heat into its reactor).</summary>
    public TankBase? OwnerBase { get; set; }

    /// <summary>Energy a relay moved into its base during its last action (0 when unpowered or the base is full).</summary>
    public double LastPumpedEnergy { get; set; }

    /// <summary>
    /// Charge the base engine made from this relay's heat during its last action, counted even when the charge store
    /// was full and could not keep it.
    /// </summary>
    public double LastChargeMade { get; set; }

    /// <summary>True when a relay could not pump its full rate because the base reservoir was nearly full.</summary>
    public bool LastPumpLimitedByBase { get; set; }

    /// <summary>
    /// A relay: the ground's <see cref="ITerrainHeatSource.QuietHeatStamp"/> when it last found no heat to pump, or
    /// negative. While the ground still gives that number there is still none, and it does not look.
    /// </summary>
    internal long ColdGroundStamp { get; set; } = -1;

    /// <summary>True when a harvester skipped its last action because the ground around it was too hot.</summary>
    public bool IsStalled { get; set; }

    /// <summary>A harvester's ground temperature at its last action, as a fraction of its stall temperature.</summary>
    public float SenseHeat { get; set; }

    /// <summary>Where a harvester was planted: it roams and mines within <c>HarvestRange</c> of this point.</summary>
    public Position Anchor { get; set; }

    /// <summary>The last step a harvester crawled (zero before it first moves); also the way it faces.</summary>
    public Offset Heading { get; set; }

    /// <summary>Charge in the machine's battery; harvesters spend it as they work (DESIGN.md §4.5).</summary>
    public double Charge { get; set; }

    /// <summary>Battery size; zero for machines without one (relays, pylons).</summary>
    public double ChargeCapacity { get; }

    public float ChargeFraction => ChargeCapacity > 0.0 ? (float)(Charge / ChargeCapacity) : 0f;

    /// <summary>True while a low harvester is heading back to a pylon to recharge.</summary>
    public bool IsReturning { get; set; }

    /// <summary>A harvester: out of its base's network coverage at its last action, so it held its ore work.</summary>
    public bool IsOffNetwork { get; set; }

    /// <summary>A harvester: no ore left in its work area at its last action.</summary>
    public bool HasNoOre { get; set; }

    /// <summary>
    /// A harvester: at its last action the only ore left in its work area was lump core, which only the tank's torch
    /// takes. Implies <see cref="HasNoOre"/>.
    /// </summary>
    public bool HasOnlyCoreOre { get; set; }

    /// <summary>A harvester: actions of work done on <see cref="OreWorkCell"/> (a cell takes a few).</summary>
    public int OreWork { get; set; }

    /// <summary>A harvester: the ore cell its <see cref="OreWork"/> belongs to. Work never carries to another cell.</summary>
    public Position? OreWorkCell { get; set; }

    /// <summary>The pylon that charged this battery at its last action, or null; drawn as a charge beam.</summary>
    public Machine? ChargingFrom { get; set; }

    /// <summary>Charge a pylon handed out at its last action (zero when it charged nothing).</summary>
    public double LastChargeGiven { get; set; }

    /// <summary>A pylon: the batteries it charged at its last action, whose <see cref="ChargingFrom"/> names it.</summary>
    internal List<Machine> ChargedAtLastAction { get; } = new();

    private TimeSpan _actionAccumulator;

    public Machine(Position position, MachineType type, int ownerColor)
    {
        _position = position;
        Type = type;
        Behavior = MachineBehaviors.Get(type);
        OwnerColor = ownerColor;
        // Built empty: planting fills it from the owner base's store (World.PlantMachine), so no charge appears from nothing.
        ChargeCapacity = Behavior.BatteryCapacity;
        Reactor = new Reactor(
            initial: new ReactorState(0, Tweaks.Machine.ReactorHealthCapacity),
            capacity: new ReactorState(Tweaks.Machine.ReactorHeatCapacity, Tweaks.Machine.ReactorHealthCapacity));
    }

    public bool IsBlockingCollision => State == MachineState.Planted;
    public bool TestCollide(Position pos) => BoundingBox.IsInside(pos, Position);

    /// <summary>
    /// Takes <paramref name="amount"/> from the machine's health; at zero it dies, and <see cref="MachineList.Advance"/>
    /// wrecks it on the same tick. Returns true when this hit destroyed it.
    /// </summary>
    public bool TakeDamage(int amount)
    {
        if (amount <= 0 || !IsAlive || State != MachineState.Planted)
            return false;
        Reactor.Exhaust(new ReactorState(0, amount));
        if (Reactor.Health > 0)
            return false;
        IsAlive = false;
        return true;
    }

    public void Advance(in MachineContext ctx, TimeSpan dt)
    {
        if (!IsAlive || State != MachineState.Planted) return;

        var interval = Behavior.ActionInterval;
        if (interval > TimeSpan.Zero)
        {
            _actionAccumulator += dt;
            if (_actionAccumulator < interval) return;
            _actionAccumulator -= interval;
        }

        Behavior.PerformAction?.Invoke(this, ctx);
    }
}
