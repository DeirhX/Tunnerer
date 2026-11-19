namespace Emberward.Core.Regrowth;

using Emberward.Core.Config;
using Emberward.Core.Types;
using Emberward.Core.Terrain;

/// <summary>
/// Regrows dirt in open and scorched ground, except inside a <see cref="RegrowthBlocker"/>, where open ground stays
/// open (growth already under way still finishes).
/// <para>
/// The rules are per pass, a pass being every <see cref="Tweaks.World.DirtRecoverInterval"/>: an open cell with more
/// than two dirt neighbours has a chance to start growing, scorch fades a step and has a smaller chance, and growing
/// dirt has a chance to settle into dirt. Growing dirt is not yet a dirt neighbour, so a tunnel closes from its walls
/// inward, a layer at a time.
/// </para>
/// <para>
/// No pass scans the map. Each cell that can change has an appointment (<see cref="RegrowthSchedule"/>) at the tick
/// it is next looked at, drawn so that the odds are those of rolling on every pass, and new ones come from the pixel
/// writes the terrain lists (<see cref="RegrowthWrites"/>). A cell that cannot change has none: solid ground, open
/// ground away from dirt or inside a blocker. So a step costs what is growing, fading or was just dug, not the
/// size of the map or the length of its tunnels.
/// </para>
/// <para>
/// The map is cut into regions that each take their pass on a tick of their own, which spreads the work over the
/// ticks between passes while ground in one place still changes together. Every roll is a hash of the seed, the
/// tick and the cell, so a run repeats exactly.
/// </para>
/// </summary>
public sealed class TerrainRegrowth
{
    private const int NeighbourCount = 8;
    private const int RegionSize = Tweaks.World.RegrowthRegionSize;

    private static readonly int PassTicks =
        (int)((Tweaks.World.DirtRecoverInterval.Ticks + Tweaks.World.AdvanceStep.Ticks / 2) / Tweaks.World.AdvanceStep.Ticks);

    // An open cell is looked at as often as if all eight neighbours were dirt, and then grows with the share of that
    // chance its real neighbours give it. The odds per pass come out the same, and nothing has to be redrawn when a
    // neighbour is dug or settles.
    private static readonly PassWait OpenLook = new(
        Math.Min(1000, Tweaks.World.DirtRegrowSpeed * Tweaks.World.DirtRegrowBlankModifier * NeighbourCount),
        Tweaks.World.RegrowthLongestLookPasses);
    private static readonly PassWait Settling = new(Tweaks.World.DirtRecoverSpeed, Tweaks.World.RegrowthLongestSettlePasses);

    private readonly int _simulationSeed;
    private readonly RegrowthBlockerIndex _blockers = new();
    private readonly List<RegrowthBlocker> _lifted = new();
    private readonly List<int> _due = new();
    private RegrowthSchedule? _schedule;
    private byte[] _passTickOfRegion = [];
    private int _regionsX;
    private int _tick;

    /// <summary>A world without regrowth (<see cref="Match.MatchRules.TerrainRegrowth"/> off) builds none, and pays nothing for it.</summary>
    public TerrainRegrowth(int simulationSeed) => _simulationSeed = simulationSeed;

    /// <summary>Cells looked at by the last <see cref="Advance"/>: what the step cost.</summary>
    public int CellsVisitedInLastAdvance { get; private set; }

    /// <summary>Cells waiting to be looked at. None are left once nothing on the map can still change.</summary>
    public int CellsWaiting => _schedule?.Count ?? 0;

    /// <summary>True for a pixel regrowth does something with: it grows dirt in, fades, or finishes growing.</summary>
    public static bool ActsOn(TerrainPixel pixel) =>
        Pixel.IsOpenGround(pixel) || pixel == TerrainPixel.DirtGrow;

