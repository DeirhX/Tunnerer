namespace Tunnerer.Core.Sensing;

using Tunnerer.Core.Config;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

/// <summary>
/// A tank's radar. Every <see cref="Tweaks.Radar.PulsePeriodTicks"/> the tank sends a pulse: a ring that runs
/// outward from where the tank stood, <see cref="Tweaks.Radar.FrontCellsPerTick"/> cells a tick, to
/// <see cref="Tweaks.Radar.Range"/> (both scaled by the radar upgrade, <see cref="RadarReach"/>). The pulse is a fan of rays that all advance with the front. Dirt and open
/// ground let a ray through; the first hard thing reflects it and stops it, and that one cell becomes a
/// <see cref="RadarContact"/> the moment the front reaches it, near things first. A contact lives for
/// <see cref="Tweaks.Radar.ContactLifetimeTicks"/>. What lies behind a reflector is in its shadow, so a shape is only
/// learned from several sides. Each kind of thing returns only from within its own range: a large rock formation
/// from the pulse's full reach, a thin wall, a machine, a tank or a cave only from nearer
/// (<see cref="Tweaks.Radar.SolidRangeMin"/> and the ranges beside it), an ore speck from nearer still and a deep
/// ore deposit from further (<see cref="Tweaks.Radar.OreRangeMin"/>, <see cref="Tweaks.Radar.OreRangeMax"/>). Out of its range a thing still stops the ray
/// and returns nothing.
/// <para>
/// When a later pulse's front passes an old contact, the ray nearest it checks it again: a contact that no longer
/// holds (the tank has driven off, the ground has cooled) goes, and a new return in the same cell replaces the old
/// one. A contact in the shadow of whatever the ray stopped at is not checked; it stays until it fades.
/// </para>
/// <para>
/// Heat uses the rays up (<see cref="Tweaks.Radar.HeatBudgetDegreeCells"/>). A ray loses strength in every hot cell it
/// crosses and leaves a <see cref="RadarEcho.Heat"/> contact there that says how hot the cell is, up to
/// <see cref="Tweaks.Radar.HeatReturnsPerRay"/> a ray. The hotter the cell the more it takes, so a ray gets through
/// many warm cells but only a few very hot ones before it runs out and stops. What it finds beyond comes back
/// fainter. A hot zone therefore shows as dots that reach a little way into it from the side the pulse came from.
/// </para>
/// </summary>
/// <remarks>
/// Like <see cref="VisionMap"/> it only observes: nothing in the simulation reads it. It reports only news: returns
/// from cells the side can see are dropped, so are returns from terrain it remembers correctly, and a contact goes
/// as soon as the side sees its cell. While a pulse
/// travels a tick walks the cells its front crossed, about <c>RaysPerPulse * FrontCellsPerTick</c>, and looks at
/// each live contact once; neither depends on the size of the map.
/// </remarks>
public sealed class Radar
{
    /// <summary>Each pulse's fan is turned this fraction of a ray spacing further round, so pulses fill each other's gaps.</summary>
    private const double PulseOffset = 0.6180339887498949;

    private const int RayCount = Tweaks.Radar.RaysPerPulse;

    /// <summary>The most contacts one ray can leave: what stopped it, its weak returns and its heat marks.</summary>
    private const int ReturnsPerRay = 1 + Tweaks.Radar.WeakReturnsPerRay + Tweaks.Radar.HeatReturnsPerRay;

    /// <summary>One ray of the pulse in flight: a Bresenham walk that resumes where the front left it.</summary>
    private struct Ray
    {
        public double DirX, DirY;
        /// <summary>Heat taken per hot cell scales by this, the length of path a cell stands for along this ray.</summary>
        public float CellLength;
        public int X, Y, EndX, EndY, Dx, Dy, Sx, Sy, Err;
        /// <summary>0, or the axis (1 x, 2 y) of the second half of a diagonal step still to take.</summary>
        public byte Owed;
        public bool Live, WasDirt;
        public int WeakLeft, HeatLeft;
        public float Strength;
        /// <summary>Squared distance from the pulse origin of the last cell reached, now and at the start of the tick.</summary>
        public int ReachSq, PrevReachSq;
    }

