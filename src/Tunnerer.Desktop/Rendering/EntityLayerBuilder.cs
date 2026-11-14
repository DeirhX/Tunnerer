namespace Tunnerer.Desktop.Rendering;

using System.Runtime.InteropServices;
using Tunnerer.Core;
using Tunnerer.Core.Config;
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
/// Turns simulation snapshots into the entity layer's quads (tanks and projectiles), placed between the last two
/// simulation steps so they move smoothly when the display refreshes faster than the simulation ticks.
/// It only reads render snapshots; nothing here feeds back into the simulation.
/// </summary>
public sealed class EntityLayerBuilder
{
    // Shape kinds; must match EntityPs.hlsl.
    private const float KindTankHull = 0f;
    private const float KindTankTurret = 1f;
    private const float KindHotShot = 2f;
    private const float KindFoam = 3f;

    private const float StateNormal = 0f;
    private const float StateHitFlash = 1f;
    private const float StateProtected = 2f;

    /// <summary>A tank that moved further than this in one step was respawned, not driven: do not slide it.</summary>
    private const float TeleportDistanceCells = 3f;

    /// <summary>How fast the drawn hull swings round to a new heading (radians per second).</summary>
    private const float HullTurnRate = 16f;

    private const int VerticesPerQuad = 6;

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

        int projectileCount = world.Projectiles.Count;
        if (_projectiles.Length < projectileCount)
            _projectiles = new ProjectileRenderState[Math.Max(projectileCount, 64)];
        _projectileCount = world.Projectiles.CopyRenderStates(_projectiles);
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
    /// </summary>
    public void Build(in RenderView view, float alpha, float turnSeconds, bool snapHeadings)
    {
        _vertexCount = 0;
        ReadOnlySpan<PositionF> tankPositions = InterpolateTankPositions(alpha);
        float scale = view.PixelScale;

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
                : tank.IsProtected && (Environment.TickCount64 / 120) % 2 == 0 ? StateProtected
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
