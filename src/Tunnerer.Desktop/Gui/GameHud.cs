namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Input;
using Tunnerer.Core.Entities.Links;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Desktop.Rendering;

public class GameHud
{
    public const float BottomPanelHeight = 223f;

    private nint _energyIconTex, _shieldIconTex;
    private nint _panelFrameTex, _buildPanelTex;
    private nint _digitStripTex;

    private const int DigitCount = 10;
    private const int DigitCellW = 17;
    private const int DigitCellH = 20;
    private const int DigitStripW = DigitCellW * DigitCount; // 170
    private const float DigitSpacing = 1f;

    private float _time;
    private bool _lowEnergyLatched;
    private bool _shieldLowLatched;
    private bool _hasLeftBase;
    private float _thermalHintUntil = -1f;
    private bool _thermalHintShown;
    private bool _dockedAtOwnBase;
    private int _lastPoweredRelays;
    private float _relayLinkedUntil = -1f;
    private float _lastBudget = float.NaN;
    private float _budgetTrend;
    private float _shownEnergyFill = -1f;
    private float _shownShieldFill = -1f;

    // Gauge shapes inside the HUD art (texture pixels = ImGui units from each panel's top-left corner).
    private static readonly Vector2 EnergyOrbCenter = new(168f, 72f);
    private const float EnergyOrbRadius = 47f;
    private static readonly Vector2[] ShieldOutline =
    [
        new(44f, 44f), new(60f, 40f), new(82f, 37f), new(104f, 40f), new(121f, 44f), new(121f, 80f),
        new(114f, 100f), new(100f, 116f), new(82f, 128f), new(64f, 116f), new(50f, 100f), new(44f, 80f),
    ];

    // Layout of the bottom strip (ImGui units): the four art panels drawn side by side.
    private const float EnergyPanelW = 245f, ShieldPanelW = 145f, MaterialsPanelW = 230f, BuildPanelW = 404f;
    private const float StripW = EnergyPanelW + ShieldPanelW + MaterialsPanelW + BuildPanelW; // 1024

    // Build slots inside build_panel.png (offsets from the panel's top-left), 3 columns x 2 rows.
    private static readonly float[] BuildSlotX = [25f, 117f, 209f];
    private static readonly float[] BuildSlotY = [44f, 105f];
    private const float BuildSlotW = 80f, BuildSlotH = 56f;

    private static readonly Vector4 TextGood = new(0.55f, 1.0f, 0.55f, 1f);
    private static readonly Vector4 TextWarn = new(1.0f, 0.82f, 0.30f, 1f);
    private static readonly Vector4 TextBad = new(1.0f, 0.40f, 0.35f, 1f);
    private static readonly Vector4 TextInfo = new(0.55f, 0.85f, 1.0f, 1f);
    private static readonly Vector4 TextDim = new(0.80f, 0.80f, 0.80f, 1f);

    public (float x, float y, float w, float h) ViewportRect { get; private set; }
    public (float x, float y)? CrosshairScreenPos { get; set; }

    public void Init(nint energyIcon, nint shieldIcon, nint panelFrame, nint buildPanel, nint digitStrip)
    {
        _energyIconTex = energyIcon;
        _shieldIconTex = shieldIcon;
        _panelFrameTex = panelFrame;
        _buildPanelTex = buildPanel;
        _digitStripTex = digitStrip;
    }

    public void Draw(
        nint gameTextureId,
        int texW,
        int texH,
        Tank player,
        World world,
        float deltaTime,
        bool heatDebugOverlayEnabled,
        PostProcessPassFlags postPassFlags,
        bool showPostPassOverlay)
    {
        _time += MathF.Max(0f, deltaTime);
        DrawGameViewport(gameTextureId, texW, texH);
        DrawCrosshair();
        DrawHitFeedback(player);
        DrawBottomPanel(player);
        DrawHeatNetworkOverlay(player, world);
        DrawWarnings(player, heatDebugOverlayEnabled);
        DrawDeathOverlay(player, world);
        if (showPostPassOverlay)
            DrawPostPassOverlay(heatDebugOverlayEnabled, postPassFlags);
    }