    private readonly VisionMap _vision;
    private readonly Ray[] _rays = new Ray[RayCount];
    private readonly RadarContact[] _contacts;
    private readonly bool[] _keep;
    private readonly HashSet<int> _newCells;
    private readonly List<RadarTarget> _onRay = new();
    private int _head;
    private int _count;
    private int _newFrom;
    private int _pulseCount;
    private double _fanOffset;
    private int _nextPulseTick;
    private int _lostToHeat;
    private float _front;
    private RadarReach _reach = RadarReach.Base;

    public Radar(VisionMap vision)
    {
        _vision = vision;
        // At most this many pulses have live contacts at once, each at most a return and the weak ones per ray.
        const int pulsesAlive = Tweaks.Radar.ContactLifetimeTicks / Tweaks.Radar.PulsePeriodTicks + 2;
        _contacts = new RadarContact[RayCount * ReturnsPerRay * pulsesAlive];
        _keep = new bool[_contacts.Length];
        _newCells = new HashSet<int>(RayCount * ReturnsPerRay);
    }

    /// <summary>Ticks this radar has run; a contact's age is <c>Tick - BornTick</c>.</summary>
    public int Tick { get; private set; }

    /// <summary>Whether a pulse is travelling.</summary>
    public bool PulseInFlight { get; private set; }

    /// <summary>The cell the pulse in flight (or the last one) was sent from.</summary>
    public Position PulseOrigin { get; private set; }

    /// <summary>How far the front of the pulse in flight has run from <see cref="PulseOrigin"/>, in whole cells.</summary>
    public int PulseRadius { get; private set; }

    /// <summary>How far the front has run, with the fraction of a cell a sped-up front (<see cref="RadarReach"/>) carries.</summary>
    public float PulseFront => _front;

    /// <summary>How far the front of the pulse in flight (or the last one) runs a tick: <see cref="RadarReach.FrontCellsPerTick"/>.</summary>
    public float PulseSpeed => _reach.FrontCellsPerTick;

    /// <summary>The full reach of the pulse in flight (or the last one), in cells: <see cref="RadarReach.Range"/>.</summary>
    public float PulseRange => _reach.Range;

    /// <summary>The share of the last finished pulse's rays that hot ground absorbed, 0..1.</summary>
    public float HeatLoss { get; private set; }

    /// <summary>Contacts still alive, oldest first.</summary>
    public int ContactCount => _count;

    public RadarContact ContactAt(int index) => _contacts[Slot(index)];

    private int Slot(int index) => (_head + index) % _contacts.Length;

    /// <summary>
    /// One tick with the radar on and its tank at <paramref name="origin"/>: old contacts expire, a pulse is sent if
    /// one is due, and the front of the pulse in flight runs on. <paramref name="ownColor"/>'s base walls do not
    /// reflect, so a tank at home is not blinded.
    /// </summary>
    public void Advance(
        ITerrainPixelReadOnly terrain, ITerrainHeatSource heat, Position origin, int ownColor,
        IReadOnlyList<RadarTarget> targets) =>
        Advance(terrain, heat, origin, ownColor, targets, RadarReach.Base);

