namespace Emberward.Desktop.Rendering.Entities;

using Emberward.Core.Entities.Machines;
using Emberward.Core.Rendering;
using Emberward.Core.Sensing;
using Emberward.Core.Types;

/// <summary>
/// Machines, planted or carried, drawn as <see cref="MachineSprites"/> says: the body, and while
/// planted the ground it senses and, on the energy network, its capacitor gauge.
/// </summary>
public static class MachineQuads
{
    public static void Emit(
        EntityQuads quads, EntityMotion motion, float turnSeconds, bool snapHeadings, VisionMap? vision)
    {
        float scale = quads.View.PixelScale;
        float phase = quads.Phase;
        ReadOnlySpan<MachineRenderState> machines = motion.Machines;

        for (int i = 0; i < machines.Length; i++)
        {
            ref readonly MachineRenderState machine = ref machines[i];
            if (!machine.IsAlive || !Sight.Sees(vision, machine.Position))
                continue;

            var (start, drift) = motion.MachineMotion(i);
            if (quads.IsOffScreen(quads.ScreenX(start.X + drift.X * quads.Alpha),
                    quads.ScreenY(start.Y + drift.Y * quads.Alpha), 6f * scale))
                continue;
            float cx = EntityQuads.Centre(start.X);
            float cy = EntityQuads.Centre(start.Y);

            MachineSprite sprite = MachineSprites.Get(machine.Type);
            bool carried = machine.State != MachineState.Planted;
            float flags = (machine.IsStalled ? EntityShape.MachineFlagStalled : 0f) +
                          (carried ? EntityShape.MachineFlagCarried : 0f) +
                          (machine.IsPumpLimited ? EntityShape.MachineFlagLimited : 0f);

            if (!carried && sprite.SenseRadius is float r)
            {
                // The ground the machine senses, coloured by how close it is to stalling.
                quads.Add(cx, cy, 1f, 0f, -r - 0.6f, r + 0.6f, -r - 0.6f, r + 0.6f,
                    EntityShape.KindSenseRing, machine.SenseHeat, r, phase, flags, drift);
            }

            float l = sprite.HalfLength, w = sprite.HalfWidth;
            if (sprite.FacesHeading)
            {
                float heading = motion.UpdateMachineHeading(i, machine.Heading, turnSeconds, snapHeadings);
                float hx = MathF.Cos(heading), hy = MathF.Sin(heading);
                // The drum turns while it works, not while stalled or carried.
                float drum = machine.IsStalled || carried ? 0f : phase * 2.2f;
                quads.Add(cx, cy, hx, hy, -l, l, -w, w, sprite.Kind, hx, hy, drum, flags, drift);
            }
            else
            {
                float powered = machine.IsPowered ? 1f : 0f;
                float gauge = sprite.Gauge?.Invoke(machine) ?? 0f;
                quads.Add(cx, cy, 1f, 0f, -l, l, -w, w, sprite.Kind, powered, gauge, phase, flags, drift);
            }

            if (!carried && sprite.ShowsBattery && machine.HasCapacitor)
            {
                // Capacitor gauge above the machine, upright whichever way it faces.
                quads.Add(cx, cy - 4.6f, 1f, 0f, -2.4f, 2.4f, -0.9f, 0.9f,
                    EntityShape.KindBattery, machine.Charge, 0f, 0f, 0f, drift);
            }
        }
    }
}
