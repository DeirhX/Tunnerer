namespace Tunnerer.Core.Entities.Machines;

using Tunnerer.Core.Types;
using Tunnerer.Core.Config;
using Tunnerer.Core.Resources;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Entities.Links;

public enum MachineType { Harvester, Charger, Relay }
public enum MachineState { Template, Transporting, Planted }

public class Machine
{
    public Position Position { get; set; }
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

    private TimeSpan _actionAccumulator;

    public Machine(Position position, MachineType type, int ownerColor)
    {
        Position = position;
        Type = type;
        Behavior = MachineBehaviors.Get(type);
        OwnerColor = ownerColor;
        Reactor = new Reactor(
            initial: new ReactorState(0, Tweaks.Machine.ReactorHealthCapacity),
            capacity: new ReactorState(Tweaks.Machine.ReactorHeatCapacity, Tweaks.Machine.ReactorHealthCapacity));
    }

    public bool IsBlockingCollision => State == MachineState.Planted;
    public bool TestCollide(Position pos) => BoundingBox.IsInside(pos, Position);

    public void Advance(TerrainGrid terrain, TimeSpan dt)
    {
        if (!IsAlive || State != MachineState.Planted) return;
        if (Reactor.Health <= 0) { IsAlive = false; return; }

        _actionAccumulator += dt;
        if (_actionAccumulator < Behavior.ActionInterval) return;
        _actionAccumulator -= Behavior.ActionInterval;

        Behavior.PerformAction?.Invoke(this, terrain);
    }

    public void Draw(Surface surface)
    {
        if (!IsAlive) return;
        int half = BoundingBox.HalfWidth;
        uint color = State == MachineState.Template
            ? Tweaks.Colors.MachineTemplate.ToArgb()
            : Behavior.ActiveColor.ToArgb();

        for (int dy = -half; dy <= half; dy++)
            for (int dx = -half; dx <= half; dx++)
            {
                int px = Position.X + dx, py = Position.Y + dy;
                if (!surface.IsInside(px, py)) continue;
                bool edge = Math.Abs(dx) == half || Math.Abs(dy) == half;
                if (edge)
                    surface.Pixels[px + py * surface.Width] = color;
            }
    }
}
