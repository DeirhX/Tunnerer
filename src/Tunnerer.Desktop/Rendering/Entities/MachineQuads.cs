namespace Tunnerer.Desktop.Rendering.Entities;

using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Rendering;
using Tunnerer.Core.Types;

/// <summary>
/// Machines: relays, pylons and harvesters, planted or carried. A planted harvester also shows the ground it senses,
/// its battery gauge and, while a pylon fills it, a charge beam.
/// </summary>
public static class MachineQuads
{
    public static void Emit(EntityQuads quads, EntityInterpolator interpolator, float turnSeconds, bool snapHeadings)
    {
        float scale = quads.View.PixelScale;
        float phase = quads.Phase;
        ReadOnlySpan<MachineRenderState> machines = interpolator.Machines;

        for (int i = 0; i < machines.Length; i++)
        {
            ref readonly MachineRenderState machine = ref machines[i];
            if (!machine.IsAlive)
                continue;

            PositionF at = interpolator.MachinePosition(i, quads.Alpha);
            float x = at.X, y = at.Y;
            float cx = quads.ScreenX(x);
            float cy = quads.ScreenY(y);
            if (quads.IsOffScreen(cx, cy, 6f * scale))
                continue;

            bool carried = machine.State != MachineState.Planted;
            float flags = (machine.IsStalled ? EntityShape.MachineFlagStalled : 0f) +
                          (carried ? EntityShape.MachineFlagCarried : 0f) +
                          (machine.IsPumpLimited ? EntityShape.MachineFlagLimited : 0f);

            switch (machine.Type)
            {
                case MachineType.Harvester:
                {
                    if (!carried)
                    {
                        // The ground the harvester senses, coloured by how close it is to stalling.
                        float r = Tweaks.Machine.HarvesterHeatSenseRadius + 1.5f;
                        quads.Add(cx, cy, 1f, 0f, -r - 0.6f, r + 0.6f, -r - 0.6f, r + 0.6f,
                            EntityShape.KindSenseRing, machine.SenseHeat, r, phase, flags);
                    }
                    float heading = interpolator.UpdateMachineHeading(i, machine.Heading, turnSeconds, snapHeadings);
                    float hx = MathF.Cos(heading), hy = MathF.Sin(heading);
                    // The drum turns while it works, not while stalled or carried.
                    float drum = machine.IsStalled || carried ? 0f : phase * 2.2f;
                    quads.Add(cx, cy, hx, hy, -3.4f, 3.4f, -3.0f, 3.0f,
                        EntityShape.KindHarvester, hx, hy, drum, flags);
                    if (!carried)
                    {
                        if (machine.IsCharging)
                            LinkQuads.AddChargeBeam(quads, machine.ChargingFrom.X, machine.ChargingFrom.Y, x, y, 1f);
                        // Battery gauge above the crawler, upright whichever way it faces.
                        quads.Add(cx, cy - 4.6f * scale, 1f, 0f, -2.4f, 2.4f, -0.9f, 0.9f,
                            EntityShape.KindBattery, machine.Charge, machine.IsReturning ? 1f : 0f, phase,
                            machine.IsCharging ? 1f : 0f);
                    }
                    break;
                }
                case MachineType.Pylon:
                {
                    quads.Add(cx, cy, 1f, 0f, -3.6f, 3.6f, -3.6f, 3.6f, EntityShape.KindPylon,
                        machine.IsPowered ? 1f : 0f, machine.IsGivingCharge ? 1f : 0f, phase, flags);
                    break;
                }
                case MachineType.Relay:
                {
                    float powered = machine.IsPowered ? 1f : 0f;
                    quads.Add(cx, cy, 1f, 0f, -3.6f, 3.6f, -3.6f, 3.6f, EntityShape.KindRelay,
                        powered, machine.PumpFraction, phase, flags);
                    break;
                }
            }
        }
    }
}
