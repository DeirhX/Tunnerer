namespace Tunnerer.Desktop.Rendering;

using System.Runtime.InteropServices;
using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Links;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Projectiles;
using Tunnerer.Core.Rendering;
using Tunnerer.Core.Types;

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
/// Turns simulation snapshots into the entity layer's quads (machines, tanks and projectiles), placed between the last two
/// simulation steps so they move smoothly when the display refreshes faster than the simulation ticks.
/// It only reads render snapshots; nothing here feeds back into the simulation.
/// </summary>
public sealed class EntityLayerBuilder
{
    // Shape kinds; must match the kKind* constants in EntityPs.hlsl (EntityLayoutGuardTests).
    private const float KindTankHull = 0f;
    private const float KindTankTurret = 1f;
    private const float KindHotShot = 2f;
    private const float KindFoam = 3f;
    private const float KindRelay = 4f;
    private const float KindHarvester = 5f;
    private const float KindSenseRing = 6f;
    private const float KindLink = 7f;
    private const float KindChargeBeam = 8f;
    private const float KindPylon = 9f;
    private const float KindBattery = 10f;
    private const float KindPreviewBeam = 11f;
    private const float KindReachArea = 12f;
    private const float KindCoverage = 13f;
    private const float KindTether = 14f;

    // The player's network coverage (DESIGN.md §4.6): the nodes' fields, and the node cooling the tank right now.
    private readonly List<CoverageNode> _coverageNodes = new();
    private TankCoverage _playerCoverage;
    private bool _playerOffNetwork;

    /// <summary>What the player's carried machine would do if planted now; drawn from the player tank.</summary>
    private readonly record struct PlacementPreview(bool Active, MachineType Type, LinkPreview Link, float SenseHeat);
    private PlacementPreview _preview;

    // Machine flags (params.w); must match the kMachineFlag* constants in EntityPs.hlsl (EntityLayoutGuardTests).
    private const float MachineFlagStalled = 1f;
    private const float MachineFlagCarried = 2f;
    private const float MachineFlagLimited = 4f;

    /// <summary>How fast a drawn harvester turns to a new crawl direction (radians per second).</summary>
    private const float HarvesterTurnRate = 6f;

    private const float StateNormal = 0f;
    private const float StateHitFlash = 1f;
    private const float StateProtected = 2f;

    /// <summary>A tank that moved further than this in one step was respawned, not driven: do not slide it.</summary>
    private const float TeleportDistanceCells = 3f;

    /// <summary>How fast the drawn hull swings round to a new heading (radians per second).</summary>
    private const float HullTurnRate = 16f;

    private const int VerticesPerQuad = 6;

    /// <summary>The shaders' animation phase wraps here so it keeps float precision in long sessions.</summary>
    private const float AnimationPhaseWrapSeconds = 1000f;

    /// <summary>A protected (just respawned) tank blinks on and off at this period.</summary>
    private const int ProtectedBlinkMilliseconds = 120;

    private TankRenderState[] _tanks = [];
    private TankRenderState[] _previousTanks = [];
    private float[] _treadTravel = [];
    private float[] _previousTreadTravel = [];
    private float[] _hullHeading = [];
    private bool[] _hullHeadingKnown = [];
    private PositionF[] _tankPositions = [];
    private int _tankCount;
    private bool _hasPreviousTanks;

    private ProjectileRenderState[] _projectiles = [];
    private int _projectileCount;

    private LinkRenderState[] _links = [];
    private int _linkCount;

    private MachineRenderState[] _machines = [];
    private MachineRenderState[] _previousMachines = [];
    private float[] _machineHeading = [];
    private int _machineCount;
    private int _previousMachineCount;

    private float _animationPhase;
    private bool _protectedBlinkOn;

    private EntityVertex[] _vertices = new EntityVertex[1024];
    private int _vertexCount;

    public EntityVertex[] Vertices => _vertices;
    public int VertexCount => _vertexCount;

