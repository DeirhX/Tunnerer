namespace Emberward.Desktop.Rendering;

using Emberward.Core.Config;
using Emberward.Core.Sensing;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using Emberward.Desktop.Config;
using Emberward.Core.Entities.Tanks;

public sealed class TerrainAuxBuilder
{
    private readonly TerrainSolidField _solidField = new();

    /// <summary>Regrow times written to the coverage texture's alpha (see <see cref="TerrainGrowthFade"/>).</summary>
    public TerrainGrowthFade GrowthFade { get; } = new();

    /// <param name="tankPositions">Where each tank is drawn this frame (cell coordinates), indexed like <paramref name="tanks"/>.</param>
    public int BuildGpuTankHeatGlowData(
        IReadOnlyList<Tank> tanks,
        ReadOnlySpan<PositionF> tankPositions,
        in RenderView view,
        float[] target,
        VisionMap? vision = null)
    {
        int camPixelX = view.CameraPixels.X;
        int camPixelY = view.CameraPixels.Y;
        int pixelScale = view.PixelScale;
        int targetW = view.ViewSize.X;
        int targetH = view.ViewSize.Y;
        int count = 0;
        for (int i = 0; i < tanks.Count && i < tankPositions.Length && count < Tweaks.World.MaxPlayers; i++)
        {
            Tank tank = tanks[i];
            if (tank.IsDead) continue;
            // A tank out of the player's sight must not give itself away by its glow.
            if (vision != null && !vision.IsVisible(tank.Position)) continue;

            float t = tank.Heat / Tweaks.Tank.HeatMax;
            float minVisible = DesktopScreenTweaks.PostTankHeatGlowMinHeat / Tweaks.Tank.HeatMax;
            float visibleAtHeat = 10f / Tweaks.Tank.HeatMax;
            float damageStart = Tweaks.Tank.HeatSafeMax / Tweaks.Tank.HeatMax;
            float earlyRange = Math.Max(0.0001f, visibleAtHeat - minVisible);
            float lateRange = Math.Max(0.0001f, damageStart - visibleAtHeat);
            float earlyT = Math.Clamp((t - minVisible) / earlyRange, 0.0f, 1.0f);
            float lateT = Math.Clamp((t - visibleAtHeat) / lateRange, 0.0f, 1.0f);
            float visibleT = Math.Clamp(0.65f * MathF.Sqrt(earlyT) + 0.35f * lateT, 0.0f, 1.0f);
            if (tank.Reactor.Health < tank.Reactor.HealthCapacity)
            {
                float damageFrac = 1.0f - (float)tank.Reactor.Health / Math.Max(1f, tank.Reactor.HealthCapacity);
                float damageVisual = 0.85f + 0.15f * MathF.Sqrt(Math.Clamp(damageFrac * 8.0f, 0.0f, 1.0f));
                visibleT = Math.Max(visibleT, damageVisual);
            }
            if (visibleT <= 0.0f) continue;

            float radiusFactor = (0.55f + 0.45f * MathF.Sqrt(visibleT)) * 1.35f;
            float glowRadiusPx = pixelScale * (DesktopScreenTweaks.PostTankHeatGlowBaseRadius + DesktopScreenTweaks.PostTankHeatGlowScaleRadius * radiusFactor);
            float cx = (tankPositions[i].X + 0.5f) * pixelScale - camPixelX;
            float cy = (tankPositions[i].Y + 0.5f) * pixelScale - camPixelY;

            if (cx + glowRadiusPx < 0 || cy + glowRadiusPx < 0 || cx - glowRadiusPx >= targetW || cy - glowRadiusPx >= targetH)
                continue;

            int baseIdx = count * 4;
            target[baseIdx + 0] = cx / targetW;
            target[baseIdx + 1] = cy / targetH;
            target[baseIdx + 2] = glowRadiusPx / MathF.Max(targetW, targetH);
            // Store normalized heat visibility; shader derives glow/distortion from this directly.
            target[baseIdx + 3] = visibleT;
            count++;
        }

        return count;
    }

    /// <summary>
    /// Builds the whole aux and coverage data. Heat and scorch are read for every cell, in sight or not: fog hides
    /// ground that has never been seen, and this gives it a state to start from.
    /// </summary>
    public void BuildGpuTerrainAuxData(in TerrainView terrain, byte[] auxTarget, byte[] coverageTarget)
    {
        _solidField.Rebuild(terrain.Pixels);
        WriteRect(terrain with { Vision = null }, 0, 0, terrain.Width - 1, terrain.Height - 1, auxTarget, coverageTarget);
    }

