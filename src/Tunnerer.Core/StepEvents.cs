namespace Tunnerer.Core;

using Tunnerer.Core.Snapshots;
using System.Runtime.InteropServices;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Types;

/// <summary>What happened in a <see cref="StepEvent"/>.</summary>
public enum StepEventKind
{
    /// <summary>A tank fired a bullet. <see cref="StepEvent.Color"/> is the tank.</summary>
    ShotFired,

    /// <summary>
    /// A bullet struck something and burst. <see cref="StepEvent.Color"/> is the shooter, <see cref="StepEvent.Target"/>
    /// what it struck. A bullet that flies off the map makes no event.
    /// </summary>
    BulletImpact,

    /// <summary>An explosion threw shrapnel: a bullet burst, a tank's death or a machine's wreck.</summary>
    Explosion,

    /// <summary>A shrapnel fragment struck something hard or burnt out inside the map.</summary>
    ShrapnelSpent,

    /// <summary>Enemy fire took armor off a tank (not blocked by its respawn shield). <see cref="StepEvent.Color"/> is the tank.</summary>
    TankHit,

    /// <summary>A tank was destroyed. <see cref="StepEvent.Position"/> is where it died.</summary>
    TankDied,

    /// <summary>A destroyed tank came back at its base.</summary>
    TankRespawned,

    /// <summary>A tank moved one step under its own power (a respawn is not a move).</summary>
    TankMoved,

    /// <summary>
    /// A tank cleared <see cref="StepEvent.Count"/> cells of terrain by driving into it; <see cref="StepEvent.Torch"/>
    /// when it was torching.
    /// </summary>
    TankDug,


    /// <summary>
    /// A tank dug a cell of emberite into its hold: <see cref="StepEvent.Count"/> units (a core cell is worth more).
    /// </summary>
    OreCollected,

    /// <summary>A live tank drove into its own base.</summary>
    Docked,

    /// <summary>A live tank drove out of its own base.</summary>
    Undocked,

    /// <summary>A tank's own base did something for it this step: repaired its shield, cooled it or banked its ore.</summary>
    BaseServiced,

    /// <summary>A tank built a machine of <see cref="StepEvent.Machine"/> and now carries it.</summary>
    MachineBuilt,

    /// <summary>A machine of <see cref="StepEvent.Machine"/> was planted. <see cref="StepEvent.Color"/> is its owner.</summary>
    MachinePlanted,

    /// <summary>A tank refused to plant the machine it carries because it would not link.</summary>
    PlantRefused,

    /// <summary>
    /// A build key asked for a machine of <see cref="StepEvent.Machine"/> that nothing in reach could pay for (not
    /// enough ore).
    /// </summary>
    BuildRefused,

    /// <summary>
    /// A tank in its own network's coverage unloaded <see cref="StepEvent.Count"/> units of ore into the network, on
    /// their way to the base store. <see cref="StepEvent.Position"/> is the tank.
    /// </summary>
    OreUnloaded,

    /// <summary>
    /// A harvester banked the ore cell at <see cref="StepEvent.Position"/> into its base's store:
    /// <see cref="StepEvent.Count"/> units. <see cref="StepEvent.Color"/> is its owner.
    /// </summary>
    OreHarvested,

    /// <summary>
    /// An enemy bullet took health off a machine that survived it. <see cref="StepEvent.Color"/> is the machine's
    /// owner. The hit that wrecks a machine is <see cref="MachineWrecked"/> instead.
    /// </summary>
    MachineDamaged,

    /// <summary>A machine was shot to pieces. <see cref="StepEvent.Color"/> is its owner.</summary>
    MachineWrecked,
}

/// <summary>What a <see cref="StepEventKind.BulletImpact"/> struck.</summary>
public enum ImpactTarget { Terrain, Tank, Machine }

/// <summary>
/// One thing that happened during a <see cref="World.Advance"/> step. Fields that do not apply to the
/// <see cref="Kind"/> keep their defaults; <see cref="Color"/> is -1 when no tank or side is involved.
/// </summary>
public readonly record struct StepEvent(
    StepEventKind Kind,
    Position Position,
    int Color = -1,
    MachineType Machine = default,
    ImpactTarget Target = default,
    int Count = 0,
    bool Torch = false);

/// <summary>
/// The events of the current step, cleared when the next one begins. It is output only: nothing in the simulation
/// reads it back, so recording never changes a result. The backing list keeps its capacity, so recording does not
/// allocate once it has grown to a busy step's size.
/// </summary>
[NotSaved("output only: what happened in the last step, for sound and the HUD; cleared before each step")]
public sealed class StepEventLog
{
    private readonly List<StepEvent> _events = new();

    public int Count => _events.Count;

    /// <summary>The events recorded since the last <see cref="Clear"/>, in the order they happened.</summary>
    public ReadOnlySpan<StepEvent> Events => CollectionsMarshal.AsSpan(_events);

    public void Record(in StepEvent e) => _events.Add(e);

    public void Clear() => _events.Clear();
}