    /// <summary>Records the world after a simulation step; the previous record becomes the interpolation start.</summary>
    public void CaptureStep(World world)
    {
        int tankCount = world.TankList.Count;
        if (_tanks.Length < tankCount)
        {
            Array.Resize(ref _tanks, tankCount);
            Array.Resize(ref _previousTanks, tankCount);
            Array.Resize(ref _treadTravel, tankCount);
            Array.Resize(ref _previousTreadTravel, tankCount);
            Array.Resize(ref _hullHeading, tankCount);
            Array.Resize(ref _hullHeadingKnown, tankCount);
            Array.Resize(ref _tankPositions, tankCount);
        }

        bool sameTanks = _hasPreviousTanks && tankCount == _tankCount;
        if (sameTanks)
        {
            Array.Copy(_tanks, _previousTanks, tankCount);
            Array.Copy(_treadTravel, _previousTreadTravel, tankCount);
        }
        _tankCount = world.TankList.CopyRenderStates(_tanks);
        for (int i = 0; i < _tankCount; i++)
        {
            if (!sameTanks || IsTeleport(_previousTanks[i], _tanks[i]))
            {
                _previousTanks[i] = _tanks[i];
                _previousTreadTravel[i] = _treadTravel[i];
                continue;
            }
            float dx = _tanks[i].Position.X - _previousTanks[i].Position.X;
            float dy = _tanks[i].Position.Y - _previousTanks[i].Position.Y;
            _treadTravel[i] = _previousTreadTravel[i] + MathF.Sqrt(dx * dx + dy * dy);
        }
        _hasPreviousTanks = true;

        CaptureMachines(world);
        CapturePlacementPreview(world);
        CaptureCoverage(world);

        if (_links.Length < world.LinkMap.LinkCount)
            _links = new LinkRenderState[Math.Max(world.LinkMap.LinkCount, 32)];
        _linkCount = world.LinkMap.CopyRenderStates(_links);

        int projectileCount = world.Projectiles.Count;
        if (_projectiles.Length < projectileCount)
            _projectiles = new ProjectileRenderState[Math.Max(projectileCount, 64)];
        _projectileCount = world.Projectiles.CopyRenderStates(_projectiles);
    }

    private void CaptureCoverage(World world)
    {
        _coverageNodes.Clear();
        _playerCoverage = TankCoverage.None;
        _playerOffNetwork = false;
        if (world.TankList.Count == 0 || world.TankList.Tanks[0] is not { Base: { } home } player)
            return;
        world.CollectCoverageNodes(home, _coverageNodes);
        _playerCoverage = player.IsDead ? TankCoverage.None : player.Coverage;
        _playerOffNetwork = !player.IsDead && player.IsOffNetwork;
    }

