namespace Tunnerer.Desktop.Rendering.Entities;

using Tunnerer.Core.Config;
using Tunnerer.Core.Sensing;
using Tunnerer.Core.Types;

/// <summary>
/// The player's radar: the faint ring of a pulse in flight, and one small glowing dot per <see cref="RadarContact"/>,
/// bright when the pulse has just found it and fading over its lifetime. A return that crossed warm ground is dimmer,
/// and hot ground a ray went through is a red dot, brighter the hotter it is. A blip is dropped once the player
/// can see its cell, where sight shows the real thing.
/// </summary>
public static class RadarQuads
{
    /// <summary>A fresh blip flares for this many ticks, which is what shows the pulse passing.</summary>
    private const float FlashTicks = 8f;

    /// <summary>Half the side of a blip's quad, in cells: room for its glow.</summary>
    private const float HalfSize = 2.2f;

    public static void Emit(EntityQuads quads, Radar radar, VisionMap vision)
    {
        float scale = quads.View.PixelScale;
        // Radar.Tick is the latest step; the frame shows a point up to one step before it.
        float now = radar.Tick - (1f - quads.Alpha);
        EmitPulse(quads, radar);
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
            // The colour's red carries the contact's strength: what was left of the ray, or for heat how hot the cell is.
            byte strength = (byte)Math.Clamp((int)MathF.Round(contact.Strength * 255f), 0, 255);
            quads.Add(cx, cy, 1f, 0f, -HalfSize, HalfSize, -HalfSize, HalfSize, new Color(strength, 0, 0),
                EntityShape.KindRadarBlip, life, (float)contact.Echo, 0f, flash);
        }
    }

    /// <summary>The front of the pulse in flight, between where it was at the last step and where it is now.</summary>
    private static void EmitPulse(EntityQuads quads, Radar radar)
    {
        if (!radar.PulseInFlight)
            return;
        float radius = radar.PulseFront - radar.PulseSpeed * (1f - quads.Alpha);
        if (radius <= 0f)
            return;
        // Room for the line's glow beyond the front.
        float extent = radius + 3f;
        float fade = 1f - radius / radar.PulseRange;
        quads.Add(quads.ScreenX(radar.PulseOrigin.X), quads.ScreenY(radar.PulseOrigin.Y), 1f, 0f,
            -extent, extent, -extent, extent, EntityShape.KindRadarPulse, radius, fade, 0f, 0f);
    }
}
