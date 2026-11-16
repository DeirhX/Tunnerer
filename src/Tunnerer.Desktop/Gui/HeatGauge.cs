namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Desktop.Rendering;
using static Tunnerer.Desktop.Gui.HudDraw;

/// <summary>The TEMP panel's moving parts: the temperature orb, the bar under the readout, the trend arrow and the off-network tag.</summary>
public sealed class HeatGauge(HudLayout layout)
{
    // Gauge shape inside the HUD art (texture pixels = ImGui units from the panel's top-left corner).
    private static readonly Vector2 OrbCenter = new(168f, 72f);
    private const float OrbRadius = 47f;

    private const float DialStartAngle = MathF.PI * 150f / 180f; // lower left, sweeping clockwise over the top
    private const float DialSweep = MathF.PI * 240f / 180f;

    private float _lastHeat = float.NaN;
    private float _heatTrend;
    private float _shownHeat = float.NaN;

    /// <summary>State of the temperature orb shader for the frame just drawn; the game passes it to the backend.</summary>
    public HeatOrbState HeatOrb { get; private set; }

    private Vector2 At(float x, float y) => layout.At(x, y);
    private float S(float v) => layout.S(v);

    private static float DialAngle(float heat) => DialStartAngle + DialSweep * HeatScale.Fraction(heat);

    public void DrawOrb(ImDrawListPtr dl, Tank player, float time) =>
        DrawOrb(dl, player, layout.At(OrbCenter), layout.S(OrbRadius), time);

