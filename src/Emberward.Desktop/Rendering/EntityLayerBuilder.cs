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
    public float ClipX, ClipY;
    public float LocalX, LocalY;
    public float R, G, B, Kind;
    public float P0, P1, P2, P3;
}

/// <summary>
/// Turns simulation snapshots into the entity layer's quads: radar blips, network coverage, links, the placement preview,
/// machines, tanks, projectiles and charge labels, in that order (later ones draw on top). <see cref="EntityInterpolator"/> places
/// tanks and machines between the last two simulation steps; each family's emitter in <c>Rendering/Entities</c>
/// writes its quads. The layer reads simulation state solely for drawing.
/// </summary>
public sealed class EntityLayerBuilder
{
    /// <summary>The shaders' animation phase wraps here so it keeps float precision in long sessions.</summary>
    private const float AnimationPhaseWrapSeconds = 1000f;

    /// <summary>A protected (just respawned) tank blinks on and off at this period.</summary>
    private const int ProtectedBlinkMilliseconds = 120;

    private readonly EntityInterpolator _interpolator = new();
    private readonly EntityQuads _quads = new();
    private readonly CoverageQuads _coverage = new();
    private readonly LinkQuads _links = new();
    private readonly BeamFader _beams = new();
    private readonly PreviewQuads _preview = new();
    private readonly ProjectileQuads _projectiles = new();
    private readonly PowerLabelQuads _powerLabels = new();
    private TankRenderState[] _tankScratch = [];
    private MachineRenderState[] _machineScratch = [];

    public EntityVertex[] Vertices => _quads.Vertices;
    public int VertexCount => _quads.VertexCount;

    /// <summary>Records the world after a simulation step; the previous record becomes the interpolation start.</summary>
    public void CaptureStep(World world, int localSlot)
    {
        if (_tankScratch.Length < world.TankList.Count)
            _tankScratch = new TankRenderState[world.TankList.Count];
        int tankCount = world.TankList.CopyRenderStates(_tankScratch);
        _interpolator.CaptureTanks(_tankScratch.AsSpan(0, tankCount));

        if (_machineScratch.Length < world.Machines.Count)
            _machineScratch = new MachineRenderState[Math.Max(world.Machines.Count, 16)];
        int machineCount = world.Machines.CopyRenderStates(_machineScratch);
        _interpolator.CaptureMachines(_machineScratch.AsSpan(0, machineCount));

        _preview.Capture(world, localSlot);
        _coverage.Capture(world, localSlot);
        _links.Capture(world);
        _projectiles.Capture(world);
        _powerLabels.CaptureStep(world, localSlot);
    }

    /// <summary>
    /// Tank positions (cell coordinates) between the previous step (<paramref name="alpha"/> 0) and the latest
    /// (<paramref name="alpha"/> 1), indexed like the world's tank list.
    /// </summary>
    public ReadOnlySpan<PositionF> InterpolateTankPositions(float alpha) => _interpolator.InterpolateTankPositions(alpha);

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
        ReadOnlySpan<PositionF> tankPositions = _interpolator.InterpolateTankPositions(alpha);

        if (radar != null && vision != null)
            RadarQuads.Emit(_quads, radar, vision);

        // Beams (links, charge, the tether) under machines, machines under tanks (a tank carrying one draws on top).
        _coverage.Emit(_quads);
        _beams.Begin(visualSeconds);
        if (tankPositions.Length > 0)
            _coverage.SubmitTether(_beams, tankPositions[0]);
        _links.Submit(_beams, vision);
        MachineQuads.SubmitChargeBeams(_beams, _interpolator, alpha, vision);
        _beams.Emit(_quads);
        if (tankPositions.Length > 0)
            _preview.Emit(_quads, tankPositions[0]);
        MachineQuads.Emit(_quads, _interpolator, turnSeconds, snapHeadings, vision);
        TankQuads.Emit(_quads, _interpolator, tankPositions, turnSeconds, snapHeadings, protectedBlinkOn, vision);
        _projectiles.Emit(_quads, vision);
        _powerLabels.Emit(_quads, vision, powerLabelCellSize);
    }
}
