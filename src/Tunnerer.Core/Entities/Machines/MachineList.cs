namespace Tunnerer.Core.Entities.Machines;

using Tunnerer.Core.Config;
using Tunnerer.Core.Types;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Rendering;

public class MachineList
{
    private readonly List<Machine> _machines = new();

    public IReadOnlyList<Machine> Machines => _machines;
    public int Count => _machines.Count;

    public Machine Add(Machine machine)
    {
        _machines.Add(machine);
        return machine;
    }

    public void Advance(in MachineContext ctx, TimeSpan dt)
    {
        for (int i = _machines.Count - 1; i >= 0; i--)
        {
            var machine = _machines[i];
            if (!machine.IsAlive)
            {
                Wreck(machine, ctx.Terrain);
                _machines.RemoveAt(i);
                continue;
            }
            _machines[i].Advance(ctx, dt);
        }
    }

    /// <summary>
    /// A destroyed machine leaves the network (the link map rebuilds on its next advance), dumps its battery into the
    /// ground as heat and scorches the ground it stood on, like an explosion's decal, for regrowth to fade.
    /// </summary>
    private static void Wreck(Machine machine, ITerrainAccess terrain)
    {
        if (machine.LinkPoint != null)
            machine.LinkPoint.IsEnabled = false; // the link map prunes disabled points
        if (machine.Charge > 0.0)
        {
            double heat = terrain.AddEnergyInRadiusArea(machine.Position,
                Tweaks.Machine.HarvesterHeatRadius, machine.Charge, TerrainGrid.ThermalField.Terrain);
            terrain.Ledger.RecordFromCharge(heat);
            machine.Charge -= heat;
        }
        if (machine.State == MachineState.Planted)
            Scorch(machine.Position, terrain);
    }

    private static void Scorch(Position center, ITerrainAccess terrain)
    {
        int half = Tweaks.Machine.WreckScorchHalfSize;
        for (int dy = -half; dy <= half; dy++)
            for (int dx = -half; dx <= half; dx++)
            {
                var pos = new Position(center.X + dx, center.Y + dy);
                if (!terrain.IsInside(pos)) continue;
                var pix = terrain.GetPixelRaw(pos);
                // Open ground and dirt burn; rock, gold and base cells keep their material.
                if (pix == TerrainPixel.Blank || Pixel.IsScorched(pix) || Pixel.IsDiggable(pix))
                    terrain.SetPixel(pos, TerrainPixel.Decal); // metered: a dirt cell's heat change is booked
            }
    }

    public int CopyRenderStates(Span<MachineRenderState> destination)
    {
        int count = Math.Min(destination.Length, _machines.Count);
        for (int i = 0; i < count; i++)
        {
            var m = _machines[i];
            destination[i] = new MachineRenderState(
                Position: m.Position,
                Type: m.Type,
                State: m.State,
                IsAlive: m.IsAlive,
                IsPowered: m.LinkPoint?.IsPowered ?? false,
                IsPumping: m.LastPumpedEnergy > 1.0,
                PumpFraction: (float)Math.Clamp(m.LastPumpedEnergy / Tweaks.Machine.RelayHeatPumpPerTick, 0.0, 1.0),
                IsPumpLimited: m.LastPumpLimitedByBase,
                IsStalled: m.IsStalled,
                Heading: m.Heading,
                SenseHeat: m.SenseHeat,
                Charge: m.ChargeFraction,
                IsReturning: m.IsReturning,
                IsCharging: m.ChargingFrom is { IsAlive: true },
                ChargingFrom: m.ChargingFrom?.Position ?? default,
                IsGivingCharge: m.LastChargeGiven > 0.0,
                Anchor: m.Anchor);
        }
        return count;
    }
}
