namespace Emberward.Desktop.Rendering;

using System.Runtime.InteropServices;
using Emberward.Core;
using Emberward.Core.Rendering;
using Emberward.Core.Sensing;
using Emberward.Core.Types;
using Emberward.Desktop.Rendering.Entities;

/// <summary>One corner of an entity quad. Layout must match VSIn in EntityVs.hlsl and the SDL_GPU vertex attributes in SdlGpuBackend.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct EntityVertex
{
    /// <summary>The quad corner in world cells, where its entity is at the start of the simulation step.</summary>
    public float CellX, CellY;
    public float LocalX, LocalY;
    public float R, G, B, Kind;
    public float P0, P1, P2, P3;
    /// <summary>How far the corner moves in one simulation step (cells); the shader adds it times the step's progress.</summary>
    public float MotionX, MotionY;
}

/// <summary>
/// Turns simulation snapshots into the entity layer's quads: radar blips, network coverage, links, the placement preview,
/// machines, tanks, projectiles, emberites and charge labels, in that order (later ones draw on top). <see cref="EntityMotion"/> says where
/// tanks and machines start and how they move through a simulation step (the vertex shader does the rest); each family's emitter in <c>Rendering/Entities</c>
/// writes its quads. The layer reads simulation state solely for drawing.
/// </summary>
public sealed class EntityLayerBuilder
{
    /// <summary>The shaders' animation phase wraps here so it keeps float precision in long sessions.</summary>
    private const float AnimationPhaseWrapSeconds = 1000f;

    /// <summary>A protected (just respawned) tank blinks on and off at this period.</summary>
    private const int ProtectedBlinkMilliseconds = 120;

    private readonly EntityMotion _motion = new();
    private readonly EntityQuads _quads = new();
    private readonly CoverageQuads _coverage = new();
    private readonly BaseShieldQuads _baseShields = new();
    private readonly TankShieldQuads _tankShields = new();
    private readonly LinkQuads _links = new();
    private readonly BeamFader _beams = new();
    private readonly PreviewQuads _preview = new();
    private readonly ProjectileQuads _projectiles = new();
    private readonly EmberiteQuads _emberites = new();
    private readonly PowerLabelQuads _powerLabels = new();
    private TankRenderState[] _tankScratch = [];
    private MachineRenderState[] _machineScratch = [];

    public EntityVertex[] Vertices => _quads.Vertices;
    public int VertexCount => _quads.VertexCount;

    /// <summary>Records the world after a simulation step; each moving thing starts from where it was last drawn.</summary>
    public void CaptureStep(World world, int localSlot)
    {
        if (_tankScratch.Length < world.TankList.Count)
            _tankScratch = new TankRenderState[world.TankList.Count];
        int tankCount = world.TankList.CopyRenderStates(_tankScratch);
        _motion.CaptureTanks(_tankScratch.AsSpan(0, tankCount));

        if (_machineScratch.Length < world.Machines.Count)
            _machineScratch = new MachineRenderState[Math.Max(world.Machines.Count, 16)];
        int machineCount = world.Machines.CopyRenderStates(_machineScratch, world.Settings.GeneratorMaxChargePerTick);
        _motion.CaptureMachines(_machineScratch.AsSpan(0, machineCount));

        _preview.Capture(world, localSlot);
        _coverage.Capture(world, localSlot);
        _links.Capture(world);
        _baseShields.CaptureStep(world);
        _tankShields.CaptureStep(world);
        _projectiles.Capture(world);
        _emberites.Capture(world);
        _powerLabels.CaptureStep(world, localSlot);
    }

    /// <summary>
    /// Tank positions (cell coordinates) as drawn at <paramref name="alpha"/> of the current step, indexed like the
    /// world's tank list.
    /// </summary>
    public ReadOnlySpan<PositionF> TankPositions(float alpha) => _motion.TankPositions(alpha);

    /// <summary>Where tank <paramref name="slot"/> is drawn from and how it moves through the step (cells per step).</summary>
    public (PositionF Start, PositionF Motion) TankMotion(int slot) => _motion.TankMotion(slot);

    /// <summary>
    /// Stops everything drawn where it is now: the world takes no step (a pause, or a network tick that carried none),
    /// so nothing should keep moving ahead of it. The next step starts again from there.
    /// </summary>
    public void Hold()
    {
        _motion.Hold();
        _projectiles.Hold();
        _emberites.Hold();
    }

    /// <summary>
    /// Builds the quads for one displayed frame. <paramref name="turnSeconds"/> is the game time since the last
    /// displayed frame, used to swing hulls round to a new heading (zero while paused, so they hold still);
    /// <paramref name="snapHeadings"/> puts them on the new heading at once (step-locked automated runs).
    /// <paramref name="visualSeconds"/> is the game's visual clock, which drives every animation (pulses, blinking).
    /// With fog of war, <paramref name="vision"/> is the player's: tanks, machines, shots and links out of its sight
    /// are left out, and <paramref name="radar"/>'s blips are drawn on the ground it cannot see.
    /// <paramref name="powerLabelCellSize"/> is the displayed size of a cell in UI units, or zero to hide charge labels.
    /// </summary>
    public void Build(
        in RenderView view, float alpha, float turnSeconds, bool snapHeadings, float visualSeconds,
        VisionMap? vision = null, Radar? radar = null, float powerLabelCellSize = 0f)
    {
        _quads.Begin(view, visualSeconds % AnimationPhaseWrapSeconds, alpha);
        bool protectedBlinkOn = (long)(visualSeconds * 1000f) / ProtectedBlinkMilliseconds % 2 == 0;
        _motion.SetAlpha(alpha);
        ReadOnlySpan<PositionF> tankPositions = _motion.TankPositions(alpha);

        if (radar != null && vision != null)
            RadarQuads.Emit(_quads, radar, vision);

        // Beams (links, the tether) under machines, machines under tanks (a tank carrying one draws on top).
        _coverage.Emit(_quads);
        _beams.Begin(visualSeconds);
        if (tankPositions.Length > 0)
            _coverage.SubmitTether(_beams, tankPositions[0]);
        _links.Submit(_beams, vision);
        _beams.Emit(_quads);
        if (tankPositions.Length > 0)
        {
            var (start, drift) = _motion.TankMotion(0);
            _preview.Emit(_quads, tankPositions[0], start, drift);
        }
        MachineQuads.Emit(_quads, _motion, turnSeconds, snapHeadings, vision);
        TankQuads.Emit(_quads, _motion, tankPositions, turnSeconds, snapHeadings, protectedBlinkOn, vision);
        _projectiles.Emit(_quads, vision);
        _emberites.Emit(_quads, vision);
        _tankShields.Emit(_quads, _motion, vision);
        _baseShields.Emit(_quads, vision);
        _powerLabels.Emit(_quads, vision, powerLabelCellSize);
    }
}
