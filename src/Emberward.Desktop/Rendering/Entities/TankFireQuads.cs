namespace Emberward.Desktop.Rendering.Entities;

using Emberward.Core;
using Emberward.Core.Rendering;
using Emberward.Core.Sensing;
using Emberward.Core.Types;

/// <summary>
/// The fire on a tank whose hull is damaged (DESIGN.md §4.7): soot, flame tongues that grow as the hull falls, and smoke.
/// How big it is comes from <see cref="Emberward.Core.Entities.Tanks.Tank.FireLevel"/>, which Core answers; this only draws.
/// A tank that is not burning costs nothing. Output only.
/// </summary>
public sealed class TankFireQuads
{
    private const float Radius = 8f;
    private const float PhaseCycleSeconds = 4f;

    // Each tank's fire level by colour, from the last step.
    private readonly Dictionary<int, float> _levels = new();

    public void CaptureStep(World world)
    {
        _levels.Clear();
        foreach (var tank in world.TankList.Tanks)
        {
            float level = tank.FireLevel;
            if (level > 0f)
                _levels[tank.Color] = level;
        }
    }

    /// <summary>How many tanks are burning, as of the last step.</summary>
    public int BurningCount => _levels.Count;

    public void Emit(EntityQuads quads, EntityMotion motion, VisionMap? vision)
    {
        if (_levels.Count == 0)
            return;
        float scale = quads.View.PixelScale;
        float t = quads.Phase % PhaseCycleSeconds;
        ReadOnlySpan<TankRenderState> tanks = motion.Tanks;
        for (int i = 0; i < tanks.Length; i++)
        {
            ref readonly TankRenderState tank = ref tanks[i];
            if (tank.IsDead || !_levels.TryGetValue(tank.Color, out float level) || !Sight.Sees(vision, tank.Position))
                continue;
            var (start, drift) = motion.TankMotion(i);
            if (quads.IsOffScreen(quads.ScreenX(start.X), quads.ScreenY(start.Y), (Radius + 2f) * scale))
                continue;
            float cx = EntityQuads.Centre(start.X);
            float cy = EntityQuads.Centre(start.Y);
            // The smoke leans away from the way the tank is driving (a little), so a moving tank trails it.
            float leanX = Math.Clamp(-drift.X * 0.6f, -0.6f, 0.6f);
            quads.Add(cx, cy, 1f, 0f, -Radius, Radius, -14f, Radius, TankSprites.GetPixelColor(1, tank.Color),
                EntityShape.KindTankFire, level, t, leanX, 0f, drift);
        }
    }
}
