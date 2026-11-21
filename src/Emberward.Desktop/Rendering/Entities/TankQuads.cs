namespace Emberward.Desktop.Rendering.Entities;

using Emberward.Core.Rendering;
using Emberward.Core.Sensing;
using Emberward.Core.Types;

/// <summary>Tanks: a hull (treads rolling with the distance driven) and a turret, in the team colour.</summary>
public static class TankQuads
{
    // Tank state (params.w), read by TankHull/TankTurret in the entity shader.
    private const float StateNormal = 0f;
    private const float StateHitFlash = 1f;
    private const float StateProtected = 2f;

    /// <summary>
    /// <paramref name="positions"/> are the interpolated tank positions; <paramref name="protectedBlinkOn"/> is the
    /// current phase of a just-respawned tank's blink.
    /// </summary>
    public static void Emit(
        EntityQuads quads, EntityMotion motion, ReadOnlySpan<PositionF> positions,
        float turnSeconds, bool snapHeadings, bool protectedBlinkOn, VisionMap? vision)
    {
        ReadOnlySpan<TankRenderState> tanks = motion.Tanks;
        float scale = quads.View.PixelScale;
        for (int i = 0; i < tanks.Length; i++)
        {
            ref readonly TankRenderState tank = ref tanks[i];
            // Turn every hull, even a dead one's, so it faces the right way when it reappears.
            float heading = motion.UpdateHullHeading(i, tank.Direction, turnSeconds, snapHeadings);
            if (tank.IsDead || !Sight.Sees(vision, tank.Position))
                continue;

            // The tank occupies whole cells; its position is the centre of its middle cell. It is culled where it is
            // drawn now, and handed to the shader as where it was last drawn and how it moves from there.
            if (quads.IsOffScreen(quads.ScreenX(positions[i].X), quads.ScreenY(positions[i].Y), 8f * scale))
                continue;
            var (start, drift) = motion.TankMotion(i);
            float cx = EntityQuads.Centre(start.X);
            float cy = EntityQuads.Centre(start.Y);

            Color team = TankSprites.GetPixelColor(1, tank.Color);
            float state = tank.HitFlashTicks > 0 && (tank.HitFlashTicks / 3) % 2 == 1 ? StateHitFlash
                : tank.IsProtected && protectedBlinkOn ? StateProtected
                : StateNormal;
            float travel = motion.TreadTravel(i, quads.Alpha);

            float hx = MathF.Cos(heading), hy = MathF.Sin(heading);
            quads.Add(cx, cy, hx, hy, -5.5f, 5.5f, -5.5f, 5.5f, team, EntityShape.KindTankHull, hx, hy, travel, state, drift);

            float tx = tank.TurretDirection.X, ty = tank.TurretDirection.Y;
            float turretLength = MathF.Sqrt(tx * tx + ty * ty);
            if (turretLength < 1e-4f) { tx = hx; ty = hy; }
            else { tx /= turretLength; ty /= turretLength; }
            quads.Add(cx, cy, tx, ty, -1.6f, 4.8f, -1.6f, 1.6f, team, EntityShape.KindTankTurret, tx, ty, 0f, state, drift);
        }
    }
}