    /// <summary>
    /// As <see cref="Advance(ITerrainPixelReadOnly, ITerrainHeatSource, Position, int, IReadOnlyList{RadarTarget})"/>,
    /// with the ranges of <paramref name="reach"/> (a tank's radar upgrades). A pulse keeps the reach it was sent
    /// with. A pulse that takes longer than <see cref="Tweaks.Radar.PulsePeriodTicks"/> to run out delays the next.
    /// </summary>
    public void Advance(
        ITerrainPixelReadOnly terrain, ITerrainHeatSource heat, Position origin, int ownColor,
        IReadOnlyList<RadarTarget> targets, RadarReach reach)
    {
        Age();
        if (!PulseInFlight)
        {
            if (Tick < _nextPulseTick)
                return;
            _reach = reach;
            Emit(origin);
        }

        int previousFront = PulseRadius;
        _front += _reach.FrontCellsPerTick;
        PulseRadius = (int)_front;
        _newFrom = _count;
        var ownWall = (TerrainPixel)((int)TerrainPixel.BaseMin + ownColor);
        bool anyLive = false;
        for (int i = 0; i < _rays.Length; i++)
        {
            ref Ray ray = ref _rays[i];
            ray.PrevReachSq = ray.ReachSq;
            if (!ray.Live)
                continue;
            Walk(ref ray, terrain, heat, ownWall, targets, previousFront);
            anyLive |= ray.Live;
        }
        Reconcile(terrain, heat, ownWall, targets, previousFront);

        if (!anyLive)
        {
            PulseInFlight = false;
            HeatLoss = _lostToHeat / (float)RayCount;
        }
    }

    /// <summary>One tick with the radar off (its tank is dead): contacts keep ageing, a pulse in flight is lost.</summary>
    public void Idle()
    {
        Age();
        PulseInFlight = false;
    }

    private void Age()
    {
        Tick++;
        while (_count > 0 && Tick - _contacts[_head].BornTick >= Tweaks.Radar.ContactLifetimeTicks)
        {
            _head = (_head + 1) % _contacts.Length;
            _count--;
        }

        // A contact the side has come to see is no longer news: sight and memory have the real thing now, and a
        // later pulse reports the cell again only if it changes.
        int kept = 0;
        for (int i = 0; i < _count; i++)
        {
            RadarContact contact = _contacts[Slot(i)];
            if (_vision.IsVisible(contact.Position))
                continue;
            if (kept != i)
                _contacts[Slot(kept)] = contact;
            kept++;
        }
        _count = kept;
    }

    private void Emit(Position origin)
    {
        PulseInFlight = true;
        PulseOrigin = origin;
        PulseRadius = 0;
        _front = 0f;
        _lostToHeat = 0;
        _nextPulseTick = Tick + Tweaks.Radar.PulsePeriodTicks;
        double offset = _pulseCount * PulseOffset;
        _fanOffset = offset - Math.Floor(offset);
        _pulseCount = (_pulseCount + 1) & 0xFFFF;

        float range = _reach.Range;
        for (int i = 0; i < _rays.Length; i++)
        {
            double angle = 2.0 * Math.PI * (i + _fanOffset) / RayCount;
            double dirX = DeterministicMath.Cos(angle), dirY = DeterministicMath.Sin(angle);
            int endX = origin.X + (int)Math.Round(dirX * range), endY = origin.Y + (int)Math.Round(dirY * range);
            int dx = Math.Abs(endX - origin.X), dy = -Math.Abs(endY - origin.Y);
            _rays[i] = new Ray
            {
                DirX = dirX,
                DirY = dirY,
                CellLength = (float)(1.0 / (Math.Abs(dirX) + Math.Abs(dirY))),
                X = origin.X,
                Y = origin.Y,
                EndX = endX,
                EndY = endY,
                Dx = dx,
                Dy = dy,
                Sx = origin.X < endX ? 1 : -1,
                Sy = origin.Y < endY ? 1 : -1,
                Err = dx + dy,
                Live = true,
                WeakLeft = Tweaks.Radar.WeakReturnsPerRay,
                HeatLeft = Tweaks.Radar.HeatReturnsPerRay,
                Strength = 1f,
            };
        }
    }

