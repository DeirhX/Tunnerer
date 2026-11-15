namespace Tunnerer.Desktop.Rendering;

using Tunnerer.Core;
using Tunnerer.Core.Collision;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Links;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Rendering;
using Tunnerer.Core.Types;

/// <summary>
/// Draws the cell-resolution entities (links, machines, marker sprites) over a copy of the terrain pixels.
/// Tanks and projectiles are not here: <see cref="EntityLayerBuilder"/> draws them at screen resolution.
/// </summary>
public sealed class WorldCompositeRenderer
{
    private SpriteRenderState[] _spriteStates = [];

    public void Compose(World world, uint[] terrainPixels, uint[] compositePixels, Tank? player = null)
    {
        Array.Copy(terrainPixels, compositePixels, terrainPixels.Length);
        var surface = new Surface(compositePixels, world.Terrain.Width, world.Terrain.Height);

        // Links and machines are drawn by the entity layer (EntityLayerBuilder / EntityPs.hlsl).
        if (player is { IsDead: false, IsCarryingMachine: true, CarriedMachineType: MachineType.Relay })
            DrawLinkPreview(surface, world.LinkMap.PreviewLink(player.Position, world.Terrain), player.Position);

        DrawHarvesterHeatRings(surface, world, player);

        int spriteCount = world.Sprites.CopyRenderStates(EnsureCapacity(ref _spriteStates, world.Sprites.Count));
        DrawSprites(surface, _spriteStates, spriteCount);
    }

    private static Span<T> EnsureCapacity<T>(ref T[] buffer, int requiredCount)
    {
        if (buffer.Length < requiredCount)
            buffer = new T[Math.Max(requiredCount, 16)];
        return buffer;
    }

    /// <summary>
    /// Shows where a carried relay would connect: a dashed line to the network point, bright where the line of
    /// sight is clear and red past the first blocking pixel, with a cross on that pixel.
    /// </summary>
    private static void DrawLinkPreview(Surface surface, LinkPreview preview, Position from)
    {
        // Corners of the square the relay would cool (half-width RelayCoolingRadius).
        uint areaColor = new Color(110, 200, 255).ToArgb();
        int r = Tweaks.Machine.RelayCoolingRadius;
        for (int sx = -1; sx <= 1; sx += 2)
        {
            for (int sy = -1; sy <= 1; sy += 2)
            {
                int cx = from.X + sx * r, cy = from.Y + sy * r;
                for (int k = 0; k < 4; k++)
                {
                    SetPixel(surface, cx - sx * k, cy, areaColor);
                    SetPixel(surface, cx, cy - sy * k, areaColor);
                }
            }
        }

        if (preview.Status is not (LinkPreviewStatus.Live or LinkPreviewStatus.Blocked))
            return;

        uint clearColor = new Color(120, 255, 140).ToArgb();
        uint blockedColor = new Color(255, 70, 60).ToArgb();
        bool pastBlock = false;
        int step = 0;
        Raycaster.BresenhamLine(from, preview.Target, pos =>
        {
            if (preview.Status == LinkPreviewStatus.Blocked && pos == preview.BlockedAt)
                pastBlock = true;
            if ((step++ & 3) < 2)
                SetPixel(surface, pos.X, pos.Y, pastBlock ? blockedColor : clearColor);
        });

        if (preview.Status == LinkPreviewStatus.Blocked)
        {
            var b = preview.BlockedAt;
            for (int d = -2; d <= 2; d++)
            {
                SetPixel(surface, b.X + d, b.Y + d, blockedColor);
                SetPixel(surface, b.X + d, b.Y - d, blockedColor);
            }
        }
    }

    /// <summary>
    /// A dotted ring around the tank while it carries a harvester, marking the ground the harvester would sense
    /// (planted harvesters draw their own ring in the entity layer). Its colour is the live temperature there: green while the harvester can work, amber as it
    /// nears the stall temperature, orange and solid once it is too hot. A carried harvester therefore shows whether
    /// planting here would work.
    /// </summary>
    private static void DrawHarvesterHeatRings(Surface surface, World world, Tank? player)
    {
        if (player is { IsDead: false, IsCarryingMachine: true, CarriedMachineType: MachineType.Harvester })
            DrawSenseRing(surface, world, player.Position);
    }

    private static void DrawSenseRing(Surface surface, World world, Position center)
    {
        int radius = Tweaks.Machine.HarvesterHeatSenseRadius + 1;
        float temperature = world.Terrain.SampleAverageHeat(center, Tweaks.Machine.HarvesterHeatSenseRadius) * 255f;
        float stall = Tweaks.Machine.HarvesterStallTemperature;
        bool tooHot = temperature >= stall;
        uint color = tooHot ? new Color(255, 140, 0).ToArgb()
            : temperature >= stall * 0.6f ? new Color(255, 215, 60).ToArgb()
            : new Color(90, 255, 120).ToArgb();

        // Dotted while fine, solid when stalled (a shape cue as well as a colour one).
        int steps = Math.Max(24, radius * 8);
        for (int i = 0; i < steps; i++)
        {
            if (!tooHot && i % 2 == 1)
                continue;
            double angle = i * Math.PI * 2.0 / steps;
            int px = center.X + (int)Math.Round(Math.Cos(angle) * radius);
            int py = center.Y + (int)Math.Round(Math.Sin(angle) * radius);
            if (surface.IsInside(px, py))
                surface.Pixels[px + py * surface.Width] = color;
        }
    }

    private static void DrawSprites(Surface surface, SpriteRenderState[] states, int count)
    {
        for (int i = 0; i < count; i++)
        {
            ref readonly var sprite = ref states[i];
            if (!sprite.IsAlive)
                continue;

            uint color = sprite.Type switch
            {
                SpriteType.FailedInteraction => Tweaks.Colors.FailedInteraction.ToArgb(),
                SpriteType.InfoMarker => Tweaks.Colors.InfoMarker.ToArgb(),
                _ => Color.White.ToArgb(),
            };

            int x = sprite.Position.X;
            int y = sprite.Position.Y;
            if (sprite.Type == SpriteType.FailedInteraction)
            {
                SetPixel(surface, x - 1, y - 1, color);
                SetPixel(surface, x + 1, y - 1, color);
                SetPixel(surface, x, y, color);
                SetPixel(surface, x - 1, y + 1, color);
                SetPixel(surface, x + 1, y + 1, color);
            }
            else
            {
                SetPixel(surface, x, y, color);
            }
        }
    }

    private static void SetPixel(Surface surface, int x, int y, uint color)
    {
        if (surface.IsInside(x, y))
            surface.Pixels[x + y * surface.Width] = color;
    }
}
