namespace Tunnerer.Core.Sensing;

using Tunnerer.Core.Snapshots;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Bases;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Core.Terrain;

/// <summary>
/// What each side someone asked about sees (fog of war, <see cref="VisionMap"/>) and its tank's radar
/// (<see cref="Radar"/>). Output only: nothing in the simulation reads it, and a world nobody asks pays nothing for it.
/// <see cref="World"/> runs it as the last system of a step and answers for it.
/// </summary>
[NotSaved("output only: vision and radar, which nothing in the simulation reads")]
internal sealed class SideSensing(TerrainGrid terrain, MachineList machines, TankList tanks)
{
    private readonly List<(Tank Tank, VisionMap Map)> _visions = new();
    private readonly List<(Tank Tank, Radar Radar)> _radars = new();
    private readonly List<VisionSource> _visionSources = new();
    private readonly List<RadarTarget> _radarTargets = new();

    /// <summary>
    /// Starts tracking what <paramref name="tank"/>'s side sees and returns the map, which every
    /// <see cref="Advance"/> then keeps up to date. The side sees through its tank, its base and its machines:
    /// the tank and the base along open tunnel, link nodes (relays, pylons) over their whole coverage circles, every
    /// machine a little around itself.
    /// </summary>
    public VisionMap TrackVision(Tank tank)
    {
        foreach (var tracked in _visions)
            if (tracked.Tank == tank)
                return tracked.Map;
        var map = new VisionMap(terrain);
        _visions.Add((tank, map));
        UpdateVision(tank, map);
        return map;
    }

    /// <summary>
    /// Starts <paramref name="tank"/>'s radar and returns it; every <see cref="Advance"/> then runs its pulses while
    /// the tank lives. It reports only what the tank's side cannot already see, so it tracks that side's vision too.
    /// </summary>
    public Radar TrackRadar(Tank tank) => RadarOf(tank) ?? AddRadar(tank);

    private Radar AddRadar(Tank tank)
    {
        var radar = new Radar(TrackVision(tank));
        _radars.Add((tank, radar));
        return radar;
    }

    /// <summary>The radar <see cref="TrackRadar"/> started for <paramref name="tank"/>, or null if none was.</summary>
    public Radar? RadarOf(Tank tank)
    {
        foreach (var tracked in _radars)
            if (tracked.Tank == tank)
                return tracked.Radar;
        return null;
    }

    public void Advance()
    {
        foreach (var (tank, map) in _visions)
            UpdateVision(tank, map);

        foreach (var (tank, radar) in _radars)
        {
            if (tank.IsDead)
            {
                radar.Idle();
                continue;
            }
            // Everything that is not terrain and reflects: the other tanks and planted machines. The side's own
            // machines are left out: it knows where it put them, so their echo would tell it nothing.
            _radarTargets.Clear();
            foreach (var machine in machines.Machines)
                if (machine.IsStanding && (tank.Base is null || machine.OwnerBase != tank.Base))
                    _radarTargets.Add(new RadarTarget(machine.Position, machine.BoundingBox.HalfWidth));
            foreach (var other in tanks.Tanks)
                if (other != tank && !other.IsDead)
                    _radarTargets.Add(new RadarTarget(other.Position, Tweaks.Radar.TankHalfSize, Alive: true));
            radar.Advance(terrain, terrain, tank.Position, tank.Color, _radarTargets,
                tank.Base is { } home ? RadarReach.Scaled(home.Upgrades.RadarRangeScale, home.Upgrades.RadarHeatBudgetScale)
                    : RadarReach.Base);
        }
    }

    /// <summary>
    /// Hands the map everything <paramref name="tank"/>'s side sees through and the cells that changed since the map
    /// last looked. The map keeps the cast of every source that is where it was, so listing them costs next to
    /// nothing.
    /// </summary>
    private void UpdateVision(Tank tank, VisionMap map)
    {
        _visionSources.Clear();
        if (!tank.IsDead)
            _visionSources.Add(new VisionSource(tank.Position, Tweaks.Vision.TankRadius, Tweaks.Vision.TankNearRadius));
        if (tank.Base is TankBase owner)
        {
            const int near = Tweaks.Vision.MachineNearRadius;
            _visionSources.Add(new VisionSource(owner.Position, owner.Upgrades.BaseCoverageRadius, near));
            foreach (var machine in machines.Machines)
            {
                if (!machine.StandsFor(owner))
                    continue;
                // A relay or pylon sees its whole coverage circle, through the ground, linked or not.
                int reach = machine.Behavior.IsLinkNode ? owner.Upgrades.NodeCoverageRadius : near;
                _visionSources.Add(new VisionSource(machine.Position, reach, reach));
            }
        }
        map.Update(terrain, _visionSources, terrain.GetChangeList());
    }
}