    /// <summary>Walks <paramref name="ray"/> through the cells the front reached this tick, until it stops or catches up.</summary>
    private void Walk(
        ref Ray ray, ITerrainPixelReadOnly terrain, ITerrainHeatSource heat, TerrainPixel ownWall,
        IReadOnlyList<RadarTarget> targets, int previousFront)
    {
        int width = terrain.Width, height = terrain.Height;
        ReadOnlySpan<TerrainPixel> pixels = terrain.Data;
        Position origin = PulseOrigin;
        int frontSq = PulseRadius * PulseRadius;
        CollectTargetsOnRay(ray.DirX, ray.DirY, previousFront, targets);

        while (true)
        {
            if (!NextCell(ray, out int x, out int y, out int err, out byte owed))
            {
                ray.Live = false;
                return;
            }
            int distanceSq = (x - origin.X) * (x - origin.X) + (y - origin.Y) * (y - origin.Y);
            if (distanceSq > frontSq)
                return;
            if ((uint)x >= (uint)width || (uint)y >= (uint)height)
            {
                ray.Live = false;
                return;
            }
            ray.X = x;
            ray.Y = y;
            ray.Err = err;
            ray.Owed = owed;
            ray.ReachSq = distanceSq;
            if (Visit(ref ray, x, y, width, pixels, heat, ownWall))
            {
                ray.Live = false;
                return;
            }
        }
    }

    /// <summary>
    /// The cell after the ray's last one, or false at the end of its range. A diagonal step goes through the side
    /// cell the line crosses first, so a ray cannot slip between two cells that touch only at a corner.
    /// </summary>
    private static bool NextCell(in Ray ray, out int x, out int y, out int err, out byte owed)
    {
        x = ray.X;
        y = ray.Y;
        err = ray.Err;
        owed = 0;
        if (ray.Owed == 1)
        {
            x += ray.Sx;
            err += ray.Dy;
            return true;
        }
        if (ray.Owed == 2)
        {
            y += ray.Sy;
            err += ray.Dx;
            return true;
        }
        if (x == ray.EndX && y == ray.EndY)
            return false;
        int e2 = 2 * err;
        bool stepX = e2 >= ray.Dy, stepY = e2 <= ray.Dx;
        if (stepX && (!stepY || e2 - ray.Dy >= ray.Dx - e2))
        {
            x += ray.Sx;
            err += ray.Dy;
            owed = stepY ? (byte)2 : (byte)0;
        }
        else
        {
            y += ray.Sy;
            err += ray.Dx;
            owed = stepX ? (byte)1 : (byte)0;
        }
        return true;
    }