    /// <summary>
    /// Looks over the whole map once and gives every cell that can change its appointment. From then on the terrain
    /// lists the writes that matter. A world does this when it is built; <see cref="Advance"/> does it if it was not
    /// done, or if the terrain was since written in bulk.
    /// </summary>
    public void Survey(TerrainGrid terrain)
    {
        int w = terrain.Width, h = terrain.Height;
        _schedule = new RegrowthSchedule(w, h);
        _regionsX = (w + RegionSize - 1) / RegionSize;
        _passTickOfRegion = new byte[_regionsX * ((h + RegionSize - 1) / RegionSize)];
        for (int region = 0; region < _passTickOfRegion.Length; region++)
            _passTickOfRegion[region] = (byte)(FastRandom.Hash32((uint)_simulationSeed ^ (uint)region * 0x9e3779b9u) % (uint)PassTicks);

        ReadOnlySpan<TerrainPixel> pixels = terrain.Data;
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
                if (ActsOn(pixels[x + y * w]))
                    Look(terrain, x, y, fresh: true);
        terrain.RegrowthWrites.MarkSurveyed();
    }

    public void Advance(TerrainGrid terrain, IReadOnlyList<RegrowthBlocker> blockers)
    {
        CellsVisitedInLastAdvance = 0;
        _tick++;
        if (_schedule == null || !terrain.RegrowthWrites.Surveyed)
            Survey(terrain);
        RegrowthSchedule schedule = _schedule!;

        // Ground a blocker kept open is free to grow again once the blocker is gone.
        if (_blockers.Update(blockers, terrain.Width, terrain.Height, _lifted))
            foreach (RegrowthBlocker blocker in _lifted)
                LookAtOpenGroundIn(terrain, blocker);

        HearOfWrites(terrain);
        schedule.TakeDue(_tick, _due);
        foreach (int cell in _due)
            Keep(terrain, cell);
        ClearAndGiveBackAfterABurst(_due);
        // Regrowth's own writes are heard of at once, so the list is empty again when the step ends.
        HearOfWrites(terrain);
    }

    private void HearOfWrites(TerrainGrid terrain)
    {
        List<int> cells = terrain.RegrowthWrites.Cells;
        int w = terrain.Width;
        for (int i = 0; i < cells.Count; i++)
        {
            int x = cells[i] % w, y = cells[i] / w;
            if (Pixel.IsDirt(terrain.GetPixelRaw(cells[i])))
            {
                // New dirt: an open cell beside it may now have enough dirt neighbours to grow.
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if ((dx != 0 || dy != 0) && IsInterior(terrain, x + dx, y + dy))
                            Look(terrain, x + dx, y + dy, fresh: false);
            }
            if (IsInterior(terrain, x, y))
                Look(terrain, x, y, fresh: true);
        }
        ClearAndGiveBackAfterABurst(cells);
    }

    /// <summary>Empties a work list. One that a burst (a long tunnel dug at once) left far larger than it now needs is let go.</summary>
    private static void ClearAndGiveBackAfterABurst(List<int> list)
    {
        const int keptCapacity = 4096;
        bool mostlyUnused = list.Capacity > keptCapacity && list.Count < list.Capacity / 16;
        list.Clear();
        if (mostlyUnused)
            list.Capacity = keptCapacity;
    }

    private void LookAtOpenGroundIn(TerrainGrid terrain, RegrowthBlocker blocker)
    {
        int r = blocker.Radius;
        int minY = Math.Max(1, blocker.Center.Y - r), maxY = Math.Min(terrain.Height - 2, blocker.Center.Y + r);
        int minX = Math.Max(1, blocker.Center.X - r), maxX = Math.Min(terrain.Width - 2, blocker.Center.X + r);
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                Look(terrain, x, y, fresh: false);
    }

    /// <summary>
    /// Gives a cell the appointment its pixel calls for. With <paramref name="fresh"/> the cell was just written, so
    /// any appointment it has belongs to what it was and is replaced or dropped; without it, only an open cell that
    /// has none is given one.
    /// </summary>
    private void Look(TerrainGrid terrain, int x, int y, bool fresh)
    {
        CellsVisitedInLastAdvance++;
        RegrowthSchedule schedule = _schedule!;
        int cell = x + y * terrain.Width;
        TerrainPixel pixel = terrain.GetPixelRaw(cell);
        if (pixel == TerrainPixel.Blank)
        {
            if (!fresh && schedule.Has(x, y))
                return;
            // Whether a blocker covers the cell is asked when the appointment comes: blockers come and go.
            if (terrain.CountDirtNeighbors(new Position(x, y)) > 2)
                schedule.Set(x, y, PassTickAfter(x, y, OpenLook.Passes(Roll(cell, 0))));
            else if (fresh)
                schedule.Clear(x, y);
        }
        else if (!fresh)
        {
            return;
        }
        else if (pixel == TerrainPixel.DirtGrow)
        {
            schedule.Set(x, y, PassTickAfter(x, y, Settling.Passes(Roll(cell, 4))));
        }
        else if (Pixel.IsScorched(pixel))
        {
            schedule.Set(x, y, PassTickAfter(x, y, 1));
        }
        else
        {
            schedule.Clear(x, y);
        }
    }

