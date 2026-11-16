namespace Tunnerer.Desktop.Rendering.Entities;

using Tunnerer.Core.Config;
using Tunnerer.Core.Sensing;
using Tunnerer.Core.Types;

/// <summary>
/// The player's radar blips: one small glowing dot per <see cref="RadarContact"/>, bright when the beam has just
/// found it and fading over its lifetime. A return that crossed warm ground is dimmer, and where heat swallowed
/// the beam the blip is a flickering orange smudge instead. A blip is dropped once the player can see its cell,
/// where sight shows the real thing.
/// </summary>
public static class RadarQuads
{
    /// <summary>A fresh blip flares for this many ticks, which is what shows the beam passing.</summary>
    private const float FlashTicks = 8f;

    /// <summary>Half the side of a blip's quad, in cells: room for its glow.</summary>
    private const float HalfSize = 2.2f;

    public static void Emit(EntityQuads quads, Radar radar, VisionMap vision)
    {
        float scale = quads.View.PixelScale;
        // Radar.Tick is the latest step; the frame shows a point up to one step before it.
        float now = radar.Tick - (1f - quads.Alpha);
        for (int i = 0; i < radar.ContactCount; i++)
        {
            RadarContact contact = radar.ContactAt(i);
            float age = Math.Max(0f, now - contact.BornTick);
            float life = 1f - age / Tweaks.Radar.ContactLifetimeTicks;
            if (life <= 0f || vision.IsVisible(contact.Position))
                continue;

            float cx = quads.ScreenX(contact.Position.X);
            float cy = quads.ScreenY(contact.Position.Y);
            if (quads.IsOffScreen(cx, cy, HalfSize * scale))
                continue;

            float flash = Math.Max(0f, 1f - age / FlashTicks);
            // The colour carries the ray's remaining strength (red) and a per-cell seed (green) that keeps heat
            // blips from flickering in step.
            byte strength = (byte)Math.Clamp((int)MathF.Round(contact.Strength * 255f), 0, 255);
            byte seed = (byte)((contact.Position.X * 73 + contact.Position.Y * 151) & 0xFF);
            quads.Add(cx, cy, 1f, 0f, -HalfSize, HalfSize, -HalfSize, HalfSize, new Color(strength, seed, 0),
                EntityShape.KindRadarBlip, life, (float)contact.Echo, quads.Phase, flash);
        }
    }
}