    /// <summary>What the ray meets in cell (<paramref name="x"/>, <paramref name="y"/>); true if that stops it.</summary>
    private bool Visit(
        ref Ray ray, int x, int y, int width, ReadOnlySpan<TerrainPixel> pixels, ITerrainHeatSource heat,
        TerrainPixel ownWall)
    {
        var cell = new Position(x, y);
        int offset = x + y * width;
        // Heat first: it takes its share of the ray before anything in the cell can reflect it.
        float temperature = heat.GetHeatTemperature(offset);
        bool marked = false;
        if (temperature > Tweaks.Radar.HeatThreshold)
        {
            // Only a mark that is kept counts against the ray's allowance: hot ground in sight must not use it up
            // and leave the unseen heat beyond unmarked.
            // Hotter ground shows from further off.
            float heatRange = _reach.HeatRangeMin + (_reach.HeatRangeMax - _reach.HeatRangeMin) * HeatIntensity(temperature);
            if (ray.HeatLeft > 0 && Within(ray, heatRange) &&
                Record(cell, RadarEcho.Heat, HeatIntensity(temperature), width, pixels, isTerrain: false))
            {
                ray.HeatLeft--;
                marked = true;
            }
            ray.Strength -= (temperature - Tweaks.Radar.HeatThreshold) * ray.CellLength / _reach.HeatBudgetDegreeCells;
            if (ray.Strength <= 0f)
            {
                _lostToHeat++;
                return true;
            }
        }

        // A reflector too hot to read returns nothing but the heat mark its cell already has (or gets now).
        bool scrambled = temperature >= Tweaks.Radar.HeatScrambleTemperature;
        TerrainPixel pixel = pixels[offset];
        bool target = HitsTarget(cell, _onRay, out bool alive);
        bool ore = !target && Pixel.IsOre(pixel);
        if (target || (Pixel.IsBlockingCollision(pixel) && pixel != ownWall))
        {
            // Whatever it is stops the ray, but each kind of thing only returns from within its own range.
            RadarEcho echo = alive ? RadarEcho.Life : target ? RadarEcho.Metal
                : ore ? RadarEcho.Ore : RadarEcho.Solid;
            float range = alive ? _reach.LifeRange
                : target ? _reach.MachineRange
                : ore ? FormationRange(ray, x, y, width, pixels, heat, ore: true)
                : FormationRange(ray, x, y, width, pixels, heat, ore: false);
            if (!Within(ray, range))
                return true;
            if (!scrambled)
                Record(cell, echo, ray.Strength, width, pixels, isTerrain: !target);
            else if (!marked)
                Record(cell, RadarEcho.Heat, HeatIntensity(temperature), width, pixels, isTerrain: false);
            return true;
        }

        // The tank's own base is no discovery: its wall neither reflects nor counts as a cave's edge.
        bool dirt = Pixel.IsSoftCollision(pixel);
        if (pixel == ownWall)
            ray.WasDirt = false;
        if (ray.WasDirt && !dirt && ray.WeakLeft > 0 && Within(ray, _reach.CavityRange))
        {
            ray.WeakLeft--;
            Record(cell, RadarEcho.Cavity, ray.Strength, width, pixels, isTerrain: true);
        }
        ray.WasDirt = dirt;
        return false;
    }

    /// <summary>Whether the cell the ray has just reached is within <paramref name="range"/> cells of the pulse's origin.</summary>
    private static bool Within(in Ray ray, float range) => ray.ReachSq <= range * range;

    /// <summary>
    /// How far off the face at (<paramref name="x"/>, <paramref name="y"/>) returns: a thin shell or a speck only from
    /// the near range, a formation <see cref="Tweaks.Radar.FormationDepth"/> cells deep along the ray from the far
    /// one. Rock reads from <see cref="RadarReach.SolidRangeMin"/> to the pulse's full range, counting rock behind
    /// it; <paramref name="ore"/> reads from <see cref="RadarReach.OreRangeMin"/> to
    /// <see cref="RadarReach.OreRangeMax"/>, counting only cool plain ore, so a lump's warm core lends its rim no
    /// reach. Looks behind the face only when the answer depends on it.
    /// </summary>
    private float FormationRange(
        in Ray ray, int x, int y, int width, ReadOnlySpan<TerrainPixel> pixels, ITerrainHeatSource heat, bool ore)
    {
        float near = ore ? _reach.OreRangeMin : _reach.SolidRangeMin;
        float far = ore ? _reach.OreRangeMax : _reach.Range;
        if (Within(ray, near))
            return near;
        const int full = Tweaks.Radar.FormationDepth;
        int height = pixels.Length / width;
        int depth = 0;
        while (depth < full)
        {
            int bx = x + (int)Math.Round(ray.DirX * (depth + 1)), by = y + (int)Math.Round(ray.DirY * (depth + 1));
            if ((uint)bx >= (uint)width || (uint)by >= (uint)height)
                break;
            int behind = bx + by * width;
            if (!(ore ? IsCoolPlainOre(pixels[behind], heat.GetHeatTemperature(behind)) : IsRock(pixels[behind])))
                break;
            depth++;
        }
        return near + (far - near) * depth / (float)full;
    }

