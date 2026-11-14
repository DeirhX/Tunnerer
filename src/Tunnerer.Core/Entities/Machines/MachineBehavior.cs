namespace Tunnerer.Core.Entities.Machines;

using Tunnerer.Core.Types;
using Tunnerer.Core.Config;
using Tunnerer.Core.Resources;
using Tunnerer.Core.Terrain;

/// <summary>
/// Defines per-type machine behavior in one place.
/// Adding a new machine type = one entry in <see cref="MachineBehaviors.Get"/>.
/// </summary>
public record MachineBehavior(
    TimeSpan ActionInterval,
    Action<Machine, ITerrainAccess>? PerformAction,
    Color ActiveColor,
    MaterialAmount BuildCost);

public static class MachineBehaviors
{
    public static readonly MachineBehavior Harvester = new(
        TimeSpan.FromMilliseconds(Tweaks.Machine.HarvesterIntervalMs),
        HarvestNearby,
        Tweaks.Colors.Harvester,
        new MaterialAmount(Tweaks.Machine.HarvesterDirtCost, 0));

    public static readonly MachineBehavior Charger = new(
        TimeSpan.FromMilliseconds(Tweaks.Machine.ChargerIntervalMs),
        null,
        Tweaks.Colors.Charger,
        new MaterialAmount(Tweaks.Machine.ChargerDirtCost, 0));

    public static readonly MachineBehavior Relay = new(
        Tweaks.World.AdvanceStep,
        PumpHeat,
        Tweaks.Colors.Relay,
        new MaterialAmount(Tweaks.Machine.RelayDirtCost, 0));

    public static MachineBehavior Get(MachineType type) => type switch
    {
        MachineType.Harvester => Harvester,
        MachineType.Charger => Charger,
        MachineType.Relay => Relay,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>
    /// Heat pipe: when the relay is connected to a powered link network it pulls heat out of the area around it
    /// and stores it in its owner's base reactor (which vents it at the radiator rate).
    /// </summary>
    private static void PumpHeat(Machine machine, ITerrainAccess terrain)
    {
        machine.LastPumpedEnergy = 0.0;
        machine.LastPumpLimitedByBase = false;
        if (machine.OwnerBase == null || machine.LinkPoint is not { IsPowered: true })
            return;

        machine.LastPumpLimitedByBase = machine.OwnerBase.ReactorRoomEnergy < Tweaks.Machine.RelayHeatPumpPerTick;
        machine.LastPumpedEnergy = machine.OwnerBase.AbsorbHeatFrom(
            terrain, machine.Position, Tweaks.Machine.RelayCoolingRadius, Tweaks.Machine.RelayHeatPumpPerTick);
    }

    private static void HarvestNearby(Machine machine, ITerrainAccess terrain)
    {
        var center = machine.Position;

        // Too hot to work: wait for the ground to cool (a linked relay nearby speeds that up a lot).
        // SampleAverageHeat is normalised (temperature / 255).
        machine.IsStalled = terrain.SampleAverageHeat(center, Tweaks.Machine.HarvesterHeatSenseRadius) * 255f >= Tweaks.Machine.HarvesterStallTemperature;
        if (machine.IsStalled)
            return;

        int range = Tweaks.Machine.HarvestRange;
        for (int dy = -range; dy <= range; dy++)
            for (int dx = -range; dx <= range; dx++)
            {
                if (dx * dx + dy * dy > range * range) continue;
                var pos = new Position(center.X + dx, center.Y + dy);
                if (!terrain.IsInside(pos)) continue;

                if (Pixel.IsDirt(terrain.GetPixelRaw(pos)))
                {
                    terrain.SetPixel(pos, TerrainPixel.Blank);
                    // Working gives off heat into the ground beside the harvester.
                    // The work itself creates this energy (no reactor is debited), so the ledger must book it as injected.
                    double heat = terrain.AddEnergyInRadiusArea(center, Tweaks.Machine.HarvesterHeatRadius,
                        Tweaks.Machine.HarvesterHeatPerHarvest, TerrainGrid.ThermalField.Terrain);
                    terrain.Ledger.RecordInjected(heat);
                    return;
                }
            }
    }
}
