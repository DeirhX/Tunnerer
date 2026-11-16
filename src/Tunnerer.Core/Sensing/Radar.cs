namespace Tunnerer.Core.Sensing;

using Tunnerer.Core.Config;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

/// <summary>What reflected a radar ray.</summary>
public enum RadarEcho : byte
{
    /// <summary>Rock, concrete, hot or frost rock, another side's base wall. Stops the ray.</summary>
    Solid,

    /// <summary>Gold, a machine or a tank. Stops the ray.</summary>
    Metal,

    /// <summary>Open ground behind dirt: a tunnel or a cave. A weak return; the ray goes on.</summary>
    Cavity,
}

/// <summary>One radar return: the cell that reflected, what kind of thing it was and when (<see cref="Radar.Tick"/>).</summary>
public readonly record struct RadarContact(Position Position, RadarEcho Echo, int BornTick);

/// <summary>Something that is not terrain and reflects radar: a machine or a tank, as a square of cells.</summary>
public readonly record struct RadarTarget(Position Position, int HalfSize);

/// <summary>
/// A tank's radar. A beam turns round the tank, a few rays every tick, and reaches well beyond sight. Dirt and open
/// ground let a ray through; the first hard thing reflects it and stops it, and that one cell becomes a
/// <see cref="RadarContact"/> which lives for <see cref="Tweaks.Radar.ContactLifetimeTicks"/>. What lies behind a
/// reflector is in its shadow, so a shape is only learned from several sides. A contact stays where it was made:
/// something that moves leaves a stale trail.
/// </summary>
/// <remarks>
/// Like <see cref="VisionMap"/> it only observes: nothing in the simulation reads it. It reports only news: returns
/// from cells the side can see are dropped, and so are returns from terrain it remembers correctly. The cost per
/// tick is a few rays, whatever the size of the map.
/// </remarks>
public sealed class Radar
{
    /// <summary>Each revolution starts this fraction of a ray spacing further round, so sweeps fill each other's gaps.</summary>
    private const double RevolutionOffset = 0.6180339887498949;

    private readonly VisionMap _vision;
    private readonly RadarContact[] _contacts;
    private readonly List<RadarTarget> _onRay = new();
    private int _head;
    private int _count;
    private int _rayCounter;

    public Radar(VisionMap vision)
    {
        _vision = vision;
        _contacts = new RadarContact[
            Tweaks.Radar.RaysPerTick * (1 + Tweaks.Radar.WeakReturnsPerRay) * Tweaks.Radar.ContactLifetimeTicks];
    }

    /// <summary>Ticks this radar has run; a contact's age is <c>Tick - BornTick</c>.</summary>
    public int Tick { get; private set; }

    /// <summary>The direction the beam points after the last sweep, in radians (0 = +x, turning toward +y).</summary>
    public float BeamAngle { get; private set; }

    /// <summary>Contacts still alive, oldest first.</summary>
    public int ContactCount => _count;

    public RadarContact ContactAt(int index) => _contacts[(_head + index) % _contacts.Length];

    /// <summary>
    /// One tick with the radar running from <paramref name="origin"/>: old contacts expire and the beam casts its
    /// next rays. <paramref name="ownColor"/>'s base walls do not reflect, so a tank at home is not blinded.
    /// </summary>
    public void Sweep(ITerrainPixelReadOnly terrain, Position origin, int ownColor, IReadOnlyList<RadarTarget> targets)
    {
        Idle();
        for (int i = 0; i < Tweaks.Radar.RaysPerTick; i++)
        {
            int ray = _rayCounter % Tweaks.Radar.RaysPerRevolution;
            int revolution = _rayCounter / Tweaks.Radar.RaysPerRevolution;
            _rayCounter = (_rayCounter + 1) % (Tweaks.Radar.RaysPerRevolution * 1024);
            double offset = revolution * RevolutionOffset;
            offset -= Math.Floor(offset);
            double angle = 2.0 * Math.PI * (ray + offset) / Tweaks.Radar.RaysPerRevolution;
            BeamAngle = (float)angle;
            CastRay(terrain, origin, DeterministicMath.Cos(angle), DeterministicMath.Sin(angle), ownColor, targets);
        }
    }

