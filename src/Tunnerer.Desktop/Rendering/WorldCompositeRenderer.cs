namespace Tunnerer.Desktop.Rendering;

using Tunnerer.Core;
using Tunnerer.Core.Collision;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Links;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Projectiles;
using Tunnerer.Core.Rendering;
using Tunnerer.Core.Types;

public sealed class WorldCompositeRenderer
{
    private TankRenderState[] _tankStates = [];
    private ProjectileRenderState[] _projectileStates = [];
    private MachineRenderState[] _machineStates = [];
    private LinkRenderState[] _linkStates = [];
    private SpriteRenderState[] _spriteStates = [];

    public void Compose(World world, uint[] terrainPixels, uint[] compositePixels, Tank? player = null)
    {
        Array.Copy(terrainPixels, compositePixels, terrainPixels.Length);
        var surface = new Surface(compositePixels, world.Terrain.Width, world.Terrain.Height);

        int linkCount = world.LinkMap.CopyRenderStates(EnsureCapacity(ref _linkStates, world.LinkMap.LinkCount));
        DrawLinks(surface, _linkStates, linkCount);
        if (player is { IsDead: false, IsCarryingMachine: true })
            DrawLinkPreview(surface, world.LinkMap.PreviewLink(player.Position, world.Terrain), player.Position);

        int machineCount = world.Machines.CopyRenderStates(EnsureCapacity(ref _machineStates, world.Machines.Count));
        DrawMachines(surface, _machineStates, machineCount);

        int projectileCount = world.Projectiles.CopyRenderStates(EnsureCapacity(ref _projectileStates, world.Projectiles.Count));
        DrawProjectiles(surface, _projectileStates, projectileCount);

        int spriteCount = world.Sprites.CopyRenderStates(EnsureCapacity(ref _spriteStates, world.Sprites.Count));
        DrawSprites(surface, _spriteStates, spriteCount);

        int tankCount = world.TankList.CopyRenderStates(EnsureCapacity(ref _tankStates, world.TankList.Count));
        DrawTanks(surface, _tankStates, tankCount);
    }

    private static Span<T> EnsureCapacity<T>(ref T[] buffer, int requiredCount)
    {
        if (buffer.Length < requiredCount)
            buffer = new T[Math.Max(requiredCount, 16)];
        return buffer;
    }

