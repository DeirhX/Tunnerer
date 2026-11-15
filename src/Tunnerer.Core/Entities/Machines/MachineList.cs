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
                if (machine.LinkPoint != null)
                    machine.LinkPoint.IsEnabled = false; // the link map prunes disabled points
                // A destroyed battery dumps its charge into the ground as heat.
                if (machine.Charge > 0.0)
                {
                    double heat = ctx.Terrain.AddEnergyInRadiusArea(machine.Position,
                        Tweaks.Machine.HarvesterHeatRadius, machine.Charge, TerrainGrid.ThermalField.Terrain);
                    ctx.Terrain.Ledger.RecordFromCharge(heat);
                    machine.Charge -= heat;
                }
                _machines.RemoveAt(i);
                continue;
            }
            _machines[i].Advance(ctx, dt);
        }
    }

    public Machine? TestCollide(Position pos)
    {
        foreach (var m in _machines)
            if (m.IsAlive && m.TestCollide(pos))
                return m;
        return null;
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
                IsGivingCharge: m.LastChargeGiven > 0.0);
        }
        return count;
    }

    public void RemoveAll() => _machines.Clear();
}