    /// <summary>What reflects as <see cref="RadarEcho.Solid"/>: anything that blocks movement but ore.</summary>
    private static bool IsRock(TerrainPixel pixel) =>
        Pixel.IsBlockingCollision(pixel) && !Pixel.IsOre(pixel);

    /// <summary>Ore that adds to a deposit's reach: plain, and cool enough that the ray would not read it as heat.</summary>
    private static bool IsCoolPlainOre(TerrainPixel pixel, float temperature) =>
        Pixel.IsOre(pixel) && !Pixel.IsOreCore(pixel) && temperature <= Tweaks.Radar.HeatThreshold;

    /// <summary>
    /// Keeps the targets a ray along (<paramref name="dirX"/>, <paramref name="dirY"/>) could touch between the
    /// front's last radius and its new one, so the walk tests few or none.
    /// </summary>
    private void CollectTargetsOnRay(double dirX, double dirY, int previousFront, IReadOnlyList<RadarTarget> targets)
    {
        _onRay.Clear();
        Position origin = PulseOrigin;
        for (int i = 0; i < targets.Count; i++)
        {
            RadarTarget target = targets[i];
            double tx = target.Position.X - origin.X, ty = target.Position.Y - origin.Y;
            // A square of half-size h reaches h * sqrt(2) from its centre. A cell the walk visits lies within
            // about 1.5 cells of the exact ray: half a cell from its Bresenham line, a little more for the side cell
            // of a diagonal step, and the line itself ends at a rounded cell.
            double reach = target.HalfSize * 1.5 + 2.0;
            double along = tx * dirX + ty * dirY;
            if (along < previousFront - reach || along > PulseRadius + reach || Math.Abs(tx * dirY - ty * dirX) > reach)
                continue;
            _onRay.Add(target);
        }
    }

    /// <summary>Whether a target covers <paramref name="cell"/>, and if so whether a living one does.</summary>
    private static bool HitsTarget(Position cell, IReadOnlyList<RadarTarget> targets, out bool alive)
    {
        bool hit = false;
        alive = false;
        for (int i = 0; i < targets.Count; i++)
        {
            RadarTarget target = targets[i];
            if (Math.Abs(cell.X - target.Position.X) <= target.HalfSize &&
                Math.Abs(cell.Y - target.Position.Y) <= target.HalfSize)
            {
                hit = true;
                alive |= target.Alive;
            }
        }
        return hit;
    }

    /// <summary>How hot a cell reads on the radar, 0..1 (<see cref="RadarContact.Strength"/> of a heat contact).</summary>
    private static float HeatIntensity(float temperature) => Math.Clamp(
        (temperature - Tweaks.Radar.HeatThreshold) / (Tweaks.Radar.HeatFullTemperature - Tweaks.Radar.HeatThreshold),
        0f, 1f);

    /// <summary>Keeps a return unless it tells the side nothing (<see cref="IsNews"/>); true if it was kept.</summary>
    private bool Record(
        Position cell, RadarEcho echo, float strength, int width, ReadOnlySpan<TerrainPixel> pixels, bool isTerrain)
    {
        if (!IsNews(cell.X + cell.Y * width, isTerrain, pixels))
            return false;
        if (_count == _contacts.Length)
        {
            _head = (_head + 1) % _contacts.Length;
            _count--;
            if (_newFrom > 0)
                _newFrom--;
        }
        _contacts[Slot(_count)] = new RadarContact(cell, echo, Tick, strength);
        _count++;
        return true;
    }

    /// <summary>
    /// A return tells the side something unless the cell is in sight, or it is terrain the side has explored and
    /// remembers as it is now. Remembered ground therefore only blips where it has changed since.
    /// </summary>
    private bool IsNews(int offset, bool isTerrain, ReadOnlySpan<TerrainPixel> pixels) =>
        !_vision.IsVisible(offset) &&
        !(isTerrain && _vision.IsExplored(offset) && _vision.Known.GetPixelRaw(offset) == pixels[offset]);