    private static void DrawLinks(Surface surface, LinkRenderState[] states, int count)
    {
        uint liveColor = Tweaks.Colors.LinkLive.ToArgb();
        uint blockedColor = Tweaks.Colors.LinkBlocked.ToArgb();

        for (int i = 0; i < count; i++)
        {
            ref readonly var link = ref states[i];
            if (!link.IsAlive || link.Type == LinkType.Theoretical)
                continue;

            uint color = link.Type == LinkType.Live ? liveColor : blockedColor;
            Raycaster.BresenhamLine(link.From, link.To, pos =>
            {
                if (surface.IsInside(pos.X, pos.Y))
                    surface.Pixels[pos.X + pos.Y * surface.Width] = color;
            });
        }
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

    private static void DrawMachines(Surface surface, MachineRenderState[] states, int count)
    {
        int half = Tweaks.Machine.BoundingBoxHalfSize;
        uint templateColor = Tweaks.Colors.MachineTemplate.ToArgb();
        uint harvesterColor = Tweaks.Colors.Harvester.ToArgb();
        uint chargerColor = Tweaks.Colors.Charger.ToArgb();
        uint relayColor = Tweaks.Colors.Relay.ToArgb();
        uint relayIdleColor = Tweaks.Colors.RelayIdle.ToArgb();
        uint pumpingColor = new Color(255, 255, 255).ToArgb();
        uint stalledColor = new Color(255, 140, 0).ToArgb();
        bool blinkFast = (Environment.TickCount / 200) % 2 == 0;
        bool blinkSlow = (Environment.TickCount / 500) % 2 == 0;

        for (int i = 0; i < count; i++)
        {
            ref readonly var machine = ref states[i];
            if (!machine.IsAlive)
                continue;

            // A relay being carried looks like a template; a planted one is bright when it is connected to a base.
            uint color = machine.State != MachineState.Planted
                ? templateColor
                : machine.Type switch
                {
                    // A stalled harvester (ground too hot) blinks orange; a working one is steady green.
                    MachineType.Harvester => machine.IsStalled && blinkFast ? stalledColor : harvesterColor,
                    // Relay: steady white = pumping near its full rate; slow blink = partial; fast blink = a trickle.
                    MachineType.Relay => !machine.IsPowered ? relayIdleColor
                        : machine.PumpFraction >= 0.8f ? pumpingColor
                        : machine.IsPumping && (machine.PumpFraction >= 0.3f ? blinkSlow : blinkFast) ? pumpingColor
                        : relayColor,
                    _ => chargerColor,
                };

            for (int dy = -half; dy <= half; dy++)
            {
                for (int dx = -half; dx <= half; dx++)
                {
                    bool edge = Math.Abs(dx) == half || Math.Abs(dy) == half;
                    if (!edge)
                        continue;
                    int px = machine.Position.X + dx;
                    int py = machine.Position.Y + dy;
                    if (surface.IsInside(px, py))
                        surface.Pixels[px + py * surface.Width] = color;
                }
            }

            // Shape cue so state never relies on colour alone: a pixel above a stalled harvester, and above a
            // relay the full base reservoir is holding back.
            if (machine.State == MachineState.Planted && (machine.IsStalled || machine.IsPumpLimited))
            {
                int markX = machine.Position.X, markY = machine.Position.Y - half - 2;
                if (surface.IsInside(markX, markY))
                    surface.Pixels[markX + markY * surface.Width] = stalledColor;
            }
        }
    }

    private static void DrawProjectiles(Surface surface, ProjectileRenderState[] states, int count)
    {
        uint hotTipColor = new Color(255, 242, 170).ToArgb();
        uint hotCoreColor = new Color(255, 150, 52).ToArgb();

        for (int i = 0; i < count; i++)
        {
            ref readonly var projectile = ref states[i];
            if (!projectile.IsAlive)
                continue;

            uint bodyColor = ProjectileList.Behaviors[(int)projectile.Type].DrawColor.ToArgb();
            switch (projectile.Type)
            {
                case ProjectileType.Bullet:
                case ProjectileType.Shrapnel:
                    DrawHotProjectile(surface, projectile.Position, projectile.Velocity, bodyColor, hotCoreColor, hotTipColor);
                    break;
                default:
                    SetPixel(surface, projectile.Position.X, projectile.Position.Y, bodyColor);
                    break;
            }
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

    private static void DrawTanks(Surface surface, TankRenderState[] states, int count)
    {
        uint outlineColor = new Color(12, 10, 8).ToArgb();
        uint flashColor = Color.White.ToArgb();
        uint protectedColor = new Color(90, 230, 255).ToArgb();

        for (int i = 0; i < count; i++)
        {
            var tank = states[i];
            if (tank.IsDead)
                continue;

            // A dark one-pixel outline keeps the tank readable against the textured terrain; it blinks cyan while
            // the tank is protected after a respawn.
            uint outline = tank.IsProtected && (tank.HitFlashTicks + Environment.TickCount / 120) % 2 == 0
                ? protectedColor
                : outlineColor;
            ForEachSpritePixel(tank.Direction, tank.Position, (_, worldPos) =>
            {
                for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                        SetPixel(surface, worldPos.X + ox, worldPos.Y + oy, outline);
            });

            // Blink white while the tank is flashing from a hit.
            bool flash = tank.HitFlashTicks > 0 && (tank.HitFlashTicks / 3) % 2 == 1;
            ForEachSpritePixel(tank.Direction, tank.Position, (spriteValue, worldPos) =>
            {
                if (!surface.IsInside(worldPos.X, worldPos.Y))
                    return;
                surface.Pixels[worldPos.X + worldPos.Y * surface.Width] =
                    flash ? flashColor : TankSprites.GetPixelColor(spriteValue, tank.Color).ToArgb();
            });

            var tip = new PositionF(
                tank.Position.X + tank.TurretDirection.X * Tweaks.Tank.TurretLength,
                tank.Position.Y + tank.TurretDirection.Y * Tweaks.Tank.TurretLength);
            int tx = (int)tip.X;
            int ty = (int)tip.Y;
            if (surface.IsInside(tx, ty))
                surface.Pixels[tx + ty * surface.Width] = Tweaks.Colors.TurretBarrelTip.ToArgb();
        }
    }

    private static void ForEachSpritePixel(int dir, Position origin, Action<byte, Position> visitor)
    {
        dir = Math.Clamp(dir, 0, TankSprites.DirectionCount - 1);
        var sprite = TankSprites.Sprites[dir];
        int w = TankSprites.SpriteWidth;
        int h = TankSprites.SpriteHeight;
        int cx = w / 2;
        int cy = h / 2;

        for (int sy = 0; sy < h; sy++)
        {
            for (int sx = 0; sx < w; sx++)
            {
                byte val = sprite[sx + sy * w];
                if (val == 0)
                    continue;
                var worldPos = new Position(origin.X - cx + sx, origin.Y - cy + sy);
                visitor(val, worldPos);
            }
        }
    }

    private static void SetPixel(Surface surface, int x, int y, uint color)
    {
        if (surface.IsInside(x, y))
            surface.Pixels[x + y * surface.Width] = color;
    }

    private static void DrawHotProjectile(
        Surface surface,
        Position pos,
        VectorF velocity,
        uint bodyColor,
        uint hotCoreColor,
        uint tipColor)
    {
        GetProjectileAxis(velocity, out int ax, out int ay);
        int tx = pos.X + ax;
        int ty = pos.Y + ay;
        int bx = pos.X - ax;
        int by = pos.Y - ay;

        // Small directional 3-pixel body with a heated nose.
        SetPixel(surface, bx, by, bodyColor);
        SetPixel(surface, pos.X, pos.Y, hotCoreColor);
        SetPixel(surface, tx, ty, tipColor);

        // Subtle side glow around the tip to avoid "box" look.
        if (ax != 0 && ay != 0)
        {
            SetPixel(surface, tx - ax, ty, hotCoreColor);
            SetPixel(surface, tx, ty - ay, hotCoreColor);
        }
        else if (ax != 0)
        {
            SetPixel(surface, tx, ty + 1, hotCoreColor);
            SetPixel(surface, tx, ty - 1, hotCoreColor);
        }
        else
        {
            SetPixel(surface, tx + 1, ty, hotCoreColor);
            SetPixel(surface, tx - 1, ty, hotCoreColor);
        }
    }

    private static void GetProjectileAxis(VectorF velocity, out int ax, out int ay)
    {
        float lenSq = velocity.LengthSquared;
        if (lenSq < 1e-6f)
        {
            ax = 1;
            ay = 0;
            return;
        }

        float invLen = 1.0f / MathF.Sqrt(lenSq);
        float nx = velocity.X * invLen;
        float ny = velocity.Y * invLen;

        ax = Math.Sign(nx);
        ay = Math.Sign(ny);
        if (ax == 0 && ay == 0)
            ax = 1;
    }
}
