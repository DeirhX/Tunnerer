namespace Emberward.Desktop.Rendering.Entities;

using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Rendering;
using Emberward.Core.Sensing;
using Emberward.Core.Types;

/// <summary>
/// What a base shows of the fire it takes (DESIGN.md §4.9): a shell around it whose strength follows its gate shield (in
/// matches with an energy network), a flash where each enemy bullet lands, and armor and shield bars while it is hurt.
/// Impacts are read from <see cref="World.StepEvents"/> after each step; everything else is read from the bases. Output only.
/// </summary>
public sealed class BaseShieldQuads
{
    private const int MaxImpacts = 12;

    /// <summary>How long an impact flash lives, in simulation steps (a little under half a second at 24 steps/s).</summary>
    private const int ImpactSteps = 10;

    /// <summary>The bars stay up this many steps after the last hit (about 6 s).</summary>
    private const int BarSteps = 144;

    private const float ShellMargin = 2.5f;

    // params.w: flag bits in the integer part, the shaders' animation phase (a 4 s cycle) in the fraction.
    private const float FlagShell = 1f;
    private const float FlagDark = 2f;
    private const float FlagBars = 4f;
    private const float PhaseCycleSeconds = 4f;

    private struct Impact
    {
        public Position Cell;
        public int Color;
        public float Strength;
        public long Step;
    }

    private struct BaseView
    {
        public Position Position;
        public int Color;
        public float Strength;
        public float Armor;
        public bool Shielded;
        public bool Dark;
        public long LastHitStep;
        public long LastShieldHitStep;
    }

    private readonly Impact[] _impacts = new Impact[MaxImpacts];
    private int _impactCount;
    private int _impactNext;
    private readonly List<BaseView> _bases = new();
    private readonly Dictionary<int, long> _lastHit = new();
    private readonly Dictionary<int, long> _lastShieldHit = new();
    private long _step;

    /// <summary>Call after each simulation step, while <see cref="World.StepEvents"/> still holds that step's events.</summary>
    public void CaptureStep(World world)
    {
        _step++;
        _bases.Clear();
        foreach (var tankBase in world.TankBases.Bases)
        {
            var power = world.BasePowerOf(tankBase);
            _bases.Add(new BaseView
            {
                Position = tankBase.Position,
                Color = tankBase.Color,
                Strength = tankBase.IsDestroyed ? 0f : Math.Clamp((float)(power?.ShieldFill ?? 0.0), 0f, 1f),
                Armor = tankBase.IsDestroyed ? 0f : (float)tankBase.ArmorFill,
                Shielded = power != null && !tankBase.IsDestroyed,
                Dark = power?.IsDark == true && !tankBase.IsDestroyed,
                LastHitStep = _lastHit.TryGetValue(tankBase.Color, out long step) ? step : long.MinValue,
                LastShieldHitStep = _lastShieldHit.TryGetValue(tankBase.Color, out long shieldStep) ? shieldStep : long.MinValue,
            });
        }

        foreach (ref readonly StepEvent e in world.StepEvents)
        {
            if (e.Kind is not (StepEventKind.BaseDamaged or StepEventKind.BaseDestroyed
                or StepEventKind.BaseShieldHit or StepEventKind.BaseShieldBroken))
                continue;
            // Only a gate hit is something the shield stopped, so only a gate hit flashes the shield. A core hit still
            // counts as the base being hit (for its bar and its warning), but it does not flash the shield.
            if (e.Kind is StepEventKind.BaseShieldHit or StepEventKind.BaseShieldBroken)
            {
                _impacts[_impactNext] = new Impact
                {
                    Cell = e.Position,
                    Color = e.Color,
                    // The gate shield as it stands after the hit (0 for a spent shield).
                    Strength = StrengthOf(e.Color),
                    Step = _step,
                };
                _impactNext = (_impactNext + 1) % MaxImpacts;
                _impactCount = Math.Min(MaxImpacts, _impactCount + 1);
                _lastShieldHit[e.Color] = _step;
            }
            _lastHit[e.Color] = _step;
            for (int i = 0; i < _bases.Count; i++)
            {
                if (_bases[i].Color == e.Color)
                {
                    var view = _bases[i];
                    view.LastHitStep = _step;
                    if (e.Kind is StepEventKind.BaseShieldHit or StepEventKind.BaseShieldBroken)
                        view.LastShieldHitStep = _step;
                    _bases[i] = view;
                }
            }
        }
    }

    private float StrengthOf(int color)
    {
        foreach (var view in _bases)
        {
            if (view.Color == color)
                return view.Strength;
        }
        return 0f;
    }

    /// <summary>How many gate-hit flashes the shell is showing (the impacts a step captured, up to <c>MaxImpacts</c>).</summary>
    public int ImpactCount => _impactCount;

    public void Emit(EntityQuads quads, VisionMap? vision)
    {
        float scale = quads.View.PixelScale;
        float stepNow = _step + quads.Alpha;
        float phase = quads.Phase % PhaseCycleSeconds / PhaseCycleSeconds * 0.999f;
        float half = Tweaks.Base.BaseSize / 2f;
        float reach = half + ShellMargin + 6f;   // the quad leaves room for the shell's glow and the bars above it

        foreach (var b in _bases)
        {
            if (!Sight.Sees(vision, b.Position))
                continue;
            // The anchor is in world cells, as every quad's is; the screen position is only for the off-screen test.
            if (quads.IsOffScreen(quads.ScreenX(b.Position.X), quads.ScreenY(b.Position.Y), (reach + 2f) * scale))
                continue;
            float cx = EntityQuads.Centre(b.Position.X), cy = EntityQuads.Centre(b.Position.Y);

            float sinceHit = b.LastHitStep == long.MinValue ? float.MaxValue : stepNow - b.LastHitStep;
            bool hurt = b.Armor < 0.999f || sinceHit < BarSteps || b.Dark;
            float flags = (b.Shielded ? FlagShell : 0f) + (b.Dark ? FlagDark : 0f) + (hurt && b.Armor > 0f ? FlagBars : 0f);
            if (flags < 1f)
                continue;
            // The whole-shell flash and veins answer to gate hits only; a core hit lights the bars, not the shield.
            float sinceShieldHit = b.LastShieldHitStep == long.MinValue ? float.MaxValue : stepNow - b.LastShieldHitStep;
            float glow = Math.Clamp(1f - sinceShieldHit / ImpactSteps, 0f, 1f);
            quads.Add(cx, cy, 1f, 0f, -reach, reach, -reach, reach, TeamColour(b.Color), EntityShape.KindBaseShield,
                b.Strength, b.Armor, glow, flags + phase);
        }

        for (int i = 0; i < _impactCount; i++)
        {
            ref readonly Impact hit = ref _impacts[i];
            float age = stepNow - hit.Step;
            if (age >= ImpactSteps || !Sight.Sees(vision, hit.Cell))
                continue;
            const float radius = 9f;
            if (quads.IsOffScreen(quads.ScreenX(hit.Cell.X), quads.ScreenY(hit.Cell.Y), (radius + 2f) * scale))
                continue;
            float cx = EntityQuads.Centre(hit.Cell.X), cy = EntityQuads.Centre(hit.Cell.Y);
            quads.Add(cx, cy, 1f, 0f, -radius, radius, -radius, radius, TeamColour(hit.Color), EntityShape.KindShieldHit,
                age / ImpactSteps, hit.Strength, 0f, 0f);
        }
    }

    private static Color TeamColour(int tankColor) => TankSprites.GetPixelColor(1, tankColor);
}