    /// <summary>A cell's appointment has come: applies the rule for the pixel it holds now.</summary>
    private void Keep(TerrainGrid terrain, int cell)
    {
        CellsVisitedInLastAdvance++;
        int x = cell % terrain.Width, y = cell / terrain.Width;
        var pos = new Position(x, y);
        TerrainPixel pixel = terrain.GetPixelRaw(cell);
        if (pixel == TerrainPixel.Blank)
        {
            // Too few dirt neighbours, or kept open by a blocker: no further appointment. New dirt beside it, or
            // the blocker going, gives it one again.
            int neighbors = terrain.CountDirtNeighbors(pos);
            if (neighbors <= 2 || _blockers.IsBlocked(x, y))
                return;
            // SetPixel (not SetPixelRaw): a material change moves energy, which the thermal ledger must see.
            if (Roll(cell, 1) % NeighbourCount < (uint)neighbors)
                terrain.SetPixel(pos, TerrainPixel.DirtGrow);
            else
                _schedule!.Set(x, y, PassTickAfter(x, y, OpenLook.Passes(Roll(cell, 0))));
        }
        else if (Pixel.IsScorched(pixel))
        {
            int neighbors = terrain.CountDirtNeighbors(pos);
            int chance = Tweaks.World.DirtRegrowSpeed * neighbors * Tweaks.World.DirtRegrowScorchedModifier;
            if (neighbors > 2 && Roll(cell, 2) % 1000u < (uint)chance && !_blockers.IsBlocked(x, y))
            {
                terrain.SetPixel(pos, TerrainPixel.DirtGrow);
                return;
            }

            int faded = Math.Max(0, terrain.GetScorchIntensity(cell) - Tweaks.World.DecalIntensityDecay);
            terrain.SetScorchIntensity(cell, (byte)faded);
            if (faded <= 0)
                terrain.SetPixel(pos, TerrainPixel.Blank);
            else
                _schedule!.Set(x, y, PassTickAfter(x, y, 1));
        }
        else if (pixel == TerrainPixel.DirtGrow)
        {
            terrain.SetPixel(pos, TerrainPixel.Dirt);
        }
    }

    private static bool IsInterior(TerrainGrid terrain, int x, int y) =>
        x > 0 && y > 0 && x < terrain.Width - 1 && y < terrain.Height - 1;

    /// <summary>The tick of the cell's region's pass, <paramref name="passes"/> passes from now (at least one).</summary>
    private int PassTickAfter(int x, int y, int passes)
    {
        int passTick = _passTickOfRegion[x / RegionSize + y / RegionSize * _regionsX];
        int untilNext = (passTick - _tick % PassTicks + PassTicks) % PassTicks;
        if (untilNext == 0)
            untilNext = PassTicks;
        // An appointment must fit in the wheel, or it would wrap into an earlier slot and fire early. The longest
        // settle fits at today's frame rate; a faster one (longer passes in ticks) would cut the wait short here.
        passes = Math.Min(passes, (RegrowthSchedule.WheelTicks - 1 - untilNext) / PassTicks + 1);
        return _tick + untilNext + (passes - 1) * PassTicks;
    }

    private uint Roll(int cell, uint salt)
    {
        uint mixed = (uint)_simulationSeed
            ^ (uint)_tick * 0x9e3779b9u
            ^ (uint)cell * 73856093u
            ^ (salt * 83492791u);
        return FastRandom.Hash32(mixed);
    }

    /// <summary>Mixes the regrowth clock and how many appointments are waiting, for the sync digest.</summary>
    internal ulong MixLatent(ulong hash) => Fnv1a.Mix(Fnv1a.Mix(hash, _tick), CellsWaiting);
}
