namespace Tunnerer.Tests;

using Tunnerer.Core;
using Tunnerer.Core.Collision;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Links;
using Tunnerer.Core.Entities.Projectiles;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Rendering;
using Tunnerer.Core.Types;

/// <summary>
/// Projects links, projectiles, marker sprites and tanks onto a cell-resolution pixel buffer for
/// <see cref="TestSimulation.ComputeWorldStateHash"/>. It reproduces, pixel for pixel, the CPU drawing Core used to
/// have, so the fingerprints recorded in <see cref="SimulationFingerprintTests"/> stay valid. The game itself draws
/// entities on the GPU (Desktop's EntityLayerBuilder); nothing here is a game rule.
/// </summary>
internal static class EntityRaster
{
    // Colours the removed Core drawing used. They feed only this hash, so they are frozen here rather than in Tweaks.
    private static readonly uint LinkLive = new Color(0xa0, 0xa0, 0x19).ToArgb();
    private static readonly uint LinkBlocked = new Color(0xa0, 0x30, 0x30).ToArgb();
    private static readonly uint TurretBarrelTip = new Color(0xf3, 0xeb, 0x1c).ToArgb();

    public static void Links(World world, uint[] pix, int w, int h)
    {
        foreach (var link in world.LinkMap.Links)
        {
            if (link.Type == LinkType.Theoretical)
                continue;
            uint color = link.Type == LinkType.Live ? LinkLive : LinkBlocked;
            Raycaster.BresenhamLine(link.From.Position, link.To.Position, pos => Set(pix, w, h, pos.X, pos.Y, color));
        }
    }

    public static void Projectiles(World world, uint[] pix, int w, int h)
    {
        var states = new ProjectileRenderState[world.Projectiles.Count];
        int count = world.Projectiles.CopyRenderStates(states);
        for (int i = 0; i < count; i++)
        {
            var p = states[i];
            if (!p.IsAlive)
                continue;
            var ipos = (Position)p.Position;
            Set(pix, w, h, ipos.X, ipos.Y, ProjectileList.Behaviors[(int)p.Type].DrawColor.ToArgb());
        }
    }

    public static void Sprites(World world, uint[] pix, int w, int h)
    {
        var states = new SpriteRenderState[world.Sprites.Count];
        int count = world.Sprites.CopyRenderStates(states);
        for (int i = 0; i < count; i++)
        {
            var s = states[i];
            if (!s.IsAlive)
                continue;
            uint color = s.Type switch
            {
                SpriteType.FailedInteraction => Tweaks.Colors.FailedInteraction.ToArgb(),
                SpriteType.InfoMarker => Tweaks.Colors.InfoMarker.ToArgb(),
                _ => Color.White.ToArgb(),
            };
            int x = s.Position.X, y = s.Position.Y;
            if (s.Type == SpriteType.FailedInteraction)
            {
                Set(pix, w, h, x - 1, y - 1, color);
                Set(pix, w, h, x + 1, y - 1, color);
                Set(pix, w, h, x, y, color);
                Set(pix, w, h, x - 1, y + 1, color);
                Set(pix, w, h, x + 1, y + 1, color);
            }
            else
            {
                Set(pix, w, h, x, y, color);
            }
        }
    }

    public static void Tanks(World world, uint[] pix, int w, int h)
    {
        int sw = TankSprites.SpriteWidth, sh = TankSprites.SpriteHeight;
        foreach (var tank in world.TankList.Tanks)
        {
            if (tank.IsDead)
                continue;
            var sprite = TankSprites.Sprites[Math.Clamp(tank.Direction, 0, TankSprites.DirectionCount - 1)];
            for (int sy = 0; sy < sh; sy++)
                for (int sx = 0; sx < sw; sx++)
                {
                    byte val = sprite[sx + sy * sw];
                    if (val != 0)
                        Set(pix, w, h, tank.Position.X - sw / 2 + sx, tank.Position.Y - sh / 2 + sy, TankSprites.GetPixelColor(val, tank.Color).ToArgb());
                }

            var tip = tank.Turret.GetBarrelTip(tank.Position);
            Set(pix, w, h, (int)tip.X, (int)tip.Y, TurretBarrelTip);
        }
    }

    private static void Set(uint[] pix, int w, int h, int x, int y, uint color)
    {
        if (x >= 0 && y >= 0 && x < w && y < h)
            pix[x + y * w] = color;
    }
}