    /// <summary>
    /// The player's network coverage: a faint field around every node, and while the tank is covered (outside a
    /// base) a cool tether from the node carrying its heat away. Off network the fields brighten a little, so the way
    /// back reads at a glance.
    /// </summary>
    private void BuildCoverage(in RenderView view, PositionF player)
    {
        float scale = view.PixelScale;
        float phase = _animationPhase;
        float emphasis = _playerOffNetwork ? 1f : 0f;
        foreach (var node in _coverageNodes)
        {
            float nx = (node.Position.X + 0.5f) * scale - view.CameraPixels.X;
            float ny = (node.Position.Y + 0.5f) * scale - view.CameraPixels.Y;
            float r = node.Radius;
            if (IsOffScreen(nx, ny, (r + 4f) * scale, view))
                continue;
            AddQuad(view, nx, ny, 1f, 0f, -r - 4f, r + 4f, -r - 4f, r + 4f, Tweaks.Colors.Relay, KindCoverage,
                r, node.Strength, phase, emphasis);
        }

        if (!_playerCoverage.Covered)
            return;
        float dx = player.X - _playerCoverage.Node.X, dy = player.Y - _playerCoverage.Node.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 4f)
            return;   // standing on the node (or in the base): nothing to draw
        // Oriented from the tank toward the node: heat flows that way.
        float tx = (player.X + 0.5f) * scale - view.CameraPixels.X;
        float ty = (player.Y + 0.5f) * scale - view.CameraPixels.Y;
        AddQuad(view, tx, ty, -dx / length, -dy / length, -1.5f, length + 1.5f, -2.5f, 2.5f, Tweaks.Colors.Relay,
            KindTether, length, _playerCoverage.Strength, phase, 0f);
    }

    /// <summary>The player is tank 0: while it carries a machine, record where that machine would link or what it would sense.</summary>
    private void CapturePlacementPreview(World world)
    {
        _preview = default;
        if (world.TankList.Count == 0)
            return;
        var player = world.TankList.Tanks[0];
        if (player.IsDead || !player.IsCarryingMachine || player.CarriedMachineType is not MachineType type)
            return;
        var preview = world.PreviewPlacement(type, player.Position);
        _preview = new PlacementPreview(true, type, preview.Link, preview.SenseHeat);
    }

    private void CaptureMachines(World world)
    {
        int count = world.Machines.Count;
        if (_machines.Length < count)
        {
            int size = Math.Max(count, 16);
            Array.Resize(ref _machines, size);
            Array.Resize(ref _previousMachines, size);
            Array.Resize(ref _machineHeading, size);
        }
        Array.Copy(_machines, _previousMachines, Math.Min(_machineCount, _previousMachines.Length));
        _previousMachineCount = _machineCount;
        _machineCount = world.Machines.CopyRenderStates(_machines);
    }

    /// <summary>
    /// Tank positions (cell coordinates) between the previous step (<paramref name="alpha"/> 0) and the latest
    /// (<paramref name="alpha"/> 1), indexed like the world's tank list.
    /// </summary>
    public ReadOnlySpan<PositionF> InterpolateTankPositions(float alpha)
    {
        for (int i = 0; i < _tankCount; i++)
        {
            Position from = _previousTanks[i].Position;
            Position to = _tanks[i].Position;
            _tankPositions[i] = new PositionF(
                from.X + (to.X - from.X) * alpha,
                from.Y + (to.Y - from.Y) * alpha);
        }
        return _tankPositions.AsSpan(0, _tankCount);
    }

    /// <summary>
    /// Builds the quads for one displayed frame. <paramref name="turnSeconds"/> is the game time since the last
    /// displayed frame, used to swing hulls round to a new heading (zero while paused, so they hold still);
    /// <paramref name="snapHeadings"/> puts them on the new heading at once (step-locked automated runs).
    /// <paramref name="visualSeconds"/> is the game's visual clock, which drives every animation (pulses, blinking).
    /// </summary>
    public void Build(in RenderView view, float alpha, float turnSeconds, bool snapHeadings, float visualSeconds)
    {
        _vertexCount = 0;
        _animationPhase = visualSeconds % AnimationPhaseWrapSeconds;
        _protectedBlinkOn = (long)(visualSeconds * 1000f) / ProtectedBlinkMilliseconds % 2 == 0;
        ReadOnlySpan<PositionF> tankPositions = InterpolateTankPositions(alpha);
        float scale = view.PixelScale;

        // Links under machines, machines under tanks (a tank carrying one draws on top of it).
        if (_tankCount > 0)
            BuildCoverage(view, tankPositions[0]);
        BuildLinks(view);
        if (_tankCount > 0)
            BuildPlacementPreview(view, tankPositions[0]);
        BuildMachines(view, alpha, turnSeconds, snapHeadings);

        for (int i = 0; i < _tankCount; i++)
        {
            ref readonly TankRenderState tank = ref _tanks[i];
            float heading = UpdateHullHeading(i, tank.Direction, turnSeconds, snapHeadings);
            if (tank.IsDead)
                continue;

            // The tank occupies whole cells; its position is the centre of its middle cell.
            float cx = (tankPositions[i].X + 0.5f) * scale - view.CameraPixels.X;
            float cy = (tankPositions[i].Y + 0.5f) * scale - view.CameraPixels.Y;
            if (IsOffScreen(cx, cy, 8f * scale, view))
                continue;

            Color team = TankSprites.GetPixelColor(1, tank.Color);
            float state = tank.HitFlashTicks > 0 && (tank.HitFlashTicks / 3) % 2 == 1 ? StateHitFlash
                : tank.IsProtected && _protectedBlinkOn ? StateProtected
                : StateNormal;
            float travel = _previousTreadTravel[i] + (_treadTravel[i] - _previousTreadTravel[i]) * alpha;

            float hx = MathF.Cos(heading), hy = MathF.Sin(heading);
            AddQuad(view, cx, cy, hx, hy, -5.5f, 5.5f, -5.5f, 5.5f, team, KindTankHull, hx, hy, travel, state);

            float tx = tank.TurretDirection.X, ty = tank.TurretDirection.Y;
            float turretLength = MathF.Sqrt(tx * tx + ty * ty);
            if (turretLength < 1e-4f) { tx = hx; ty = hy; }
            else { tx /= turretLength; ty /= turretLength; }
            AddQuad(view, cx, cy, tx, ty, -1.6f, 4.8f, -1.6f, 1.6f, team, KindTankTurret, tx, ty, 0f, state);
        }

        for (int i = 0; i < _projectileCount; i++)
        {
            ref readonly ProjectileRenderState projectile = ref _projectiles[i];
            if (!projectile.IsAlive)
                continue;

            // The snapshot is where the shot is after the latest step; it got there along its velocity.
            float back = 1f - alpha;
            float px = (projectile.Position.X - projectile.Velocity.X * back) * scale - view.CameraPixels.X;
            float py = (projectile.Position.Y - projectile.Velocity.Y * back) * scale - view.CameraPixels.Y;
            if (IsOffScreen(px, py, 6f * scale, view))
                continue;

            float speed = projectile.Velocity.Length;
            float dx = speed > 1e-4f ? projectile.Velocity.X / speed : 1f;
            float dy = speed > 1e-4f ? projectile.Velocity.Y / speed : 0f;
            Color body = ProjectileList.Behaviors[(int)projectile.Type].DrawColor;

            switch (projectile.Type)
            {
                case ProjectileType.Bullet:
                {
                    float trail = Math.Clamp(speed * 0.9f, 0.8f, 3.5f);
                    const float radius = 0.42f, glow = 1.5f;
                    AddQuad(view, px, py, dx, dy, -trail - glow, glow, -glow, glow, body, KindHotShot, trail, radius, 0f, 0f);
                    break;
                }
                case ProjectileType.Shrapnel:
                {
                    float trail = Math.Clamp(speed * 1.6f, 0.25f, 1.4f);
                    const float radius = 0.22f, glow = 0.8f;
                    AddQuad(view, px, py, dx, dy, -trail - glow, glow, -glow, glow, body, KindHotShot, trail, radius, 0f, 0f);
                    break;
                }
                default:
                {
                    const float radius = 0.48f, margin = 0.75f;
                    AddQuad(view, px, py, dx, dy, -margin, margin, -margin, margin, body, KindFoam, 0f, radius, 0f, 0f);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Live and blocked links as beams. A link carrying heat is oriented so its +x runs toward the base, which is
    /// the way the shader's heat pulses flow.
    /// </summary>
    private void BuildLinks(in RenderView view)
    {
        float scale = view.PixelScale;
        float phase = _animationPhase;
        for (int i = 0; i < _linkCount; i++)
        {
            ref readonly LinkRenderState link = ref _links[i];
            if (!link.IsAlive || link.Type == LinkType.Theoretical)
                continue;

            Position from = link.FlowsFromTo ? link.From : link.To;
            Position to = link.FlowsFromTo ? link.To : link.From;
            float dx = to.X - from.X, dy = to.Y - from.Y;
            float length = MathF.Sqrt(dx * dx + dy * dy);
            if (length < 0.5f)
                continue;
            float ax = dx / length, ay = dy / length;
            float fx = (from.X + 0.5f) * scale - view.CameraPixels.X;
            float fy = (from.Y + 0.5f) * scale - view.CameraPixels.Y;
            float mx = fx + dx * 0.5f * scale, my = fy + dy * 0.5f * scale;
            if (IsOffScreen(mx, my, (length * 0.5f + 3f) * scale, view))
                continue;

            float heat = link.Type == LinkType.Live ? link.HeatFlow : 0f;
            float type = link.Type == LinkType.Live ? 0f : 1f;
            // Room for the glow, which the shader grows with the flow.
            float halfWidth = heat > 0.01f ? 4.5f : 1.2f;
            AddQuad(view, fx, fy, ax, ay, -1.5f, length + 1.5f, -halfWidth, halfWidth, Tweaks.Colors.LinkLive, KindLink,
                length, heat, phase, type);

            // Charge going out to pylons: a second, blue beam whose pulses run the other way, toward the pylon.
            if (link.Type == LinkType.Live && link.ChargeFlow > 0.01f)
            {
                Position chargeFrom = link.ChargeFlowsFromTo ? link.From : link.To;
                Position chargeTo = link.ChargeFlowsFromTo ? link.To : link.From;
                AddChargeBeam(view, chargeFrom.X, chargeFrom.Y, chargeTo.X, chargeTo.Y, link.ChargeFlow, phase);
            }
        }
    }

    /// <summary>
    /// The carried machine's preview, anchored on the (interpolated) player tank: the ground a relay would cool or a
    /// pylon would reach, and a translucent energy stream to the network point it would link to (red past whatever
    /// blocks the line). A carried harvester shows the ground it would sense instead.
    /// </summary>
    private void BuildPlacementPreview(in RenderView view, PositionF player)
    {
        if (!_preview.Active)
            return;
        float scale = view.PixelScale;
        float phase = _animationPhase;
        float cx = (player.X + 0.5f) * scale - view.CameraPixels.X;
        float cy = (player.Y + 0.5f) * scale - view.CameraPixels.Y;

        if (_preview.Type == MachineType.Harvester)
        {
            float r = Tweaks.Machine.HarvesterHeatSenseRadius + 1.5f;
            AddQuad(view, cx, cy, 1f, 0f, -r - 0.6f, r + 0.6f, -r - 0.6f, r + 0.6f, Tweaks.Colors.Harvester,
                KindSenseRing, _preview.SenseHeat, r, phase, 0f);
            return;
        }

        bool pylon = _preview.Type == MachineType.Pylon;
        float reach = pylon ? Tweaks.Machine.PylonRange : Tweaks.Machine.RelayCoolingRadius;
        Color areaColor = pylon ? Tweaks.Colors.Pylon : Tweaks.Colors.Relay;
        AddQuad(view, cx, cy, 1f, 0f, -reach - 6f, reach + 6f, -reach - 6f, reach + 6f, areaColor, KindReachArea,
            reach, pylon ? 1f : 0f, phase, 0f);

        var link = _preview.Link;
        if (link.Status is LinkPreviewStatus.NoNetwork)
            return;
        float dx = link.Target.X - player.X, dy = link.Target.Y - player.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.5f)
            return;
        float status = link.Status switch
        {
            LinkPreviewStatus.Live => 0f,
            LinkPreviewStatus.Blocked => 1f,
            _ => 2f,
        };
        float blockedAt = length;
        if (link.Status == LinkPreviewStatus.Blocked)
        {
            float bx = link.BlockedAt.X - player.X, by = link.BlockedAt.Y - player.Y;
            blockedAt = MathF.Sqrt(bx * bx + by * by);
        }
        // Too far: only a short fading feeler toward the network, not a full line across the map.
        float drawn = link.Status == LinkPreviewStatus.TooFar ? MathF.Min(length, 24f) : length;
        AddQuad(view, cx, cy, dx / length, dy / length, -1.5f, drawn + 1.5f, -3f, 3f, new Color(120, 255, 150),
            KindPreviewBeam, drawn, status, phase, blockedAt);
    }

    /// <summary>A charge beam from cell (<paramref name="x0"/>, <paramref name="y0"/>) to (<paramref name="x1"/>, <paramref name="y1"/>); its pulses flow toward the end.</summary>
    private void AddChargeBeam(in RenderView view, float x0, float y0, float x1, float y1, float flow, float phase)
    {
        float scale = view.PixelScale;
        float dx = x1 - x0, dy = y1 - y0;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.5f)
            return;
        float fx = (x0 + 0.5f) * scale - view.CameraPixels.X;
        float fy = (y0 + 0.5f) * scale - view.CameraPixels.Y;
        if (IsOffScreen(fx + dx * 0.5f * scale, fy + dy * 0.5f * scale, (length * 0.5f + 3f) * scale, view))
            return;
        AddQuad(view, fx, fy, dx / length, dy / length, -1.5f, length + 1.5f, -3.5f, 3.5f, Tweaks.Colors.Pylon,
            KindChargeBeam, length, flow, phase, 0f);
    }

    private void BuildMachines(in RenderView view, float alpha, float turnSeconds, bool snapHeadings)
    {
        float scale = view.PixelScale;
        float phase = _animationPhase;
        // The machine list only appends, or drops dead machines; while its size is unchanged, index i is the same
        // machine as in the previous step, so a crawling harvester can slide between steps.
        bool sameMachines = _previousMachineCount == _machineCount;

        for (int i = 0; i < _machineCount; i++)
        {
            ref readonly MachineRenderState machine = ref _machines[i];
            if (!machine.IsAlive)
                continue;

            float x = machine.Position.X, y = machine.Position.Y;
            if (sameMachines && _previousMachines[i].Type == machine.Type)
            {
                Position from = _previousMachines[i].Position;
                int jump = Math.Abs(machine.Position.X - from.X) + Math.Abs(machine.Position.Y - from.Y);
                if (jump <= 2)
                {
                    x = from.X + (machine.Position.X - from.X) * alpha;
                    y = from.Y + (machine.Position.Y - from.Y) * alpha;
                }
            }
            float cx = (x + 0.5f) * scale - view.CameraPixels.X;
            float cy = (y + 0.5f) * scale - view.CameraPixels.Y;
            if (IsOffScreen(cx, cy, 6f * scale, view))
                continue;

            bool carried = machine.State != MachineState.Planted;
            float flags = (machine.IsStalled ? MachineFlagStalled : 0f) + (carried ? MachineFlagCarried : 0f) +
                          (machine.IsPumpLimited ? MachineFlagLimited : 0f);

            switch (machine.Type)
            {
                case MachineType.Harvester:
                {
                    if (!carried)
                    {
                        // The ground the harvester senses, coloured by how close it is to stalling.
                        float r = Tweaks.Machine.HarvesterHeatSenseRadius + 1.5f;
                        AddQuad(view, cx, cy, 1f, 0f, -r - 0.6f, r + 0.6f, -r - 0.6f, r + 0.6f, Tweaks.Colors.Harvester,
                            KindSenseRing, machine.SenseHeat, r, phase, flags);
                    }
                    float heading = UpdateMachineHeading(i, machine.Heading, turnSeconds, snapHeadings);
                    float hx = MathF.Cos(heading), hy = MathF.Sin(heading);
                    // The drum turns while it works, not while stalled or carried.
                    float drum = machine.IsStalled || carried ? 0f : phase * 2.2f;
                    AddQuad(view, cx, cy, hx, hy, -3.4f, 3.4f, -3.0f, 3.0f, Tweaks.Colors.Harvester, KindHarvester,
                        hx, hy, drum, flags);
                    if (!carried)
                    {
                        if (machine.IsCharging)
                            AddChargeBeam(view, machine.ChargingFrom.X, machine.ChargingFrom.Y, x, y, 1f, phase);
                        // Battery gauge above the crawler, upright whichever way it faces.
                        AddQuad(view, cx, cy - 4.6f * scale, 1f, 0f, -2.4f, 2.4f, -0.9f, 0.9f, Tweaks.Colors.Pylon,
                            KindBattery, machine.Charge, machine.IsReturning ? 1f : 0f, phase,
                            machine.IsCharging ? 1f : 0f);
                    }
                    break;
                }
                case MachineType.Pylon:
                {
                    AddQuad(view, cx, cy, 1f, 0f, -3.6f, 3.6f, -3.6f, 3.6f, Tweaks.Colors.Pylon, KindPylon,
                        machine.IsPowered ? 1f : 0f, machine.IsGivingCharge ? 1f : 0f, phase, flags);
                    break;
                }
                case MachineType.Relay:
                {
                    float powered = machine.IsPowered ? 1f : 0f;
                    AddQuad(view, cx, cy, 1f, 0f, -3.6f, 3.6f, -3.6f, 3.6f, Tweaks.Colors.Relay, KindRelay,
                        powered, machine.PumpFraction, phase, flags);
                    break;
                }
            }
        }
    }

    /// <summary>The drawn heading of machine <paramref name="index"/>, turning smoothly to its crawl direction.</summary>
    private float UpdateMachineHeading(int index, Offset step, float turnSeconds, bool snap)
    {
        if (step == default)
            return _machineHeading[index];
        float target = MathF.Atan2(step.Y, step.X);
        if (snap)
            return _machineHeading[index] = target;
        float difference = MathF.IEEERemainder(target - _machineHeading[index], MathF.Tau);
        float maxTurn = HarvesterTurnRate * MathF.Max(0f, turnSeconds);
        _machineHeading[index] += Math.Clamp(difference, -maxTurn, maxTurn);
        return _machineHeading[index];
    }

    private static bool IsTeleport(in TankRenderState from, in TankRenderState to)
    {
        if (from.IsDead != to.IsDead)
            return true;
        int dx = to.Position.X - from.Position.X;
        int dy = to.Position.Y - from.Position.Y;
        return dx * dx + dy * dy > TeleportDistanceCells * TeleportDistanceCells;
    }

    /// <summary>Returns the drawn hull heading (radians), turning it towards the simulation's 8-way direction.</summary>
    private float UpdateHullHeading(int tankIndex, int direction, float turnSeconds, bool snap)
    {
        // Directions are a 3x3 grid with east on the left (0=NE, 1=N, 2=NW, 3=E, 5=W, 6=SE, 7=S, 8=SW); the middle
        // one means "no direction". See Tank.OffsetToDirection.
        int stepX = 1 - direction % 3;
        int stepY = direction / 3 - 1;
        if (stepX == 0 && stepY == 0)
            return _hullHeading[tankIndex];

        float target = MathF.Atan2(stepY, stepX);
        if (!_hullHeadingKnown[tankIndex] || snap)
        {
            _hullHeadingKnown[tankIndex] = true;
            return _hullHeading[tankIndex] = target;
        }

        float difference = MathF.IEEERemainder(target - _hullHeading[tankIndex], MathF.Tau);
        float maxTurn = HullTurnRate * MathF.Max(0f, turnSeconds);
        _hullHeading[tankIndex] += Math.Clamp(difference, -maxTurn, maxTurn);
        return _hullHeading[tankIndex];
    }

    private static bool IsOffScreen(float x, float y, float margin, in RenderView view) =>
        x < -margin || y < -margin || x > view.ViewSize.X + margin || y > view.ViewSize.Y + margin;

    /// <summary>
    /// Adds a quad centred on screen pixel (<paramref name="cx"/>, <paramref name="cy"/>), whose local +x axis points
    /// along (<paramref name="axisX"/>, <paramref name="axisY"/>) and which spans the given local rectangle in cells.
    /// </summary>
    private void AddQuad(
        in RenderView view, float cx, float cy, float axisX, float axisY,
        float minX, float maxX, float minY, float maxY,
        Color color, float kind, float p0, float p1, float p2, float p3)
    {
        if (_vertexCount + VerticesPerQuad > _vertices.Length)
            Array.Resize(ref _vertices, _vertices.Length * 2);

        float scale = view.PixelScale;
        float toClipX = 2f / view.ViewSize.X;
        float toClipY = 2f / view.ViewSize.Y;

        EntityVertex Corner(float lx, float ly)
        {
            float sx = cx + (lx * axisX - ly * axisY) * scale;
            float sy = cy + (lx * axisY + ly * axisX) * scale;
            return new EntityVertex
            {
                ClipX = sx * toClipX - 1f,
                ClipY = 1f - sy * toClipY,
                LocalX = lx,
                LocalY = ly,
                R = color.R / 255f,
                G = color.G / 255f,
                B = color.B / 255f,
                Kind = kind,
                P0 = p0,
                P1 = p1,
                P2 = p2,
                P3 = p3,
            };
        }

        EntityVertex a = Corner(minX, minY), b = Corner(maxX, minY), c = Corner(maxX, maxY), d = Corner(minX, maxY);
        _vertices[_vertexCount++] = a;
        _vertices[_vertexCount++] = b;
        _vertices[_vertexCount++] = c;
        _vertices[_vertexCount++] = a;
        _vertices[_vertexCount++] = c;
        _vertices[_vertexCount++] = d;
    }
}