    private void DrawGameViewport(nint gameTextureId, int texW, int texH)
    {
        var displaySize = ImGui.GetIO().DisplaySize;

        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(displaySize.X, displaySize.Y), ImGuiCond.Always);

        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                    ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
                    ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoBackground;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);

        if (ImGui.Begin("##Viewport", flags))
        {
            var cursorPos = ImGui.GetCursorScreenPos();
            ImGui.SetCursorPos(Vector2.Zero);
            ImGui.Image(gameTextureId, new Vector2(texW, texH));
            ViewportRect = (cursorPos.X, cursorPos.Y, texW, texH);
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private void DrawCrosshair()
    {
        if (CrosshairScreenPos is not { } pos) return;
        var vp = ViewportRect;
        if (pos.x < vp.x || pos.y < vp.y || pos.x >= vp.x + vp.w || pos.y >= vp.y + vp.h) return;

        var dl = ImGui.GetForegroundDrawList();
        const float armLen = 10f;
        const float gap = 3f;
        const uint col = 0xFFFFFFFF;
        dl.AddLine(new Vector2(pos.x - armLen, pos.y), new Vector2(pos.x - gap, pos.y), col, 2f);
        dl.AddLine(new Vector2(pos.x + gap, pos.y), new Vector2(pos.x + armLen, pos.y), col, 2f);
        dl.AddLine(new Vector2(pos.x, pos.y - armLen), new Vector2(pos.x, pos.y - gap), col, 2f);
        dl.AddLine(new Vector2(pos.x, pos.y + gap), new Vector2(pos.x, pos.y + armLen), col, 2f);
    }

    private void DrawBottomPanel(Tank player)
    {
        var displaySize = ImGui.GetIO().DisplaySize;
        var dl = ImGui.GetForegroundDrawList();

        const float h = BottomPanelHeight;
        float startX = MathF.Floor((displaySize.X - StripW) * 0.5f);
        float drawY = displaySize.Y - h;

        float x = startX;
        dl.AddImage(_energyIconTex, new Vector2(x, drawY), new Vector2(x + EnergyPanelW, drawY + h));
        x += EnergyPanelW;
        dl.AddImage(_shieldIconTex, new Vector2(x, drawY), new Vector2(x + ShieldPanelW, drawY + h));
        x += ShieldPanelW;
        float materialsX = x;
        dl.AddImage(_panelFrameTex, new Vector2(x, drawY), new Vector2(x + MaterialsPanelW, drawY + h));
        x += MaterialsPanelW;
        float buildX = x;
        dl.AddImage(_buildPanelTex, new Vector2(x, drawY), new Vector2(x + BuildPanelW, drawY + h));

        DrawLiquidGauges(dl, player, startX, startX + EnergyPanelW, drawY);

        // ENERGY: the work (digging, shooting) the tank can still do before it overheats. A docked tank is
        // pre-cooled with coolant, which lifts the value above 100.
        int energy = player.IsDead ? 0 : (int)MathF.Max(0f, MathF.Round(player.HeatBudget));
        float plateX = startX + 105f;
        float plateW = 130f;
        float plateY = drawY + 168f;
        DrawNumberCentered(dl, energy, plateX, plateY, plateW);
        DrawEnergyTrend(dl, player, plateX + plateW - 18f, plateY + 10f);
        if (!player.IsDead)
            DrawEnergyBar(dl, player, plateX + 8f, drawY + 192f, plateW - 16f);

        // SHIELD: the tank's armor.
        int health = Math.Max(0, player.Reactor.Health);
        float sPlateX = startX + EnergyPanelW + 10f;
        float sPlateW = 125f;
        float sPlateY = drawY + 166f;
        DrawNumberCentered(dl, health, sPlateX, sPlateY, sPlateW);
        float shieldFill = player.Reactor.HealthCapacity > 0 ? health / (float)player.Reactor.HealthCapacity : 0f;
        DrawBar(dl, sPlateX + 8f, drawY + 191f, sPlateW - 16f, 5f, shieldFill,
            shieldFill > 0.5f ? 0xFFFFC040u : shieldFill > 0.25f ? 0xFF30C0FFu : 0xFF3040FFu);

        // MATERIALS: dirt carried, which is what structures are built from.
        DrawNumberCentered(dl, player.Resources.Current.Dirt, materialsX + 15f, drawY + 166f, MaterialsPanelW - 30f);

        DrawBuildSlots(dl, player, AvailableRelayDirt(player, _dockedAtOwnBase), buildX, drawY);

        // The COMMAND button has no function yet; dim it like the unavailable build slots.
        dl.AddRectFilled(new Vector2(buildX + 77f, drawY + 172f), new Vector2(buildX + 277f, drawY + 212f), 0x90000000u, 10f);
    }

    /// <summary>
    /// Small arrow beside the energy number: down (red) while digging or firing drains it, up (green) while it
    /// recovers. The rate is smoothed so the arrow does not flicker.
    /// </summary>
    private void DrawEnergyTrend(ImDrawListPtr dl, Tank player, float x, float y)
    {
        float budget = player.HeatBudget;
        if (!float.IsNaN(_lastBudget) && !player.IsDead)
            _budgetTrend = _budgetTrend * 0.9f + (budget - _lastBudget) * 0.1f;
        _lastBudget = budget;
        if (player.IsDead || MathF.Abs(_budgetTrend) < 0.02f)
            return;

        const float s = 6f;
        if (_budgetTrend < 0f)
            dl.AddTriangleFilled(new Vector2(x - s, y - s * 0.6f), new Vector2(x + s, y - s * 0.6f), new Vector2(x, y + s * 0.8f), 0xFF4050FFu);
        else
            dl.AddTriangleFilled(new Vector2(x - s, y + s * 0.6f), new Vector2(x + s, y + s * 0.6f), new Vector2(x, y - s * 0.8f), 0xFF50E060u);
    }

    /// <summary>
    /// Diablo-style liquid gauges in the art: the energy orb and the shield crystal are full of "liquid" up to
    /// their level and dark above it, with a softly waving bright surface. The coolant charge shows as a cyan
    /// band at the top of the orb; a low level pulses red, and the shield flashes when hit.
    /// </summary>
    private void DrawLiquidGauges(ImDrawListPtr dl, Tank player, float energyPanelX, float shieldPanelX, float panelY)
    {
        float capacity = Tank.HeatBudgetCapacity;
        float energyFill = player.IsDead ? 0f : Math.Clamp(player.HeatBudget / capacity, 0f, 1f);
        float shieldFill = Math.Clamp(Math.Max(0, player.Reactor.Health) / (float)Math.Max(1, player.Reactor.HealthCapacity), 0f, 1f);
        // Ease the shown levels toward the real ones so the liquid moves rather than jumps.
        _shownEnergyFill = _shownEnergyFill < 0f ? energyFill : _shownEnergyFill + (energyFill - _shownEnergyFill) * 0.15f;
        _shownShieldFill = _shownShieldFill < 0f ? shieldFill : _shownShieldFill + (shieldFill - _shownShieldFill) * 0.20f;

        bool pulse = (int)(_time * 4f) % 2 == 0;

        // Energy orb.
        var c = new Vector2(energyPanelX, panelY) + EnergyOrbCenter;
        float r = EnergyOrbRadius;
        float top = c.Y - r, bottom = c.Y + r;
        float level = bottom - 2f * r * _shownEnergyFill;
        float normalLevel = bottom - 2f * r * (Tweaks.Tank.HeatSafeMax / capacity);
        if (_shownEnergyFill > Tweaks.Tank.HeatSafeMax / capacity)
            FillCircleBand(dl, c, r, level, normalLevel, 0x70FFD040u);       // coolant charge (cyan)
        if (energyFill < 0.2f * Tweaks.Tank.HeatSafeMax / capacity && energyFill > 0f && pulse)
            FillCircleBand(dl, c, r, level, bottom, 0x603030FFu);            // low energy (red pulse)
        FillCircleBand(dl, c, r, top, level, 0xC8080808u);                   // empty part
        DrawSurface(dl, level, CircleHalfWidth(c, r, level), c.X, energyFill > 0f ? 0xFFB0FFB0u : 0u);

        // Shield crystal.
        var o = new Vector2(shieldPanelX, panelY);
        var poly = new Vector2[ShieldOutline.Length];
        float sTop = float.MaxValue, sBottom = float.MinValue;
        for (int i = 0; i < poly.Length; i++)
        {
            poly[i] = o + ShieldOutline[i];
            sTop = MathF.Min(sTop, poly[i].Y);
            sBottom = MathF.Max(sBottom, poly[i].Y);
        }
        float sLevel = sBottom - (sBottom - sTop) * _shownShieldFill;
        if (player.HitFlashTicks > Tank.HitFlashDuration - 6)
            FillPolygonBand(dl, poly, sLevel, sBottom, 0x90FFFFFFu);         // hit flash
        else if (shieldFill < 0.25f && shieldFill > 0f && pulse)
            FillPolygonBand(dl, poly, sLevel, sBottom, 0x703030FFu);         // low shield (red pulse)
        FillPolygonBand(dl, poly, sTop, sLevel, 0xC8080808u);                // empty part
        var (sx0, sx1) = PolygonSpanAtY(poly, sLevel);
        if (sx1 > sx0)
            DrawSurface(dl, sLevel, (sx1 - sx0) * 0.5f, (sx0 + sx1) * 0.5f, shieldFill > 0f ? 0xFFFFE0A0u : 0u);
    }

    private static float CircleHalfWidth(Vector2 c, float r, float y)
    {
        float dy = y - c.Y;
        return dy * dy >= r * r ? 0f : MathF.Sqrt(r * r - dy * dy);
    }

    /// <summary>Fills the part of a disc between two heights (clip to the band, then draw the disc).</summary>
    private static void FillCircleBand(ImDrawListPtr dl, Vector2 c, float r, float y0, float y1, uint color)
    {
        if (y1 <= y0)
            return;
        dl.PushClipRect(new Vector2(c.X - r - 1f, y0), new Vector2(c.X + r + 1f, y1), true);
        dl.AddCircleFilled(c, r, color, 48);
        dl.PopClipRect();
    }

    /// <summary>Fills the part of a convex polygon between two heights.</summary>
    private static void FillPolygonBand(ImDrawListPtr dl, Vector2[] poly, float y0, float y1, uint color)
    {
        if (y1 <= y0)
            return;
        float x0 = float.MaxValue, x1 = float.MinValue;
        foreach (var p in poly)
        {
            x0 = MathF.Min(x0, p.X);
            x1 = MathF.Max(x1, p.X);
        }
        dl.PushClipRect(new Vector2(x0 - 1f, y0), new Vector2(x1 + 1f, y1), true);
        dl.AddConvexPolyFilled(ref poly[0], poly.Length, color);
        dl.PopClipRect();
    }

    /// <summary>Left and right x where a horizontal line crosses a convex polygon (equal if it misses).</summary>
    private static (float x0, float x1) PolygonSpanAtY(Vector2[] poly, float y)
    {
        float x0 = float.MaxValue, x1 = float.MinValue;
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            if ((y < MathF.Min(a.Y, b.Y)) || (y > MathF.Max(a.Y, b.Y)) || a.Y == b.Y)
                continue;
            float x = a.X + (y - a.Y) / (b.Y - a.Y) * (b.X - a.X);
            x0 = MathF.Min(x0, x);
            x1 = MathF.Max(x1, x);
        }
        return x0 <= x1 ? (x0, x1) : (0f, 0f);
    }

    /// <summary>A gently waving bright line where the liquid meets the empty part.</summary>
    private void DrawSurface(ImDrawListPtr dl, float y, float halfWidth, float centerX, uint color)
    {
        if (color == 0u || halfWidth < 3f)
            return;
        const int segments = 12;
        var points = new Vector2[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float u = i / (float)segments;
            float x = centerX - halfWidth + 2f * halfWidth * u;
            points[i] = new Vector2(x, y + MathF.Sin(u * 9f + _time * 5f) * 1.2f);
        }
        dl.AddPolyline(ref points[0], points.Length, color & 0x00FFFFFFu | 0x50000000u, ImDrawFlags.None, 4f);
        dl.AddPolyline(ref points[0], points.Length, color, ImDrawFlags.None, 1.5f);
    }

    /// <summary>Energy bar: green-to-red for the normal range, with the coolant charge as a cyan extension.</summary>
    private void DrawEnergyBar(ImDrawListPtr dl, Tank player, float x, float y, float w)
    {
        const float barH = 5f;
        float capacity = Tank.HeatBudgetCapacity;
        float normalW = w * (Tweaks.Tank.HeatSafeMax / capacity);
        float budget = MathF.Max(0f, player.HeatBudget);
        float normalFill = MathF.Min(budget, Tweaks.Tank.HeatSafeMax) / Tweaks.Tank.HeatSafeMax;
        float coolFill = MathF.Max(0f, budget - Tweaks.Tank.HeatSafeMax) / Tweaks.Tank.CoolantCapacity;

        dl.AddRectFilled(new Vector2(x - 1f, y - 1f), new Vector2(x + w + 1f, y + barH + 1f), 0xC0000000u);
        uint normalColor = normalFill > 0.5f ? 0xFF50E060u : normalFill > 0.2f ? 0xFF30C8FFu : 0xFF3040FFu;
        if (normalFill < 0.2f && (int)(_time * 4f) % 2 == 0)
            normalColor = 0xFF8080FFu;
        dl.AddRectFilled(new Vector2(x, y), new Vector2(x + normalW * normalFill, y + barH), normalColor);
        if (coolFill > 0f)
            dl.AddRectFilled(new Vector2(x + normalW, y), new Vector2(x + normalW + (w - normalW) * coolFill, y + barH), 0xFFFFD040u);
        dl.AddLine(new Vector2(x + normalW, y - 1f), new Vector2(x + normalW, y + barH + 1f), 0x80FFFFFFu);
    }

    private static void DrawBar(ImDrawListPtr dl, float x, float y, float w, float h, float fill, uint color)
    {
        fill = Math.Clamp(fill, 0f, 1f);
        dl.AddRectFilled(new Vector2(x - 1f, y - 1f), new Vector2(x + w + 1f, y + h + 1f), 0xC0000000u);
        dl.AddRectFilled(new Vector2(x, y), new Vector2(x + w * fill, y + h), color);
    }

    /// <summary>
    /// The relay is the only structure that can be built today: its slot shows how much of the dirt cost is
    /// collected and the key that builds it; the other slots are dimmed.
    /// </summary>
    /// <summary>Dirt that can pay for a relay: the tank's own, or the base store while docked at the own base.</summary>
    private static int AvailableRelayDirt(Tank player, bool docked)
    {
        int own = player.Resources.Current.Dirt;
        if (!docked || player.Base == null || own >= Tweaks.Machine.RelayDirtCost)
            return own;
        return Math.Max(own, player.Base.Materials.Current.Dirt);
    }

    private void DrawBuildSlots(ImDrawListPtr dl, Tank player, int dirt, float panelX, float panelY)
    {
        // Slot 0 builds a relay (E), slot 1 a harvester (R). While a machine is in hand only its own slot is lit:
        // pressing the key again plants it.
        var carried = player.IsCarryingMachine ? player.CarriedMachineType : null;

        for (int row = 0; row < BuildSlotY.Length; row++)
        {
            for (int col = 0; col < BuildSlotX.Length; col++)
            {
                if (row == 0 && col <= 1)
                    continue;
                var min = new Vector2(panelX + BuildSlotX[col], panelY + BuildSlotY[row]);
                dl.AddRectFilled(min, min + new Vector2(BuildSlotW, BuildSlotH), 0xA8000000u, 6f);
            }
        }

        DrawBuildSlot(dl, player, panelX, panelY, col: 0, MachineType.Relay, "RELAY", "E", Tweaks.Machine.RelayDirtCost, dirt, carried);
        DrawBuildSlot(dl, player, panelX, panelY, col: 1, MachineType.Harvester, "HARVEST", "R", Tweaks.Machine.HarvesterDirtCost, dirt, carried);
    }

    private void DrawBuildSlot(ImDrawListPtr dl, Tank player, float panelX, float panelY, int col, MachineType type,
        string name, string key, int cost, int dirt, MachineType? carried)
    {
        float progress = Math.Clamp(dirt / (float)cost, 0f, 1f);
        bool ready = progress >= 1f;
        bool carryingThis = carried == type;
        bool blocked = carried != null && !carryingThis; // the other machine is in hand

        var sMin = new Vector2(panelX + BuildSlotX[col], panelY + BuildSlotY[0]);
        var sMax = sMin + new Vector2(BuildSlotW, BuildSlotH);
        if (blocked)
            dl.AddRectFilled(sMin, sMax, 0xA8000000u, 6f);
        else if (!ready && !carryingThis)
        {
            float fillTop = sMax.Y - BuildSlotH * progress;
            dl.AddRectFilled(sMin, new Vector2(sMax.X, fillTop), 0x80000000u, 6f);
        }

        uint border = carryingThis
            ? ((int)(_time * 3f) % 2 == 0 ? 0xFF40FFFFu : 0xFF20A0A0u)
            : ready && !blocked ? 0xFF40FF60u : 0xFF707070u;
        dl.AddRect(sMin, sMax, border, 6f, ImDrawFlags.None, 2.5f);

        string label = carryingThis ? $"PLANT [{key}]" : ready ? $"{name} [{key}]" : $"{dirt}/{cost}";
        DrawOutlinedText(dl, 15f, new Vector2(sMin.X + 4f, sMax.Y - 18f), label, blocked ? 0xFF909090u : 0xFFFFFFFFu);
        // The key is always visible, even before the slot is affordable.
        if (!ready && !carryingThis)
            DrawOutlinedText(dl, 13f, new Vector2(sMin.X + 4f, sMin.Y + 3f), $"[{key}] {name}", 0xFFB0B0B0u);
    }

    private static void DrawOutlinedText(ImDrawListPtr dl, float size, Vector2 pos, string text, uint color)
    {
        var font = ImGui.GetFont();
        uint shadow = 0xE0000000u;
        dl.AddText(font, size, pos + new Vector2(1f, 1f), shadow, text);
        dl.AddText(font, size, pos + new Vector2(-1f, 1f), shadow, text);
        dl.AddText(font, size, pos + new Vector2(1f, -1f), shadow, text);
        dl.AddText(font, size, pos + new Vector2(-1f, -1f), shadow, text);
        dl.AddText(font, size, pos, color, text);
    }

    private static void DrawCenteredText(ImDrawListPtr dl, float size, float centerX, float y, string text, uint color)
    {
        var font = ImGui.GetFont();
        float w = font.CalcTextSizeA(size, float.MaxValue, 0f, text).X;
        DrawOutlinedText(dl, size, new Vector2(MathF.Floor(centerX - w * 0.5f), y), text, color);
    }

    /// <summary>Red edge vignette after a hit, with a wedge pointing back towards where the shot came from.</summary>
    private void DrawHitFeedback(Tank player)
    {
        var vp = ViewportRect;
        if (vp.w <= 0 || player.IsDead || player.HitFlashTicks <= 0)
            return;

        float k = player.HitFlashTicks / (float)Tank.HitFlashDuration;
        uint edge = (uint)(k * 150f) << 24 | 0x0000E0u;
        const uint clear = 0x000000E0u;
        var dl = ImGui.GetForegroundDrawList();
        float t = MathF.Min(vp.w, vp.h) * 0.12f;
        float x0 = vp.x, y0 = vp.y, x1 = vp.x + vp.w, y1 = vp.y + vp.h;
        dl.AddRectFilledMultiColor(new Vector2(x0, y0), new Vector2(x1, y0 + t), edge, edge, clear, clear);
        dl.AddRectFilledMultiColor(new Vector2(x0, y1 - t), new Vector2(x1, y1), clear, clear, edge, edge);
        dl.AddRectFilledMultiColor(new Vector2(x0, y0), new Vector2(x0 + t, y1), edge, clear, clear, edge);
        dl.AddRectFilledMultiColor(new Vector2(x1 - t, y0), new Vector2(x1, y1), clear, edge, edge, clear);

        var v = player.LastHitVelocity;
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        if (len < 1e-3f)
            return;
        // The shot travelled along v, so it came from -v.
        var dir = new Vector2(-v.X / len, -v.Y / len);
        var side = new Vector2(-dir.Y, dir.X);
        var center = new Vector2(vp.x + vp.w * 0.5f, vp.y + vp.h * 0.5f);
        float r = MathF.Min(vp.w, vp.h) * 0.22f;
        var tip = center + dir * (r + 26f);
        var b1 = center + dir * r + side * 22f;
        var b2 = center + dir * r - side * 22f;
        uint wedge = (uint)(k * 230f) << 24 | 0x3040FFu;
        dl.AddTriangleFilled(tip, b1, b2, wedge);
    }

    /// <summary>
    /// One banner at a time, most urgent first: overheating, then low shield, then low energy. Low warnings latch
    /// on below 20% and clear above 30% so they do not flicker around the threshold.
    /// </summary>
    private void DrawWarnings(Tank player, bool heatDebugOverlayEnabled)
    {
        var vp = ViewportRect;
        if (vp.w <= 0 || player.IsDead)
        {
            _lowEnergyLatched = _shieldLowLatched = false;
            return;
        }

        float energy = player.HeatBudget / Tweaks.Tank.HeatSafeMax;
        _lowEnergyLatched = _lowEnergyLatched ? energy < 0.30f : energy < 0.20f;
        float shield = player.Reactor.Health / (float)Math.Max(1, player.Reactor.HealthCapacity);
        _shieldLowLatched = _shieldLowLatched ? shield < 0.30f : shield < 0.25f;

        // After the first hit, teach the thermal view once: enemy tunnels glow in it.
        if (!_thermalHintShown && player.HitFlashTicks > 0 && !heatDebugOverlayEnabled)
        {
            _thermalHintShown = true;
            _thermalHintUntil = _time + 8f;
        }

        var dl = ImGui.GetForegroundDrawList();
        float cx = vp.x + vp.w * 0.5f;
        float y = vp.y + 18f;
        bool blink = (int)(_time * 3f) % 2 == 0;

        if (player.IsOverheating)
        {
            DrawCenteredText(dl, 30f, cx, y, "OVERHEATING - SHIELD BURNING", blink ? 0xFF3030FFu : 0xFF8080FFu);
            DrawCenteredText(dl, 18f, cx, y + 34f, "Stop digging and firing, or return to base to cool down", 0xFFFFFFFFu);
        }
        else if (_shieldLowLatched)
        {
            DrawCenteredText(dl, 26f, cx, y, "SHIELD LOW", blink ? 0xFF3040FFu : 0xFF8080FFu);
            DrawCenteredText(dl, 18f, cx, y + 30f, "Your base repairs the shield", 0xFFFFFFFFu);
        }
        else if (_lowEnergyLatched)
        {
            DrawCenteredText(dl, 26f, cx, y, "LOW ENERGY", blink ? 0xFF30C8FFu : 0xFF80E0FFu);
            DrawCenteredText(dl, 18f, cx, y + 30f, "Digging and firing heat the tank - return to base to cool down", 0xFFFFFFFFu);
        }
        else if (player.SpawnProtectionTicks > 0)
        {
            DrawCenteredText(dl, 20f, cx, y, "Respawn shield active", 0xFFFFE65Au);
        }
        else if (_time < _thermalHintUntil)
        {
            DrawCenteredText(dl, 20f, cx, y, "Press H for thermal view - enemy tunnels glow hot", 0xFF80E0FFu);
        }
    }

    /// <summary>Death screen: what destroyed the tank and when it respawns; victory and defeat when lives run out.</summary>
    private void DrawDeathOverlay(Tank player, World world)
    {
        var vp = ViewportRect;
        if (vp.w <= 0)
            return;

        bool enemyOut = false;
        foreach (var tank in world.TankList.Tanks)
        {
            if (tank != player && tank.IsDead && tank.LivesLeft <= 0)
                enemyOut = true;
        }

        var dl = ImGui.GetForegroundDrawList();
        float cx = vp.x + vp.w * 0.5f;
        float cy = vp.y + vp.h * 0.24f;

        if (enemyOut && !player.IsDead)
        {
            DrawCenteredText(dl, 44f, cx, cy - 30f, "VICTORY", 0xFF40FF80u);
            DrawCenteredText(dl, 20f, cx, cy + 20f, "The enemy tank is out of lives", 0xFFFFFFFFu);
            DrawCenteredText(dl, 20f, cx, cy + 50f, "Press Enter to play again", 0xFFC0E0FFu);
            return;
        }

        if (!player.IsDead)
            return;

        dl.AddRectFilled(new Vector2(vp.x, vp.y), new Vector2(vp.x + vp.w, vp.y + vp.h), 0x70000000u);
        string cause = player.DeathCause switch
        {
            DamageCause.EnemyFire => "Destroyed by enemy fire",
            DamageCause.Overheat => "Destroyed by overheating",
            _ => "Destroyed",
        };

        if (player.LivesLeft <= 0)
        {
            DrawCenteredText(dl, 44f, cx, cy - 30f, "DEFEAT", 0xFF4040FFu);
            DrawCenteredText(dl, 20f, cx, cy + 20f, cause + " - no lives left", 0xFFFFFFFFu);
            DrawCenteredText(dl, 20f, cx, cy + 50f, "Press Enter to play again", 0xFFC0E0FFu);
            return;
        }

        DrawCenteredText(dl, 44f, cx, cy - 30f, "TANK DESTROYED", 0xFF4060FFu);
        DrawCenteredText(dl, 20f, cx, cy + 20f, cause, 0xFFFFFFFFu);
        string lives = player.LivesLeft == 1 ? "1 life left" : $"{player.LivesLeft} lives left";
        DrawCenteredText(dl, 20f, cx, cy + 48f,
            $"Respawning at base in {player.RespawnRemaining.TotalSeconds:0.0} s  -  {lives}", 0xFFC0E0FFu);
    }

    private void DrawNumberCentered(ImDrawListPtr dl, int value, float areaX, float areaY, float areaW)
    {
        string text = value.ToString();
        float totalW = text.Length > 0
            ? text.Length * DigitCellW + (text.Length - 1) * DigitSpacing
            : 0f;
        float x = MathF.Floor(areaX + (areaW - totalW) * 0.5f);
        float y = MathF.Floor(areaY);

        foreach (char c in text)
        {
            if (c is >= '0' and <= '9')
            {
                int idx = c - '0';
                // Half-texel inset prevents sampling bleed from neighbor digits.
                float texelU = 0.5f / DigitStripW;
                float u0 = (float)(idx * DigitCellW) / DigitStripW + texelU;
                float u1 = (float)((idx + 1) * DigitCellW) / DigitStripW - texelU;
                dl.AddImage(_digitStripTex,
                    new Vector2(x, y),
                    new Vector2(x + DigitCellW, y + DigitCellH),
                    new Vector2(u0, 0f),
                    new Vector2(u1, 1f));
            }
            x += DigitCellW + DigitSpacing;
        }
    }

    public void DrawTerrainTest(
        nint gameTextureId,
        int texW,
        int texH,
        bool heatDebugOverlayEnabled,
        PostProcessPassFlags passFlags,
        Action<string?> requestScreenshot,
        Action<GameCommand> executeCommand)
    {
        DrawGameViewportFullscreen(gameTextureId, texW, texH);
        DrawTerrainTestPanel(heatDebugOverlayEnabled, passFlags, requestScreenshot, executeCommand);
    }

    private void DrawGameViewportFullscreen(nint gameTextureId, int texW, int texH)
    {
        var displaySize = ImGui.GetIO().DisplaySize;

        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(displaySize.X, displaySize.Y), ImGuiCond.Always);

        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                    ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
                    ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoBackground;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);

        if (ImGui.Begin("##Viewport", flags))
        {
            var cursorPos = ImGui.GetCursorScreenPos();
            ImGui.SetCursorPos(Vector2.Zero);
            ImGui.Image(gameTextureId, new Vector2(texW, texH));
            ViewportRect = (cursorPos.X, cursorPos.Y, texW, texH);
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private void DrawTerrainTestPanel(
        bool heatDebugOverlayEnabled,
        PostProcessPassFlags passFlags,
        Action<string?> requestScreenshot,
        Action<GameCommand> executeCommand)
    {
        var vp = ViewportRect;
        if (vp.w <= 0 || vp.h <= 0)
            return;

        ImGui.SetNextWindowPos(new Vector2(vp.x + 10f, vp.y + 10f), ImGuiCond.Once);
        ImGui.SetNextWindowBgAlpha(0.78f);
        ImGui.SetNextWindowSize(new Vector2(280f, 0f), ImGuiCond.Once);

        if (ImGui.Begin("Terrain Test", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.TextUnformatted("Shader Passes");
            ImGui.Separator();

            DrawToggleCheckbox("Native Smoothing", passFlags, PostProcessPassFlags.NativeTerrainSmoothing,
                GameCommand.TogglePostNativeTerrainSmoothing, executeCommand);
            DrawToggleCheckbox("Edge Curving", passFlags, PostProcessPassFlags.NativeEdgeCurving,
                GameCommand.TogglePostNativeEdgeCurving, executeCommand);
            DrawToggleCheckbox("Terrain Curve (Post)", passFlags, PostProcessPassFlags.TerrainCurve,
                GameCommand.TogglePostTerrainCurve, executeCommand);
            DrawToggleCheckbox("Material Textures", passFlags, PostProcessPassFlags.TerrainAux,
                GameCommand.TogglePostTerrainAux, executeCommand);
            DrawToggleCheckbox("Vignette", passFlags, PostProcessPassFlags.Vignette,
                GameCommand.TogglePostVignette, executeCommand);
            DrawToggleCheckbox("Terrain Heat", passFlags, PostProcessPassFlags.TerrainHeat,
                GameCommand.TogglePostTerrainHeat, executeCommand);
            DrawToggleCheckbox("Tank Glow", passFlags, PostProcessPassFlags.TankGlow,
                GameCommand.TogglePostTankGlow, executeCommand);

            ImGui.Separator();
            bool heatDebug = heatDebugOverlayEnabled;
            if (ImGui.Checkbox("Heat Debug Colormap", ref heatDebug))
                executeCommand(GameCommand.ToggleHeatOverlay);

            ImGui.Separator();
            if (ImGui.Button("Screenshot (F10)"))
                requestScreenshot("terrain-test");

            ImGui.Separator();
            ImGui.TextDisabled("WASD to pan camera");
            ImGui.TextDisabled("1-7 hotkeys toggle passes");
        }
        ImGui.End();
    }

    private static void DrawToggleCheckbox(
        string label,
        PostProcessPassFlags currentFlags,
        PostProcessPassFlags targetFlag,
        GameCommand toggleCommand,
        Action<GameCommand> executeCommand)
    {
        bool enabled = (currentFlags & targetFlag) != 0;
        if (ImGui.Checkbox(label, ref enabled))
            executeCommand(toggleCommand);
    }

    /// <summary>
    /// Top-right readout: lives, the relay network (how many relays are linked, how full the base reservoir is)
    /// and a context hint for the build key that explains what E will do, or why a carried relay will not link.
    /// </summary>
    private double _radiatorInflow;

    private void DrawHeatNetworkOverlay(Tank player, World world)
    {
        var vp = ViewportRect;
        var tankBase = player.Base;
        if (vp.w <= 0 || vp.h <= 0 || tankBase == null)
            return;

        int relays = 0, powered = 0, pumping = 0, harvesters = 0, stalled = 0;
        double inflow = 0.0;
        foreach (var machine in world.Machines.Machines)
        {
            if (machine.IsAlive && machine.Type == MachineType.Harvester && machine.State == MachineState.Planted &&
                machine.OwnerBase == tankBase)
            {
                harvesters++;
                if (machine.IsStalled)
                    stalled++;
            }

            if (!machine.IsAlive || machine.Type != MachineType.Relay || machine.State != MachineState.Planted ||
                machine.OwnerBase != tankBase)
                continue;
            relays++;
            if (machine.LinkPoint?.IsPowered == true)
                powered++;
            if (machine.LastPumpedEnergy > 1.0)
                pumping++;
            inflow += machine.LastPumpedEnergy;
        }
        // Smooth the readout: relays act every tick but the number is read by eye.
        _radiatorInflow += (inflow - _radiatorInflow) * 0.1;
        double ventCapacity = Tweaks.Base.HeatCooldown * (double)Tweaks.Base.ReactorEnergyPerHeatUnit;
        _dockedAtOwnBase = !player.IsDead && world.TankBases.CheckBaseCollision(player.Position) == tankBase;

        // Exact stored energy (including the sub-unit remainder), so a pumping relay shows progress at once.
        double capacityEnergy = Math.Max(1, (int)tankBase.Reactor.HeatCapacity) * (double)Tweaks.Base.ReactorEnergyPerHeatUnit;
        float fill = (float)Math.Clamp(tankBase.ReactorHeatEnergy / capacityEnergy, 0.0, 1.0);
        var fillColor = fill < 0.6f ? TextGood : fill < 0.9f ? TextWarn : TextBad;

        if (!player.IsDead && world.TankBases.CheckBaseCollision(player.Position) == null)
            _hasLeftBase = true;
        if (powered > _lastPoweredRelays)
            _relayLinkedUntil = _time + 4f;
        _lastPoweredRelays = powered;

        int dirt = AvailableRelayDirt(player, _dockedAtOwnBase);
        bool fromBaseStore = _dockedAtOwnBase && player.Resources.Current.Dirt < Tweaks.Machine.RelayDirtCost &&
                             dirt >= Tweaks.Machine.RelayDirtCost;
        string hint;
        Vector4 hintColor;
        if (player.IsDead)
        {
            hint = string.Empty;
            hintColor = TextDim;
        }
        else if (!_hasLeftBase && world.TankBases.CheckBaseCollision(player.Position) == tankBase)
        {
            hint = "WASD to drive - the base exit is at the bottom";
            hintColor = TextInfo;
        }
        else if (_time < _relayLinkedUntil && !player.IsCarryingMachine)
        {
            hint = pumping > 0
                ? "Relay linked - pumping heat into your base"
                : "Relay linked - idle until the ground near it heats up";
            hintColor = TextGood;
        }
        else if (player.PlantRefusedTicks > 0)
        {
            hint = "Relay not planted: it would not link - find a clear line";
            hintColor = TextBad;
        }
        else if (player.IsCarryingMachine)
        {
            bool carryingHarvester = player.CarriedMachineType == MachineType.Harvester;
            if (world.TankBases.CheckBaseCollision(player.Position) != null)
            {
                hint = carryingHarvester ? "Carrying harvester - leave the base to plant it" : "Carrying relay - leave the base to plant it";
                hintColor = TextInfo;
            }
            else if (carryingHarvester)
            {
                hint = "R: plant harvester here - it digs nearby dirt and heats the ground";
                hintColor = TextGood;
            }
            else
            {
                var preview = world.LinkMap.PreviewLink(player.Position, world.Terrain);
                (hint, hintColor) = preview.Status switch
                {
                    LinkPreviewStatus.Live => ("E: plant relay here - it will link (green line)", TextGood),
                    LinkPreviewStatus.Blocked => ("Line to network blocked (red X) - dig it clear", TextBad),
                    LinkPreviewStatus.TooFar => ("Too far from the network - move closer", TextWarn),
                    _ => ("No powered network to link to", TextWarn),
                };
            }
        }
        else if (fill >= 0.9f && powered > 0)
        {
            hint = "Base heat store full - relays and docking cool less";
            hintColor = TextWarn;
        }
        else if (dirt >= Tweaks.Machine.RelayDirtCost)
        {
            hint = fromBaseStore
                ? $"E: build relay from the base store ({Tweaks.Machine.RelayDirtCost} dirt)"
                : $"E: build relay ({Tweaks.Machine.RelayDirtCost} dirt)";
            if (dirt >= Tweaks.Machine.HarvesterDirtCost)
                hint += $"   R: build harvester ({Tweaks.Machine.HarvesterDirtCost} dirt)";
            hintColor = TextGood;
        }
        else if (_dockedAtOwnBase && _hasLeftBase && player.HeatBudget < Tank.HeatBudgetCapacity - 1f)
        {
            hint = "Docked: cooling down and storing your dirt";
            hintColor = TextInfo;
        }
        else
        {
            int stored = tankBase.Materials.Current.Dirt;
            hint = stored >= Tweaks.Machine.RelayDirtCost
                ? $"Dig {Tweaks.Machine.RelayDirtCost - dirt} more dirt, or build a relay at base from the store"
                : $"Dig {Tweaks.Machine.RelayDirtCost - dirt} more dirt to build a relay";
            hintColor = TextDim;
        }

        ImGui.SetNextWindowPos(new Vector2(vp.x + vp.w - 10f, vp.y + 10f), ImGuiCond.Always, new Vector2(1f, 0f));
        ImGui.SetNextWindowBgAlpha(0.55f);
        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoInputs |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("##HeatNetworkOverlay", flags))
        {
            ImGui.SetWindowFontScale(1.25f);
            ColoredText(player.LivesLeft > 1 ? TextDim : TextBad, $"Lives {player.LivesLeft}");
            ColoredText(TextDim, $"Base dirt store {tankBase.Materials.Current.Dirt}");
            if (harvesters > 0)
                ColoredText(stalled > 0 ? TextWarn : TextDim,
                    $"Harvesters working {harvesters - stalled}/{harvesters}" + (stalled > 0 ? $", {stalled} too hot" : ""));
            if (relays > 0)
            {
                ColoredText(powered == relays ? TextDim : TextWarn,
                    $"Relays linked {powered}/{relays}" + (pumping > 0 ? $", {pumping} pumping" : ""));
                ColoredText(fillColor, fill > 0f && fill < 0.01f
                    ? "Base heat store <1% full"
                    : $"Base heat store {(int)MathF.Round(fill * 100f)}% full");

                // Radiator load: heat arriving through relays against the rate the base can vent. Past 100% the
                // store fills, and once it is full the relays are throttled.
                float load = (float)(_radiatorInflow / ventCapacity);
                var loadColor = load < 0.8f ? TextGood : load <= 1.0f ? TextWarn : TextBad;
                ColoredText(loadColor,
                    $"Radiator {(int)Math.Round(_radiatorInflow)} in / {(int)ventCapacity} out per tick");
                ImGui.ProgressBar(Math.Clamp(load / 1.5f, 0f, 1f), new Vector2(180f, 8f), string.Empty);
            }
            if (hint.Length > 0)
            {
                ImGui.Separator();
                ColoredText(hintColor, hint);
            }
            ColoredText(TextDim, "H: heat view");
            ImGui.SetWindowFontScale(1.0f);
        }
        ImGui.End();
    }

    /// <summary>Colored text that is never treated as a format string (so a literal '%' is shown).</summary>
    private static void ColoredText(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    private void DrawPostPassOverlay(bool heatDebugOverlayEnabled, PostProcessPassFlags passFlags)
    {
        var vp = ViewportRect;
        if (vp.w <= 0 || vp.h <= 0)
            return;

        ImGui.SetNextWindowPos(new Vector2(vp.x + 10f, vp.y + 10f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.60f);
        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoInputs |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("##PostPassOverlay", flags))
        {
            ImGui.SetWindowFontScale(1.25f);
            ImGui.TextUnformatted("Post Passes");
            ImGui.Separator();
            DrawPassStatusLine(passFlags, PostProcessPassFlags.Vignette, "1 Vignette");
            DrawPassStatusLine(passFlags, PostProcessPassFlags.TerrainCurve, "2 TerrainCurve");
            DrawPassStatusLine(passFlags, PostProcessPassFlags.TerrainAux, "3 TerrainTex");
            DrawPassStatusLine(passFlags, PostProcessPassFlags.TankGlow, "4 TankGlow");
            DrawPassStatusLine(passFlags, PostProcessPassFlags.NativeTerrainSmoothing, "5 NativeSmoothing");
            DrawPassStatusLine(passFlags, PostProcessPassFlags.TerrainHeat, "6 TerrainHeat");
            DrawPassStatusLine(passFlags, PostProcessPassFlags.NativeEdgeCurving, "7 NativeEdgeCurving");
            DrawToggleStatusLine(heatDebugOverlayEnabled, "8 HeatColormap (Shift+1)");
            DrawPassStatusLine(passFlags, PostProcessPassFlags.ThermalRegions, "9 ThermalRegions (Shift+2)");
            ImGui.SetWindowFontScale(1.0f);
        }
        ImGui.End();
    }

    private static void DrawPassStatusLine(PostProcessPassFlags passFlags, PostProcessPassFlags pass, string label)
    {
        bool enabled = (passFlags & pass) != 0;
        DrawToggleStatusLine(enabled, label);
    }

    private static void DrawToggleStatusLine(bool enabled, string label)
    {
        Vector4 color = enabled
            ? new Vector4(0.36f, 0.96f, 0.49f, 1.0f)
            : new Vector4(0.92f, 0.33f, 0.33f, 1.0f);
        ColoredText(color, $"{label}: {(enabled ? "ON" : "OFF")}");
    }

    private static uint ToAbgr(float r, float g, float b, float a)
    {
        byte rb = (byte)(MathF.Min(r, 1f) * 255 + 0.5f);
        byte gb = (byte)(MathF.Min(g, 1f) * 255 + 0.5f);
        byte bb = (byte)(MathF.Min(b, 1f) * 255 + 0.5f);
        byte ab = (byte)(MathF.Min(a, 1f) * 255 + 0.5f);
        return (uint)(ab << 24 | bb << 16 | gb << 8 | rb);
    }
}