    /// <summary>
    /// Brings the older contacts up to date with this tick's front: one in a cell that has just returned again is
    /// replaced by the new return, and one the front has just passed on a ray that got that far is kept only if it
    /// still holds and is still news. Contacts in a ray's shadow are left alone.
    /// </summary>
    private void Reconcile(
        ITerrainPixelReadOnly terrain, ITerrainHeatSource heat, TerrainPixel ownWall,
        IReadOnlyList<RadarTarget> targets, int previousFront)
    {
        if (_count == 0)
            return;
        int width = terrain.Width;
        ReadOnlySpan<TerrainPixel> pixels = terrain.Data;
        Position origin = PulseOrigin;
        int frontSq = PulseRadius * PulseRadius;
        // A ray's last cell is less than one step behind the front, so nothing nearer than this was passed now.
        int passedFromSq = previousFront > 0 ? (previousFront - 1) * (previousFront - 1) : -1;
        _newCells.Clear();
        bool removed = false;

        for (int i = _count - 1; i >= 0; i--)
        {
            RadarContact contact = _contacts[Slot(i)];
            Position cell = contact.Position;
            int offset = cell.X + cell.Y * width;
            bool keep;
            if (i >= _newFrom)
            {
                keep = _newCells.Add(offset);
            }
            else if (_newCells.Contains(offset))
            {
                keep = false;
            }
            else
            {
                keep = true;
                int dx = cell.X - origin.X, dy = cell.Y - origin.Y;
                int distanceSq = dx * dx + dy * dy;
                if (distanceSq > passedFromSq && distanceSq <= frontSq)
                {
                    ref readonly Ray ray = ref _rays[NearestRay(dx, dy)];
                    if (distanceSq > ray.PrevReachSq && distanceSq <= ray.ReachSq)
                        keep = StillHolds(contact, offset, pixels, heat, ownWall, targets);
                }
            }
            _keep[i] = keep;
            removed |= !keep;
        }

        if (!removed)
            return;
        int kept = 0;
        for (int i = 0; i < _count; i++)
        {
            if (!_keep[i])
                continue;
            if (kept != i)
                _contacts[Slot(kept)] = _contacts[Slot(i)];
            kept++;
        }
        _count = kept;
    }

    /// <summary>The ray of the pulse in flight whose direction is closest to the offset (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    private int NearestRay(int dx, int dy)
    {
        double angle = DeterministicMath.Atan2((double)dy, dx);
        int ray = (int)Math.Round(angle * RayCount / (2.0 * Math.PI) - _fanOffset) % RayCount;
        return ray < 0 ? ray + RayCount : ray;
    }

    /// <summary>Whether what <paramref name="contact"/> says about its cell is still so, and still news.</summary>
    private bool StillHolds(
        RadarContact contact, int offset, ReadOnlySpan<TerrainPixel> pixels, ITerrainHeatSource heat,
        TerrainPixel ownWall, IReadOnlyList<RadarTarget> targets)
    {
        TerrainPixel pixel = pixels[offset];
        bool holds = contact.Echo switch
        {
            RadarEcho.Solid => IsRock(pixel) && pixel != ownWall,
            RadarEcho.Ore => Pixel.IsOre(pixel),
            RadarEcho.Metal => HitsTarget(contact.Position, targets, out bool living) && !living,
            RadarEcho.Life => HitsTarget(contact.Position, targets, out bool alive) && alive,
            RadarEcho.Cavity => !Pixel.IsSoftCollision(pixel) && !Pixel.IsBlockingCollision(pixel),
            _ => heat.GetHeatTemperature(offset) > Tweaks.Radar.HeatThreshold,
        };
        bool isTerrain = contact.Echo is RadarEcho.Solid or RadarEcho.Cavity or RadarEcho.Ore;
        return holds && IsNews(offset, isTerrain, pixels);
    }
}
