namespace Emberward.Desktop.Rendering.Entities;

using Emberward.Core;
using Emberward.Core.Rendering;
using Emberward.Core.Sensing;
using Emberward.Core.Types;

/// <summary>
/// What a tank shows when its shield takes a hit (DESIGN.md §4.7): for a moment the veins of the whole shield light up
/// with a wavefront from the impact, and a red patch glows where the shot landed. Hits are read from
/// <see cref="World.StepEvents"/> after each step. Nothing is drawn, and nothing costs anything, while no hit is
/// fresh. Output only.
/// </summary>
public sealed class TankShieldQuads
{
    /// <summary>How long the pulse lives, in simulation steps (about 0.4 s at 24 steps a second).</summary>
    private const int PulseSteps = 9;

    private const float Radius = 8.5f;

    private struct Pulse
    {
        public int Color;
        public long Step;
        public float OffsetX, OffsetY;
        public float Fill;
    }

    private readonly List<Pulse> _pulses = new();
    private long _step;

    /// <summary>Call after each simulation step, while <see cref="World.StepEvents"/> still holds that step's events.</summary>
    public void CaptureStep(World world)
    {
        _step++;
        for (int i = _pulses.Count - 1; i >= 0; i--)
        {
            if (_step - _pulses[i].Step >= PulseSteps + 1)
                _pulses.RemoveAt(i);
        }
        foreach (ref readonly StepEvent e in world.StepEvents)
        {
            if (e.Kind is not (StepEventKind.TankShieldHit or StepEventKind.TankShieldBroken))
                continue;
            int color = e.Color;
            var tank = FindTank(world, color);
            if (tank == null)
                continue;
            // A hit restarts the tank's pulse. Offset from the tank's centre; a hit at the centre (a bite) shows no spot.
            _pulses.RemoveAll(p => p.Color == color);
            _pulses.Add(new Pulse
            {
                Color = color,
                Step = _step,
                OffsetX = e.Position.X - tank.Position.X,
                OffsetY = e.Position.Y - tank.Position.Y,
                Fill = world.TankShieldOf(tank).ShieldFill,
            });
        }
    }

    private static Emberward.Core.Entities.Tanks.Tank? FindTank(World world, int color)
    {
        foreach (var tank in world.TankList.Tanks)
        {
            if (tank.Color == color)
                return tank;
        }
        return null;
    }

    /// <summary>How many pulses are showing (the tanks hit within the last <c>PulseSteps</c> steps).</summary>
    public int PulseCount => _pulses.Count;

    public void Emit(EntityQuads quads, EntityMotion motion, VisionMap? vision)
    {
        if (_pulses.Count == 0)
            return;
        float scale = quads.View.PixelScale;
        float stepNow = _step + quads.Alpha;
        ReadOnlySpan<TankRenderState> tanks = motion.Tanks;
        foreach (var pulse in _pulses)
        {
            float age = (stepNow - pulse.Step) / PulseSteps;
            if (age >= 1f)
                continue;
            for (int i = 0; i < tanks.Length; i++)
            {
                ref readonly TankRenderState tank = ref tanks[i];
                if (tank.Color != pulse.Color || tank.IsDead || !Sight.Sees(vision, tank.Position))
                    continue;
                var (start, drift) = motion.TankMotion(i);
                if (quads.IsOffScreen(quads.ScreenX(start.X), quads.ScreenY(start.Y), (Radius + 2f) * scale))
                    continue;
                float cx = EntityQuads.Centre(start.X);
                float cy = EntityQuads.Centre(start.Y);
                quads.Add(cx, cy, 1f, 0f, -Radius, Radius, -Radius, Radius, TankSprites.GetPixelColor(1, tank.Color),
                    EntityShape.KindTankShield, pulse.Fill, Math.Max(0f, age), pulse.OffsetX, pulse.OffsetY, drift);
            }
        }
    }
}