    /// <summary>One tick with the radar off (its tank is dead): contacts keep ageing, nothing new is found.</summary>
    public void Idle()
    {
        Tick++;
        while (_count > 0 && Tick - _contacts[_head].BornTick >= Tweaks.Radar.ContactLifetimeTicks)
        {
            _head = (_head + 1) % _contacts.Length;
            _count--;
        }
    }

    private void CastRay(
        ITerrainPixelReadOnly terrain, Position origin, double dirX, double dirY, int ownColor,
        IReadOnlyList<RadarTarget> targets)
    {
        const int range = Tweaks.Radar.Range;
        int width = terrain.Width, height = terrain.Height;
        ReadOnlySpan<TerrainPixel> pixels = terrain.Data;
        CollectTargetsOnRay(origin, dirX, dirY, targets);
        var ownWall = (TerrainPixel)((int)TerrainPixel.BaseMin + ownColor);

        int x0 = origin.X, y0 = origin.Y;
        int x1 = x0 + (int)Math.Round(dirX * range), y1 = y0 + (int)Math.Round(dirY * range);
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int x = x0, y = y0;
        bool wasDirt = false;
        int weakLeft = Tweaks.Radar.WeakReturnsPerRay;

        while (true)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height)
                return;
            if (x != x0 || y != y0)
            {
                var cell = new Position(x, y);
                if (HitsTarget(cell))
                {
                    Record(cell, RadarEcho.Metal, width, pixels, isTerrain: false);
                    return;
                }

                TerrainPixel pixel = pixels[x + y * width];
                if (Pixel.IsGold(pixel))
                {
                    Record(cell, RadarEcho.Metal, width, pixels, isTerrain: true);
                    return;
                }
                if (Pixel.IsBlockingCollision(pixel) && pixel != ownWall)
                {
                    Record(cell, RadarEcho.Solid, width, pixels, isTerrain: true);
                    return;
                }

                // The tank's own base is no discovery: its wall neither reflects nor counts as a cave's edge.
                bool dirt = Pixel.IsSoftCollision(pixel);
                if (pixel == ownWall)
                    wasDirt = false;
                if (wasDirt && !dirt && weakLeft > 0)
                {
                    weakLeft--;
                    Record(cell, RadarEcho.Cavity, width, pixels, isTerrain: true);
                }
                wasDirt = dirt;
            }

            if (x == x1 && y == y1)
                return;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
    }

    /// <summary>Keeps the targets a ray along (<paramref name="dirX"/>, <paramref name="dirY"/>) could touch, so the walk tests few or none.</summary>
    private void CollectTargetsOnRay(Position origin, double dirX, double dirY, IReadOnlyList<RadarTarget> targets)
    {
        _onRay.Clear();
        for (int i = 0; i < targets.Count; i++)
        {
            RadarTarget target = targets[i];
            double tx = target.Position.X - origin.X, ty = target.Position.Y - origin.Y;
            // A square of half-size h reaches at most h * sqrt(2) from its centre; one more cell for the ray's steps.
            double reach = target.HalfSize * 1.5 + 1.0;
            if (tx * dirX + ty * dirY < -reach || Math.Abs(tx * dirY - ty * dirX) > reach)
                continue;
            _onRay.Add(target);
        }
    }

    private bool HitsTarget(Position cell)
    {
        foreach (RadarTarget target in _onRay)
            if (Math.Abs(cell.X - target.Position.X) <= target.HalfSize &&
                Math.Abs(cell.Y - target.Position.Y) <= target.HalfSize)
                return true;
        return false;
    }

    /// <summary>
    /// Keeps a return unless it tells the side nothing: the cell is in sight, or it is terrain the side has
    /// explored and remembers as it is now. Remembered ground therefore only blips where it has changed since.
    /// </summary>
    private void Record(Position cell, RadarEcho echo, int width, ReadOnlySpan<TerrainPixel> pixels, bool isTerrain)
    {
        int offset = cell.X + cell.Y * width;
        if (_vision.IsVisible(offset))
            return;
        if (isTerrain && _vision.IsExplored(offset) && _vision.Known.GetPixelRaw(offset) == pixels[offset])
            return;
        if (_count == _contacts.Length)
        {
            _head = (_head + 1) % _contacts.Length;
            _count--;
        }
        _contacts[(_head + _count) % _contacts.Length] = new RadarContact(cell, echo, Tick);
        _count++;
    }
}