    /// <summary>
    /// The temperature orb. A dial runs round the rim, coloured from cold (blue, the coolant charge) over green and
    /// amber to red past the safe maximum, lit up to the current temperature with a needle at its tip. Inside, a
    /// glass sphere always full of liquid is drawn by a shader (HeatOrbPs, see <see cref="HeatOrbEffect"/>): the
    /// liquid takes the temperature's colour and its state follows it, frozen to ice when pre-cooled, calm around
    /// ambient, bubbling as it heats and boiling once overheating burns the shield.
    /// </summary>
    private void DrawOrb(ImDrawListPtr dl, Tank player, Vector2 c, float r, float time)
    {
        float k = layout.Scale;
        float heat = player.IsDead ? HeatScale.Min : player.Heat;
        // Ease the shown temperature toward the real one so the liquid and needle move rather than jump.
        _shownHeat = float.IsNaN(_shownHeat) ? heat : _shownHeat + (heat - _shownHeat) * 0.15f;
        float shown = _shownHeat;
        float safe = Tweaks.Tank.HeatSafeMax;
        bool alive = !player.IsDead;
        bool burning = alive && (player.IsOverheating || player.IsAboveSafeHeat);
        float pulse = 0.5f + 0.5f * MathF.Sin(time * 10f);
        float lit = HeatScale.Fraction(shown);

        // Dark glass behind everything (hides the energy bolt baked into the art), then the liquid sphere.
        dl.AddCircleFilled(c, r - k, 0xFF0A0C0Eu, 64);
        float inner = r - 9f * k;
        HeatOrb = new HeatOrbState(
            LiquidColor: alive ? HeatScale.ColorRgb(shown) : new Vector3(0.12f, 0.13f, 0.14f),
            Frozen: alive ? Math.Clamp(-shown / (0.6f * Tweaks.Tank.CoolantCapacity), 0f, 1f) : 0f,
            Boil: alive ? Math.Clamp((shown - 0.4f * safe) / (0.6f * safe), 0f, 1f) : 0f,
            Burning: burning ? 0.6f + 0.4f * pulse : 0f,
            Time: time);
        dl.AddImage(HeatOrbEffect.TextureId, c - new Vector2(inner), c + new Vector2(inner));

        // Dial round the rim: dim track in the zone colours, lit up to the shown temperature.
        float arcR = r - 6f * k;
        float arcW = 5f * k;
        const int bands = 48;
        for (int i = 0; i < bands; i++)
        {
            float f0 = i / (float)bands, f1 = (i + 1) / (float)bands;
            float mid = HeatScale.Min + (HeatScale.Max - HeatScale.Min) * (f0 + f1) * 0.5f;
            float a0 = DialStartAngle + DialSweep * f0;
            float a1 = DialStartAngle + DialSweep * f1 + 0.01f;
            bool on = alive && f0 < lit;
            if (on && f1 > lit)
                a1 = DialStartAngle + DialSweep * lit;
            StrokeArc(dl, c, arcR, a0, a1, (HeatScale.Color(mid) & 0x00FFFFFFu) | (on ? 0xFF000000u : 0x48000000u), arcW);
        }
        if (burning)
            StrokeArc(dl, c, arcR, DialAngle(safe), DialStartAngle + DialSweep,
                PackColor(new Vector3(1f, 0.3f, 0.2f), 0.35f + 0.5f * pulse), arcW + 3f * k);

        // Ticks at ambient (0) and at the safe maximum, where overheating starts.
        foreach (float t in stackalloc float[] { 0f, safe })
        {
            float a = DialAngle(t);
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            uint tickCol = t == 0f ? 0xC0FFFFFFu : 0xFF4060FFu;
            dl.AddLine(c + dir * (arcR - arcW * 0.5f - 3f * k), c + dir * (arcR + arcW * 0.5f + k), 0xFF000000u, 4f * k);
            dl.AddLine(c + dir * (arcR - arcW * 0.5f - 2f * k), c + dir * (arcR + arcW * 0.5f), tickCol, 2f * k);
        }

        // Needle at the tip of the lit arc.
        if (alive)
        {
            float a = DialAngle(shown);
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
    /// Small arrow beside the temperature: up (red) while digging or firing heats the tank, down (blue) while it
    /// cools. The rate is smoothed so the arrow does not flicker.
    /// </summary>
    public void DrawTrend(ImDrawListPtr dl, Tank player, float x, float y)
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
    public void DrawOffNetworkTag(ImDrawListPtr dl, Tank player, float centerX, float time)
    {
        if (player.IsDead || !player.IsOffNetwork)
            return;

        float seconds = player.OffNetworkSecondsToOverheat;
        string text = seconds > 0f ? $"OFF NETWORK  ~{(int)MathF.Ceiling(seconds)}s" : "OFF NETWORK  OVERHEATING";
        bool urgent = player.IsOffNetworkUrgent;
        uint color = urgent
            ? ((int)(time * 4f) % 2 == 0 ? 0xFF3040FFu : 0xFF60A0FFu)
            : 0xFF40C8FFu;
        float size = S(20f);
        float width = ImGui.GetFont().CalcTextSizeA(size, float.MaxValue, 0f, text).X;
        var pos = At(centerX, -30f);
        DrawOutlinedText(dl, size, new Vector2(MathF.Floor(pos.X - width * 0.5f), pos.Y), text, color);
    }

    /// <summary>
    /// Temperature bar under the readout, on the same scale as the orb: a blue coolant zone left of the ambient
    /// mark, green to amber up to the safe-maximum mark, and red beyond it.
    /// </summary>
    public void DrawBar(ImDrawListPtr dl, Tank player, float x, float y, float w, float time)
    {
        float heat = player.Heat;
        const float barH = 5f;
        float X(float t) => x + w * HeatScale.Fraction(t);
        dl.AddRectFilled(At(x - 1f, y - 1f), At(x + w + 1f, y + barH + 1f), 0xC0000000u);
        dl.AddRectFilled(At(X(Tweaks.Tank.HeatSafeMax), y), At(x + w, y + barH), 0x503040FFu); // danger zone
        uint col = HeatScale.Color(heat);
        if (player.IsAboveSafeHeat && (int)(time * 4f) % 2 == 0)
            col = 0xFF8080FFu;
        dl.AddRectFilled(At(x, y), At(X(heat), y + barH), col);
        dl.AddLine(At(X(0f), y - 1f), At(X(0f), y + barH + 1f), 0x80FFFFFFu, S(1f));
        dl.AddLine(At(X(Tweaks.Tank.HeatSafeMax), y - 2f), At(X(Tweaks.Tank.HeatSafeMax), y + barH + 2f), 0xFF4060FFu, S(1f));
    }
}
