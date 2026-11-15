namespace Tunnerer.Desktop.Rendering;

using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Config;

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
        float[] target)
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

    public void BuildGpuTerrainAuxData(TerrainGrid terrain, byte[] auxTarget, byte[] coverageTarget)
    {
        _solidField.Rebuild(terrain);
        WriteRect(terrain, 0, 0, terrain.Width - 1, terrain.Height - 1, auxTarget, coverageTarget);
    }

    /// <summary>
    /// Rebuilds the aux and coverage data for <paramref name="dirtyRect"/> plus the blur margin: a changed cell
    /// alters the blurred fields of every cell within the kernel radius. Returns the padded rect actually written,
    /// which is the rect the caller must upload.
    /// </summary>
    public Rect BuildGpuTerrainAuxRect(TerrainGrid terrain, byte[] auxTarget, byte[] coverageTarget, in Rect dirtyRect)
    {
        RectMath.GetMinMaxInclusive(dirtyRect, out int minX, out int minY, out int maxX, out int maxY);
        const int pad = TerrainSolidField.KernelRadius > TerrainMaterialCoverage.KernelRadius
            ? TerrainSolidField.KernelRadius
            : TerrainMaterialCoverage.KernelRadius;
        minX = Math.Max(0, minX - pad);
        minY = Math.Max(0, minY - pad);
        maxX = Math.Min(terrain.Width - 1, maxX + pad);
        maxY = Math.Min(terrain.Height - 1, maxY + pad);
        _solidField.UpdateRect(terrain, minX, minY, maxX, maxY);
        WriteRect(terrain, minX, minY, maxX, maxY, auxTarget, coverageTarget);
        return RectMath.FromMinMaxInclusive(minX, minY, maxX, maxY);
    }

    private void WriteRect(TerrainGrid terrain, int minX, int minY, int maxX, int maxY, byte[] auxTarget, byte[] coverageTarget)
    {
        TerrainMaterialCoverage.WriteRect(terrain, minX, minY, maxX, maxY, coverageTarget);
        RowParallel.For(minY, maxY + 1, maxX - minX + 1, (from, to) =>
        {
            for (int y = from; y < to; y++)
            {
                int row = y * terrain.Width;
                for (int x = minX; x <= maxX; x++)
                {
                    int idx = row + x;
                    WriteTerrainAux(terrain, idx, terrain.GetPixelRaw(idx), _solidField.SampleAsByte(x, y), auxTarget, idx * 4);
                    coverageTarget[idx * 4 + 3] = GrowthFade.StampAt(idx);
                }
            }
        });
    }

    public static void UpdateGpuTerrainAuxHeatOnly(TerrainGrid terrain, byte[] target, in Rect dirtyRect)
    {
        RectMath.GetMinMaxInclusive(dirtyRect, out int minX, out int minY, out int maxX, out int maxY);
        int w = terrain.Width;

        RowParallel.For(minY, maxY + 1, maxX - minX + 1, (from, to) =>
        {
            for (int y = from; y < to; y++)
            {
                int row = y * w;
                for (int x = minX; x <= maxX; x++)
                {
                    int idx = row + x;
                    target[idx * 4] = TerrainGrid.HeatToByte(terrain.GetHeatTemperature(idx));
                }
            }
        });
    }

    private static void WriteTerrainAux(TerrainGrid terrain, int idx, TerrainPixel pixel, byte sdfValue, byte[] target, int writeIndex)
    {
        // Aux channel layout:
        // R = heat (signed: TerrainGrid.HeatToByte), G = solid field (TerrainSolidField, cave edge at 0.5), B = material class, A = scorch damage level.
        // B is read by nearest-cell Load only (never interpolated). Natural materials are told apart by the
        // material coverage texture; B just marks them solid. Gold has its own code so the shader can draw the vein
        // from the exact cells. Base cells keep their exact code:
        // floor, door, and wall = BaseWallBase + owner colour index (0..7). Keep in sync with Constants.hlsli.
        const byte materialNone = 0;
        const byte materialNatural = 100;
        const byte materialGold = 110;
        const byte materialBaseFloor = 20;
        const byte materialBaseDoor = 30;
        const byte materialBaseWallBase = 40;

        byte material = pixel switch
        {
            TerrainPixel.Dirt or TerrainPixel.DirtGrow or TerrainPixel.Rock
                or TerrainPixel.HotRock or TerrainPixel.FrostRock or TerrainPixel.Concrete => materialNatural,
            TerrainPixel.Gold => materialGold,
            TerrainPixel.BaseCore => materialBaseFloor,
            TerrainPixel.BaseBarrier => materialBaseDoor,
            >= TerrainPixel.BaseMin and <= TerrainPixel.BaseMax =>
                (byte)(materialBaseWallBase + (pixel - TerrainPixel.BaseMin)),
            _ => materialNone,
        };

        target[writeIndex] = TerrainGrid.HeatToByte(terrain.GetHeatTemperature(idx));
        target[writeIndex + 1] = sdfValue;
        target[writeIndex + 2] = material;
        target[writeIndex + 3] = terrain.GetScorchIntensity(idx);
    }
}