    /// <summary>
    /// Rebuilds the aux and coverage data for <paramref name="dirtyRect"/> plus the blur margin: a changed cell
    /// alters the blurred fields of every cell within the kernel radius. Returns the padded rect actually written,
    /// which is the rect the caller must upload.
    /// </summary>
    public Rect BuildGpuTerrainAuxRect(in TerrainView terrain, byte[] auxTarget, byte[] coverageTarget, in Rect dirtyRect)
    {
        RectMath.GetMinMaxInclusive(dirtyRect, out int minX, out int minY, out int maxX, out int maxY);
        const int pad = TerrainSolidField.KernelRadius > TerrainMaterialCoverage.KernelRadius
            ? TerrainSolidField.KernelRadius
            : TerrainMaterialCoverage.KernelRadius;
        minX = Math.Max(0, minX - pad);
        minY = Math.Max(0, minY - pad);
        maxX = Math.Min(terrain.Width - 1, maxX + pad);
        maxY = Math.Min(terrain.Height - 1, maxY + pad);
        _solidField.UpdateRect(terrain.Pixels, minX, minY, maxX, maxY);
        WriteRect(terrain, minX, minY, maxX, maxY, auxTarget, coverageTarget);
        return RectMath.FromMinMaxInclusive(minX, minY, maxX, maxY);
    }

    private void WriteRect(TerrainView terrain, int minX, int minY, int maxX, int maxY, byte[] auxTarget, byte[] coverageTarget)
    {
        TerrainMaterialCoverage.WriteRect(terrain.Pixels, minX, minY, maxX, maxY, coverageTarget);
        RowParallel.For(minY, maxY + 1, maxX - minX + 1, (from, to) =>
        {
            for (int y = from; y < to; y++)
            {
                int row = y * terrain.Width;
                for (int x = minX; x <= maxX; x++)
                {
                    int idx = row + x;
                    WriteTerrainAux(terrain, idx, terrain.Pixels.GetPixelRaw(idx), _solidField.SampleAsByte(x, y), auxTarget, idx * 4);
                    coverageTarget[idx * 4 + 3] = GrowthFade.StampAt(idx);
                }
            }
        });
    }

    /// <summary>
    /// Rewrites the channels that follow the live world rather than the terrain's pixels, for the cells of
    /// <paramref name="dirtyRect"/> that are in sight: heat, and with <paramref name="includeScorch"/> scorch too
    /// (for ground that has just come into sight).
    /// </summary>
    public static void UpdateGpuTerrainAuxHeatOnly(TerrainView terrain, byte[] target, in Rect dirtyRect, bool includeScorch = false)
    {
        RectMath.GetMinMaxInclusive(dirtyRect, out int minX, out int minY, out int maxX, out int maxY);
        int w = terrain.Width;
        TerrainGrid grid = terrain.Grid;
        VisionMap? vision = terrain.Vision;

        RowParallel.For(minY, maxY + 1, maxX - minX + 1, (from, to) =>
        {
            for (int y = from; y < to; y++)
            {
                int row = y * w;
                for (int x = minX; x <= maxX; x++)
                {
                    int idx = row + x;
                    if (vision != null && !vision.IsVisible(idx))
                        continue;
                    target[idx * 4] = TerrainGrid.HeatToByte(grid.GetHeatTemperature(idx));
                    if (includeScorch)
                        target[idx * 4 + 3] = grid.GetScorchIntensity(idx);
                }
            }
        });
    }

    private static void WriteTerrainAux(in TerrainView terrain, int idx, TerrainPixel pixel, byte sdfValue, byte[] target, int writeIndex)
    {
        // Aux channel layout:
        // R = heat (signed: TerrainGrid.HeatToByte), G = solid field (TerrainSolidField, cave edge at 0.5), B = material class, A = scorch damage level.
        // B is read by nearest-cell Load only (never interpolated). Natural materials are told apart by the
        // material coverage texture; B just marks them solid. Pyrite (plain and core) has its own codes
        // so the shader can draw it from the exact cells. Base cells keep their exact code:
        // floor, core, gate (shut or open), and wall = BaseWallBase + owner colour index (0..7). Keep in sync with
        // Constants.hlsli.
        const byte materialNone = 0;
        const byte materialNatural = 100;
        const byte materialOre = 120;
        const byte materialOreCore = 121;
        const byte materialBaseFloor = 20;
        const byte materialBaseCore = 25;
        const byte materialBaseDoor = 30;
        const byte materialBaseDoorOpen = 31;
        const byte materialBaseWallBase = 40;

        byte material = pixel switch
        {
            TerrainPixel.Dirt or TerrainPixel.DirtGrow or TerrainPixel.Rock
                or TerrainPixel.HotRock or TerrainPixel.FrostRock or TerrainPixel.Concrete => materialNatural,
            TerrainPixel.Pyrite => materialOre,
            TerrainPixel.PyriteCore => materialOreCore,
            TerrainPixel.BaseFloor => materialBaseFloor,
            TerrainPixel.BaseCore => materialBaseCore,
            TerrainPixel.BaseBarrier => materialBaseDoor,
            TerrainPixel.BaseGateOpen => materialBaseDoorOpen,
            >= TerrainPixel.BaseMin and <= TerrainPixel.BaseMax =>
                (byte)(materialBaseWallBase + (pixel - TerrainPixel.BaseMin)),
            _ => materialNone,
        };

        target[writeIndex + 1] = sdfValue;
        target[writeIndex + 2] = material;
        // Heat and scorch follow the live world, so ground out of sight keeps what it last showed.
        if (terrain.Vision != null && !terrain.Vision.IsVisible(idx))
            return;
        target[writeIndex] = TerrainGrid.HeatToByte(terrain.Grid.GetHeatTemperature(idx));
        target[writeIndex + 3] = terrain.Grid.GetScorchIntensity(idx);
    }
}
