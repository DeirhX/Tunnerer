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
    /// <summary>Height of the HUD art in its own pixels; on screen it is multiplied by the HUD scale.</summary>
    private const float StripH = 223f;

    /// <summary>The display height at which the HUD art is drawn 1:1; taller displays enlarge it.</summary>
    private const float ReferenceDisplayHeight = 1080f;

    private nint _heatIconTex, _shieldIconTex;
    private nint _panelFrameTex, _buildPanelTex;
    private nint _digitStripTex;

    private const int DigitCount = 10;
    private const int DigitCellW = 17;
    private const int DigitCellH = 20;
    private const int DigitStripW = DigitCellW * DigitCount; // 170
    private const float DigitSpacing = 1f;

    private float _time;
    private bool _runningHotLatched;
    private bool _shieldLowLatched;
    private bool _hasLeftBase;
    private float _thermalHintUntil = -1f;
    private bool _thermalHintShown;
    private bool _dockedAtOwnBase;
    private int _lastPoweredRelays;
    private float _relayLinkedUntil = -1f;
    private float _lastHeat = float.NaN;
    private float _heatTrend;
    private float _shownHeat = float.NaN;
    private float _shownShieldFill = -1f;
    private float _shownMaterialsFill = -1f;

    // Gauge shapes inside the HUD art (texture pixels = ImGui units from each panel's top-left corner).
    private static readonly Vector2 HeatOrbCenter = new(168f, 72f);
    private const float HeatOrbRadius = 47f;

    // Temperature scale of the heat orb and bar, in tank heat units (0 = ambient): from a full coolant charge at
    // the cold end, through the safe range, to a margin past the point where overheating starts burning the shield.
    private static float HeatScaleMin => -Tweaks.Tank.CoolantCapacity;
    private static float HeatScaleMax => Tweaks.Tank.HeatSafeMax * 1.25f;
    private const float HeatDialStartAngle = MathF.PI * 150f / 180f; // lower left, sweeping clockwise over the top
    private const float HeatDialSweep = MathF.PI * 240f / 180f;
    private static readonly Vector2[] ShieldOutline =
    [
        new(44f, 44f), new(60f, 40f), new(82f, 37f), new(104f, 40f), new(121f, 44f), new(121f, 80f),
        new(114f, 100f), new(100f, 116f), new(82f, 128f), new(64f, 116f), new(50f, 100f), new(44f, 80f),
    ];

    // The open top of the materials chest, where its contents show.
    private static readonly Vector2[] ChestOutline =
    [
        new(52f, 40f), new(193f, 40f), new(206f, 52f), new(206f, 86f),
        new(185f, 98f), new(60f, 98f), new(40f, 86f), new(40f, 52f),
    ];

    // Layout of the bottom strip (ImGui units): the four art panels drawn side by side.
    private const float HeatPanelW = 245f, ShieldPanelW = 145f, MaterialsPanelW = 230f, BuildPanelW = 404f;
    private const float StripW = HeatPanelW +ShieldPanelW + MaterialsPanelW + BuildPanelW; // 1024

    // Build slots inside build_panel.png (offsets from the panel's top-left), 3 columns x 2 rows.
    // The strip is extended to both screen edges by repeating, mirrored, the chassis at its outer ends: the left
    // end of heat_icon.png and the right end of build_panel.png, from ExtensionTopY down. Mirroring every other
    // copy makes the joins seamless.
    private const float ExtensionTileW = 60f;
    private const float ExtensionTopY = 100f;

    // Where the bottom strip is on screen this frame: art pixel (x, y) is drawn at _hudOrigin + (x, y) * _hudScale.
    private Vector2 _hudOrigin;
    private float _hudScale = 1f;

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

    /// <summary>
    /// How much the HUD art is enlarged: 1 up to a 1080-pixel-high display, in proportion above it (2 at 4K), and
    /// shrunk only when the display is narrower than the art.
    /// </summary>
    public static float HudScale(float displayW, float displayH) =>
        MathF.Min(displayW / StripW, MathF.Max(1f, displayH / ReferenceDisplayHeight));

    /// <summary>Screen pixels at the bottom of the window covered by the HUD; the game view is drawn above them.</summary>
    public static int BottomPanelHeight(int windowW, int windowH) =>
        (int)MathF.Ceiling(StripH * HudScale(windowW, windowH));

    /// <summary>Art pixel to screen position, for the HUD drawn at the current scale.</summary>
    private Vector2 At(float x, float y) => _hudOrigin + new Vector2(x, y) * _hudScale;
    private Vector2 At(Vector2 p) => _hudOrigin + p * _hudScale;
    private float S(float v) => v * _hudScale;

    /// <summary>State of the temperature orb shader for the frame just drawn; the game passes it to the backend.</summary>
    public HeatOrbState HeatOrb { get; private set; }

    public void Init(nint heatIcon, nint shieldIcon, nint panelFrame, nint buildPanel, nint digitStrip)
    {
        _heatIconTex = heatIcon;
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

        _hudScale = HudScale(displaySize.X, displaySize.Y);
        float stripScreenW = StripW * _hudScale;
        _hudOrigin = new Vector2(
            MathF.Floor((displaySize.X - stripScreenW) * 0.5f),
            displaySize.Y - BottomPanelHeight((int)displaySize.X, (int)displaySize.Y));

        DrawStripExtensions(dl, displaySize.X);

        const float h = StripH;
        const float heatX = 0f;
        const float shieldX = heatX + HeatPanelW;
        const float materialsX = shieldX + ShieldPanelW;
        const float buildX = materialsX + MaterialsPanelW;
        dl.AddImage(_heatIconTex, At(heatX, 0f), At(heatX + HeatPanelW, h));
        dl.AddImage(_shieldIconTex, At(shieldX, 0f), At(shieldX + ShieldPanelW, h));
        dl.AddImage(_panelFrameTex, At(materialsX, 0f), At(materialsX + MaterialsPanelW, h));
        dl.AddImage(_buildPanelTex, At(buildX, 0f), At(buildX + BuildPanelW, h));

        DrawHeatOrb(dl, player, At(HeatOrbCenter), S(HeatOrbRadius));
        DrawLiquidGauges(dl, player, shieldX, materialsX);

        // TEMP: the tank's temperature in degrees above ambient. Digging and firing heat it; past the safe maximum
        // the heat burns the shield. A tank docked at its own base is pre-cooled below ambient (negative degrees).
        const float plateX = heatX + 105f;
        const float plateW = 130f;
        const float plateY = 168f;
        int degrees = player.IsDead ? 0 : (int)MathF.Round(player.Heat);
        DrawNumberCentered(dl, degrees, plateX, plateY, plateW, degreeSign: true);
        DrawHeatTrend(dl, player, plateX + plateW - 14f, plateY + 10f);
        if (!player.IsDead)
            DrawHeatBar(dl, player.Heat, plateX + 8f, 192f, plateW - 16f);
        DrawOffNetworkTag(dl, player, heatX + HeatPanelW * 0.5f);

        // SHIELD: the tank's armor.
        int health = Math.Max(0, player.Reactor.Health);
        const float sPlateX = shieldX + 10f;
        const float sPlateW = 125f;
        DrawNumberCentered(dl, health, sPlateX, 166f, sPlateW);
        float shieldFill = player.Reactor.HealthCapacity > 0 ? health / (float)player.Reactor.HealthCapacity : 0f;
        DrawBar(dl, sPlateX + 8f, 191f, sPlateW - 16f, 5f, shieldFill,
            shieldFill > 0.5f ? 0xFFFFC040u : shieldFill > 0.25f ? 0xFF30C0FFu : 0xFF3040FFu);

        // MATERIALS: the dirt in the tank's hold, which is what structures are built from; the bar is how full it is.
        float materialsFill = MaterialsFill(player);
        const float mPlateX = materialsX + 15f;
        const float mPlateW = MaterialsPanelW - 30f;
        DrawNumberCentered(dl, player.Resources.Dirt, mPlateX, 166f, mPlateW);
        DrawBar(dl, mPlateX + 40f, 191f, mPlateW - 80f, 5f, materialsFill, 0xFF40B8F0u);

        DrawBuildSlots(dl, player, player.BuildableDirt(_dockedAtOwnBase), buildX);

        // The COMMAND button has no function yet; dim it like the unavailable build slots.
        dl.AddRectFilled(At(buildX + 77f, 172f), At(buildX + 277f, 212f), 0x90000000u, S(10f));
    }

    /// <summary>
    /// Fills the screen width on both sides of the strip with mirrored copies of the chassis at its outer ends, so
    /// the HUD runs from edge to edge at any aspect ratio.
    /// </summary>
    private void DrawStripExtensions(ImDrawListPtr dl, float displayW)
    {
        float tileW = S(ExtensionTileW);
        float top = At(0f, ExtensionTopY).Y;
        float bottom = At(0f, StripH).Y;
        float vTop = ExtensionTopY / StripH;

        // Left: the first ExtensionTileW columns of the heat panel, the copy touching the strip mirrored.
        float uHeat = ExtensionTileW / HeatPanelW;
        float x = _hudOrigin.X;
        for (int i = 0; x > 0f; i++, x -= tileW)
        {
            bool mirrored = i % 2 == 0;
            dl.AddImage(_heatIconTex, new Vector2(x - tileW, top), new Vector2(x, bottom),
                new Vector2(mirrored ? uHeat : 0f, vTop), new Vector2(mirrored ? 0f : uHeat, 1f));
        }

        // Right: the last columns of the build panel.
        float uBuild = 1f - ExtensionTileW / BuildPanelW;
        x = _hudOrigin.X + S(StripW);
        for (int i = 0; x < displayW; i++, x += tileW)
        {
            bool mirrored = i % 2 == 0;
            dl.AddImage(_buildPanelTex, new Vector2(x, top), new Vector2(x + tileW, bottom),
                new Vector2(mirrored ? 1f : uBuild, vTop), new Vector2(mirrored ? uBuild : 1f, 1f));
        }
    }

    /// <summary>
    /// Small arrow beside the temperature: up (red) while digging or firing heats the tank, down (blue) while it
    /// cools. The rate is smoothed so the arrow does not flicker.
    /// </summary>
    private void DrawHeatTrend(ImDrawListPtr dl, Tank player, float x, float y)
    {
        float heat = player.Heat;
        if (!float.IsNaN(_lastHeat) && !player.IsDead)
            _heatTrend = _heatTrend * 0.9f + (heat - _lastHeat) * 0.1f;
        _lastHeat = heat;
        if (player.IsDead || MathF.Abs(_heatTrend) < 0.02f)
            return;

        const float s = 6f;
        if (_heatTrend > 0f)
            dl.AddTriangleFilled(At(x - s, y + s * 0.6f), At(x + s, y + s * 0.6f), At(x, y - s * 0.8f), 0xFF4050FFu);
        else
            dl.AddTriangleFilled(At(x - s, y - s * 0.6f), At(x + s, y - s * 0.6f), At(x, y + s * 0.8f), 0xFFFFC050u);
    }

    /// <summary>
    /// Above the TEMP panel while the tank is outside its network's coverage (DESIGN.md §4.6): how long until its
    /// rising heat floor reaches the safe maximum. Digging spikes show on the gauge itself and shed back down.
    /// </summary>
    private void DrawOffNetworkTag(ImDrawListPtr dl, Tank player, float centerX)
    {
        if (player.IsDead || !player.IsOffNetwork)
            return;

        float seconds = MathF.Max(0f, (Tweaks.Tank.HeatSafeMax - player.OffNetworkFloor) / Tweaks.Tank.OffNetworkHeatPerSecond);
        string text = seconds > 0f ? $"OFF NETWORK  ~{(int)MathF.Ceiling(seconds)}s" : "OFF NETWORK  OVERHEATING";
        bool urgent = seconds < 20f;
        uint color = urgent
            ? ((int)(_time * 4f) % 2 == 0 ? 0xFF3040FFu : 0xFF60A0FFu)
            : 0xFF40C8FFu;
        float size = S(20f);
        float width = ImGui.GetFont().CalcTextSizeA(size, float.MaxValue, 0f, text).X;
        var pos = At(centerX, -30f);
        DrawOutlinedText(dl, size, new Vector2(MathF.Floor(pos.X - width * 0.5f), pos.Y), text, color);
    }

    /// <summary>Where a temperature lies on the heat scale, 0 (full coolant charge) to 1 (well past overheating).</summary>
    private static float HeatScaleFraction(float heat) =>
        Math.Clamp((heat - HeatScaleMin) / (HeatScaleMax - HeatScaleMin), 0f, 1f);

    /// <summary>
    /// Colour for a temperature: icy blue with coolant, green around ambient, then amber, orange and red as the
    /// tank approaches and passes the safe maximum.
    /// </summary>
    private static uint HeatColor(float heat) => PackColor(HeatColorRgb(heat), 1f);

    private static Vector3 HeatColorRgb(float heat)
    {
        float safe = Tweaks.Tank.HeatSafeMax;
        ReadOnlySpan<float> at = [HeatScaleMin, 0f, 0.55f * safe, 0.85f * safe, safe];
        ReadOnlySpan<Vector3> rgb =
        [
            new(0.35f, 0.75f, 1.00f), new(0.40f, 0.90f, 0.40f), new(1.00f, 0.80f, 0.20f),
            new(1.00f, 0.45f, 0.10f), new(1.00f, 0.15f, 0.10f),
        ];
        Vector3 c = rgb[^1];
        if (heat <= at[0])
            c = rgb[0];
        else
        {
            for (int i = 1; i < at.Length; i++)
            {
                if (heat <= at[i])
                {
                    c = Vector3.Lerp(rgb[i - 1], rgb[i], (heat - at[i - 1]) / (at[i] - at[i - 1]));
                    break;
                }
            }
        }
        return c;
    }

    private static uint PackColor(Vector3 rgb, float alpha) =>
        (uint)(Math.Clamp(alpha, 0f, 1f) * 255f) << 24 |
        (uint)(Math.Clamp(rgb.Z, 0f, 1f) * 255f) << 16 |
        (uint)(Math.Clamp(rgb.Y, 0f, 1f) * 255f) << 8 |
        (uint)(Math.Clamp(rgb.X, 0f, 1f) * 255f);

    private static void StrokeArc(ImDrawListPtr dl, Vector2 c, float r, float a0, float a1, uint color, float thickness)
    {
        if (a1 <= a0)
            return;
        int segments = Math.Max(2, (int)((a1 - a0) / (MathF.PI * 2f) * 64f));
        dl.PathArcTo(c, r, a0, a1, segments);
        dl.PathStroke(color, ImDrawFlags.None, thickness);
    }

    private static float HeatDialAngle(float heat) => HeatDialStartAngle + HeatDialSweep * HeatScaleFraction(heat);

    /// <summary>
    /// The temperature orb. A dial runs round the rim, coloured from cold (blue, the coolant charge) over green and
    /// amber to red past the safe maximum, lit up to the current temperature with a needle at its tip. Inside, a
    /// glass sphere always full of liquid is drawn by a shader (HeatOrbPs, see <see cref="HeatOrbEffect"/>): the
    /// liquid takes the temperature's colour and its state follows it, frozen to ice when pre-cooled, calm around
    /// ambient, bubbling as it heats and boiling once overheating burns the shield.
    /// </summary>
    private void DrawHeatOrb(ImDrawListPtr dl, Tank player, Vector2 c, float r)
    {
        float k = _hudScale;
        float heat = player.IsDead ? HeatScaleMin : player.Heat;
        // Ease the shown temperature toward the real one so the liquid and needle move rather than jump.
        _shownHeat = float.IsNaN(_shownHeat) ? heat : _shownHeat + (heat - _shownHeat) * 0.15f;
        float shown = _shownHeat;
        float safe = Tweaks.Tank.HeatSafeMax;
        bool alive = !player.IsDead;
        bool burning = alive && (player.IsOverheating || heat > safe);
        float pulse = 0.5f + 0.5f * MathF.Sin(_time * 10f);
        float lit = HeatScaleFraction(shown);

        // Dark glass behind everything (hides the energy bolt baked into the art), then the liquid sphere.
        dl.AddCircleFilled(c, r - k, 0xFF0A0C0Eu, 64);
        float inner = r - 9f * k;
        HeatOrb = new HeatOrbState(
            LiquidColor: alive ? HeatColorRgb(shown) : new Vector3(0.12f, 0.13f, 0.14f),
            Frozen: alive ? Math.Clamp(-shown / (0.6f * Tweaks.Tank.CoolantCapacity), 0f, 1f) : 0f,
            Boil: alive ? Math.Clamp((shown - 0.4f * safe) / (0.6f * safe), 0f, 1f) : 0f,
            Burning: burning ? 0.6f + 0.4f * pulse : 0f,
            Time: _time);
        dl.AddImage(HeatOrbEffect.TextureId, c - new Vector2(inner), c + new Vector2(inner));

        // Dial round the rim: dim track in the zone colours, lit up to the shown temperature.
        float arcR = r - 6f * k;
        float arcW = 5f * k;
        const int bands = 48;
        for (int i = 0; i < bands; i++)
        {
            float f0 = i / (float)bands, f1 = (i + 1) / (float)bands;
            float mid = HeatScaleMin + (HeatScaleMax - HeatScaleMin) * (f0 + f1) * 0.5f;
            float a0 = HeatDialStartAngle + HeatDialSweep * f0;
            float a1 = HeatDialStartAngle + HeatDialSweep * f1 + 0.01f;
            bool on = alive && f0 < lit;
            if (on && f1 > lit)
                a1 = HeatDialStartAngle + HeatDialSweep * lit;
            StrokeArc(dl, c, arcR, a0, a1, (HeatColor(mid) & 0x00FFFFFFu) | (on ? 0xFF000000u : 0x48000000u), arcW);
        }
        if (burning)
            StrokeArc(dl, c, arcR, HeatDialAngle(safe), HeatDialStartAngle + HeatDialSweep,
                PackColor(new Vector3(1f, 0.3f, 0.2f), 0.35f + 0.5f * pulse), arcW + 3f * k);

        // Ticks at ambient (0) and at the safe maximum, where overheating starts.
        foreach (float t in stackalloc float[] { 0f, safe })
        {
            float a = HeatDialAngle(t);
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            uint tickCol = t == 0f ? 0xC0FFFFFFu : 0xFF4060FFu;
            dl.AddLine(c + dir * (arcR - arcW * 0.5f - 3f * k), c + dir * (arcR + arcW * 0.5f + k), 0xFF000000u, 4f * k);
            dl.AddLine(c + dir * (arcR - arcW * 0.5f - 2f * k), c + dir * (arcR + arcW * 0.5f), tickCol, 2f * k);
        }

        // Needle at the tip of the lit arc.
        if (alive)
        {
            float a = HeatDialAngle(shown);
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            var tip = c + dir * (arcR + arcW * 0.5f + k);
            var tail = c + dir * (arcR - arcW * 0.5f - 5f * k);
            dl.AddLine(tail, tip, 0xFF000000u, 4.5f * k);
            dl.AddLine(tail, tip, 0xFFFFFFFFu, 2f * k);
        }

        // Highlight on the glass.
        StrokeArc(dl, c, inner - 3f * k, MathF.PI * 1.15f, MathF.PI * 1.55f, 0x30FFFFFFu, 3f * k);
    }

    /// <summary>
    /// Temperature bar under the readout, on the same scale as the orb: a blue coolant zone left of the ambient
    /// mark, green to amber up to the safe-maximum mark, and red beyond it.
    /// </summary>
    private void DrawHeatBar(ImDrawListPtr dl, float heat, float x, float y, float w)
    {
        const float barH = 5f;
        float X(float t) => x + w * HeatScaleFraction(t);
        dl.AddRectFilled(At(x - 1f, y - 1f), At(x + w + 1f, y + barH + 1f), 0xC0000000u);
        dl.AddRectFilled(At(X(Tweaks.Tank.HeatSafeMax), y), At(x + w, y + barH), 0x503040FFu); // danger zone
        uint col = HeatColor(heat);
        if (heat > Tweaks.Tank.HeatSafeMax && (int)(_time * 4f) % 2 == 0)
            col = 0xFF8080FFu;
        dl.AddRectFilled(At(x, y), At(X(heat), y + barH), col);
        dl.AddLine(At(X(0f), y - 1f), At(X(0f), y + barH + 1f), 0x80FFFFFFu, S(1f));
        dl.AddLine(At(X(Tweaks.Tank.HeatSafeMax), y - 2f), At(X(Tweaks.Tank.HeatSafeMax), y + barH + 2f), 0xFF4060FFu, S(1f));
    }

    /// <summary>
    /// Diablo-style liquid gauges in the art: the shield crystal and the materials chest are full up to their level
    /// and dark above it, with a softly waving bright surface. A low shield pulses red and flashes when hit.
    /// </summary>
    private void DrawLiquidGauges(ImDrawListPtr dl, Tank player, float shieldPanelX, float materialsPanelX)
    {
        float shieldFill = Math.Clamp(Math.Max(0, player.Reactor.Health) / (float)Math.Max(1, player.Reactor.HealthCapacity), 0f, 1f);
        // Ease the shown levels toward the real ones so the liquid moves rather than jumps.
        _shownShieldFill = _shownShieldFill < 0f ? shieldFill : _shownShieldFill + (shieldFill - _shownShieldFill) * 0.20f;

        bool pulse = (int)(_time * 4f) % 2 == 0;

        // Shield crystal.
        var o = new Vector2(shieldPanelX, 0f);
        var poly = new Vector2[ShieldOutline.Length];
        float sTop = float.MaxValue, sBottom = float.MinValue;
        for (int i = 0; i < poly.Length; i++)
        {
            poly[i] = At(o + ShieldOutline[i]);
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

        // Materials chest.
        float materialsFill = MaterialsFill(player);
        _shownMaterialsFill = _shownMaterialsFill < 0f
            ? materialsFill
            : _shownMaterialsFill + (materialsFill - _shownMaterialsFill) * 0.15f;
        var chest = new Vector2[ChestOutline.Length];
        float cTop = float.MaxValue, cBottom = float.MinValue;
        for (int i = 0; i < chest.Length; i++)
        {
            chest[i] = At(new Vector2(materialsPanelX, 0f) + ChestOutline[i]);
            cTop = MathF.Min(cTop, chest[i].Y);
            cBottom = MathF.Max(cBottom, chest[i].Y);
        }
        float cLevel = cBottom - (cBottom - cTop) * _shownMaterialsFill;
        FillPolygonBand(dl, chest, cTop, cLevel, 0xD8080808u);               // empty part
        var (cx0, cx1) = PolygonSpanAtY(chest, cLevel);
        if (cx1 > cx0)
            DrawSurface(dl, cLevel, (cx1 - cx0) * 0.5f, (cx0 + cx1) * 0.5f, materialsFill > 0f ? 0xFF80D8FFu : 0u);
    }

    /// <summary>How full the tank's dirt hold is, 0 to 1.</summary>
    private static float MaterialsFill(Tank player) =>
        Math.Clamp(player.Resources.Dirt / (float)Math.Max(1, player.Resources.DirtCapacity), 0f, 1f);

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
        if (color == 0u || halfWidth < S(3f))
            return;
        const int segments = 12;
        var points = new Vector2[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float u = i / (float)segments;
            float x = centerX - halfWidth + 2f * halfWidth * u;
            points[i] = new Vector2(x, y + MathF.Sin(u * 9f + _time * 5f) * S(1.2f));
        }
        dl.AddPolyline(ref points[0], points.Length, color & 0x00FFFFFFu | 0x50000000u, ImDrawFlags.None, S(4f));
        dl.AddPolyline(ref points[0], points.Length, color, ImDrawFlags.None, S(1.5f));
    }

    private void DrawBar(ImDrawListPtr dl, float x, float y, float w, float h, float fill, uint color)
    {
        fill = Math.Clamp(fill, 0f, 1f);
        dl.AddRectFilled(At(x - 1f, y - 1f), At(x + w + 1f, y + h + 1f), 0xC0000000u);
        dl.AddRectFilled(At(x, y), At(x + w * fill, y + h), color);
    }

    private void DrawBuildSlots(ImDrawListPtr dl, Tank player, int dirt, float panelX)
    {
        // Slot 0 builds a relay (E), slot 1 a harvester (R), slot 2 a pylon (T). While a machine is in hand only its own slot is lit:
        // pressing the key again plants it.
        var carried = player.IsCarryingMachine ? player.CarriedMachineType : null;

        for (int row = 0; row < BuildSlotY.Length; row++)
        {
            for (int col = 0; col < BuildSlotX.Length; col++)
            {
                if (row == 0)
                    continue;
                float x = panelX + BuildSlotX[col], y = BuildSlotY[row];
                dl.AddRectFilled(At(x, y), At(x + BuildSlotW, y + BuildSlotH), 0xA8000000u, S(6f));
            }
        }

        DrawBuildSlot(dl, player, panelX, col: 0, MachineType.Relay, "RELAY", "E", Tweaks.Machine.RelayDirtCost, dirt, carried);
        DrawBuildSlot(dl, player, panelX, col: 1, MachineType.Harvester, "HARVEST", "R", Tweaks.Machine.HarvesterDirtCost, dirt, carried);
        DrawBuildSlot(dl, player, panelX, col: 2, MachineType.Pylon, "PYLON", "T", Tweaks.Machine.PylonDirtCost, dirt, carried);
    }

    private void DrawBuildSlot(ImDrawListPtr dl, Tank player, float panelX, int col, MachineType type,
        string name, string key, int cost, int dirt, MachineType? carried)
    {
        float progress = Math.Clamp(dirt / (float)cost, 0f, 1f);
        bool ready = progress >= 1f;
        bool carryingThis = carried == type;
        bool blocked = carried != null && !carryingThis; // the other machine is in hand

        var sMin = At(panelX + BuildSlotX[col], BuildSlotY[0]);
        var sMax = At(panelX + BuildSlotX[col] + BuildSlotW, BuildSlotY[0] + BuildSlotH);
        if (blocked)
            dl.AddRectFilled(sMin, sMax, 0xA8000000u, S(6f));
        else if (!ready && !carryingThis)
        {
            float fillTop = sMax.Y - S(BuildSlotH) * progress;
            dl.AddRectFilled(sMin, new Vector2(sMax.X, fillTop), 0x80000000u, S(6f));
        }

        uint border = carryingThis
            ? ((int)(_time * 3f) % 2 == 0 ? 0xFF40FFFFu : 0xFF20A0A0u)
            : ready && !blocked ? 0xFF40FF60u : 0xFF707070u;
        dl.AddRect(sMin, sMax, border, S(6f), ImDrawFlags.None, S(2.5f));

        string label = carryingThis ? $"PLANT [{key}]" : ready ? $"{name} [{key}]" : $"{dirt}/{cost}";
        DrawOutlinedText(dl, S(15f), new Vector2(sMin.X + S(4f), sMax.Y - S(18f)), label, blocked ? 0xFF909090u : 0xFFFFFFFFu, S(1f));
        // The key is always visible, even before the slot is affordable.
        if (!ready && !carryingThis)
            DrawOutlinedText(dl, S(13f), new Vector2(sMin.X + S(4f), sMin.Y + S(3f)), $"[{key}] {name}", 0xFFB0B0B0u, S(1f));
    }

    private static void DrawOutlinedText(ImDrawListPtr dl, float size, Vector2 pos, string text, uint color, float outline = 1f)
    {
        var font = ImGui.GetFont();
        uint shadow = 0xE0000000u;
        dl.AddText(font, size, pos + new Vector2(outline, outline), shadow, text);
        dl.AddText(font, size, pos + new Vector2(-outline, outline), shadow, text);
        dl.AddText(font, size, pos + new Vector2(outline, -outline), shadow, text);
        dl.AddText(font, size, pos + new Vector2(-outline, -outline), shadow, text);
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
    /// One banner at a time, most urgent first: overheating, then low shield, then running hot. When each warning is
    /// on is decided by <see cref="PlayerWarnings"/>, which the warning sounds share.
    /// </summary>
    private void DrawWarnings(Tank player, bool heatDebugOverlayEnabled)
    {
        var vp = ViewportRect;
        if (vp.w <= 0 || player.IsDead)
        {
            _runningHotLatched = _shieldLowLatched = false;
            return;
        }

        _runningHotLatched = PlayerWarnings.RunningHot(_runningHotLatched, player);
        _shieldLowLatched = PlayerWarnings.LowShield(_shieldLowLatched, player);

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
        else if (_runningHotLatched)
        {
            DrawCenteredText(dl, 26f, cx, y, "RUNNING HOT", blink ? 0xFF30A0FFu : 0xFF80D0FFu);
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

    /// <param name="degreeSign">Draw a small degree ring after the number.</param>
    private void DrawNumberCentered(ImDrawListPtr dl, int value, float areaX, float areaY, float areaW, bool degreeSign = false)
    {
        string text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        const float degreeW = 9f;
        float totalW = text.Length > 0
            ? text.Length * DigitCellW + (text.Length - 1) * DigitSpacing
            : 0f;
        if (degreeSign)
            totalW += degreeW;
        float x = MathF.Floor(areaX + (areaW - totalW) * 0.5f);
        float y = MathF.Floor(areaY);
        var cell = new Vector2(S(DigitCellW), S(DigitCellH));

        foreach (char c in text)
        {
            if (c == '-')
            {
                // The digit strip has no minus: draw one in the digits' green, at their middle height.
                float my = y + DigitCellH * 0.5f;
                dl.AddRectFilled(At(x + 3f, my - 2.5f), At(x + DigitCellW - 3f, my + 2.5f), 0xFF000000u, 1f);
                dl.AddRectFilled(At(x + 4f, my - 1.5f), At(x + DigitCellW - 4f, my + 1.5f), 0xFFAAFACDu);
            }
            else if (c is >= '0' and <= '9')
            {
                int idx = c - '0';
                // Half-texel inset prevents sampling bleed from neighbor digits.
                float texelU = 0.5f / DigitStripW;
                float u0 = (float)(idx * DigitCellW) / DigitStripW + texelU;
                float u1 = (float)((idx + 1) * DigitCellW) / DigitStripW - texelU;
                var min = At(x, y);
                dl.AddImage(_digitStripTex, min, min + cell, new Vector2(u0, 0f), new Vector2(u1, 1f));
            }
            x += DigitCellW + DigitSpacing;
        }

        if (degreeSign)
        {
            var ring = At(x + 3f, y + 4.5f);
            dl.AddCircle(ring, S(3.2f), 0xFF000000u, 16, S(3.5f));
            dl.AddCircle(ring, S(3.2f), 0xFFAAFACDu, 16, S(1.6f));
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
    private double _engineOutput;

    private void DrawHeatNetworkOverlay(Tank player, World world)
    {
        var vp = ViewportRect;
        var tankBase = player.Base;
        if (vp.w <= 0 || vp.h <= 0 || tankBase == null)
            return;

        int relays = 0, powered = 0, pumping = 0, harvesters = 0, stalled = 0, recharging = 0, flat = 0;
        double inflow = 0.0;
        foreach (var machine in world.Machines.Machines)
        {
            if (machine.IsAlive && machine.Type == MachineType.Harvester && machine.State == MachineState.Planted &&
                machine.OwnerBase == tankBase)
            {
                harvesters++;
                if (machine.IsStalled)
                    stalled++;
                if (machine.IsReturning)
                    recharging++;
                else if (machine.Charge < Tweaks.Machine.HarvesterHeatPerHarvest)
                    flat++;
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
        // The engine turns part of the relay heat into charge; only the rest reaches the radiator.
        _engineOutput += (tankBase.EngineOutputPerTick - _engineOutput) * 0.1;
        _radiatorInflow += (inflow - tankBase.EngineOutputPerTick - _radiatorInflow) * 0.1;
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

        int dirt = player.BuildableDirt(_dockedAtOwnBase);
        bool fromBaseStore = _dockedAtOwnBase && player.Resources.Dirt < Tweaks.Machine.RelayDirtCost &&
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
            hint = "Not planted: it would not link - find a clear line";
            hintColor = TextBad;
        }
        else if (player.IsCarryingMachine)
        {
            bool carryingHarvester = player.CarriedMachineType == MachineType.Harvester;
            bool carryingPylon = player.CarriedMachineType == MachineType.Pylon;
            string carriedName = carryingHarvester ? "harvester" : carryingPylon ? "pylon" : "relay";
            if (world.TankBases.CheckBaseCollision(player.Position) != null)
            {
                hint = $"Carrying {carriedName} - leave the base to plant it";
                hintColor = TextInfo;
            }
            else if (carryingHarvester)
            {
                hint = "R: plant harvester here - it mines nearby dirt into your base store";
                hintColor = TextGood;
            }
            else
            {
                var preview = world.LinkMap.PreviewLink(player.Position, world.Terrain);
                (hint, hintColor) = preview.Status switch
                {
                    LinkPreviewStatus.Live => (carryingPylon
                        ? "T: plant pylon here - it charges harvesters it can see"
                        : "E: plant relay here - it will link (green line)", TextGood),
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
            if (dirt >= Tweaks.Machine.PylonDirtCost && harvesters > 0)
                hint += $"   T: pylon ({Tweaks.Machine.PylonDirtCost})";
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
            ColoredText(TextDim, $"Base dirt store {tankBase.Materials.Dirt}/{tankBase.Materials.DirtCapacity}");

            // Power: the charge store and what the engine is making from relay heat right now.
            float chargeFill = (float)Math.Clamp(tankBase.Charge / tankBase.ChargeCapacity, 0.0, 1.0);
            double perSecond = _engineOutput * Tweaks.Perf.TargetFps;
            ColoredText(chargeFill < 0.1f ? TextBad : chargeFill < 0.3f ? TextWarn : TextInfo,
                $"Charge {tankBase.Charge / 1000.0:0}k / {tankBase.ChargeCapacity / 1000.0:0}k" +
                (perSecond >= 1.0 ? $"   engine +{perSecond / 1000.0:0.0}k/s" : "   engine idle"));
            ImGui.ProgressBar(chargeFill, new Vector2(180f, 8f), string.Empty);

            if (harvesters > 0)
            {
                ColoredText(stalled > 0 ? TextWarn : TextDim,
                    $"Harvesters working {Math.Max(0, harvesters - stalled - recharging - flat)}/{harvesters}" +
                    (stalled > 0 ? $", {stalled} too hot" : ""));
                if (recharging > 0 || flat > 0)
                    ColoredText(flat > 0 ? TextBad : TextWarn,
                        (recharging > 0 ? $"{recharging} heading to a pylon" : "") +
                        (recharging > 0 && flat > 0 ? ", " : "") +
                        (flat > 0 ? $"{flat} out of charge" : ""));
            }
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
