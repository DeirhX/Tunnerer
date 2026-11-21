namespace Emberward.Core.Entities.Emberites;

using System.Runtime.InteropServices;
using Emberward.Core.Collision;
using Emberward.Core.Config;
using Emberward.Core.Entities.Bases;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.LevelGen;
using Emberward.Core.Rendering;
using Emberward.Core.Sensing;
using Emberward.Core.Snapshots;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

/// <summary>
/// The emberites (DESIGN.md §6.2): creatures that nest in pyrite pockets and lumps, guard the ore and hunt any tank
/// that runs hot. Present only in worlds whose rules ask for it (<see cref="Match.MatchRules.Emberites"/>), and costing
/// nothing while no nest is awake and none is out: a dormant nest holds counters and is looked at once in
/// <see cref="Tweaks.Emberites.WakeCheckTicks"/> ticks, a creature in its nest has no state of its own. A woken nest sends
/// them in pulses sized by the hunted tank's heat, each after a stir that warns of it.
/// </summary>
/// <remarks>
/// Heat is conserved. A creature's charge is made, and booked as injected, when it first leaves its nest; a bite
/// moves it into the tank; a kill spills it into the ground where it dies; a resting creature recharges by taking heat
/// out of the deposit, leaves again only with a full charge, and starves where the deposit has none to give. <see cref="TotalCharge"/> is what the swarm holds, so the balance of
/// <see cref="World.SumSystemThermalEnergy"/> closes. Burrowing writes no pixels.
/// </remarks>
public sealed partial class EmberiteSwarm
{
    private readonly TerrainGrid _terrain;
    private readonly TankList _tanks;
    private readonly TankBases _bases;
    private readonly StepEventLog _events;
    [NotSaved("fixed: a hook onto the world, which knows what stands where")]
    private readonly Func<Position, bool> _isOccupied;
    private readonly double _fullCharge;
    private readonly List<Nest> _nests = new();
    private readonly List<Emberite> _emberites = new();
    private readonly List<EmberiteFlowField> _fields = new();
    private readonly EmberiteTrails _trails;
    private FastRandom _rng;
    private int _tick;
    private int _nextId;

    [NotSaved("scratch: a field build fills the queue before it reads it")]
    private readonly PriorityQueue<int, long> _queue = new();
    [NotSaved("scratch: rebuilt from the creatures out at the start of each step")]
    private readonly WorldSectors _sectors;
    [NotSaved("scratch: whether the sector index holds anything to clear")]
    private bool _indexed;

    /// <param name="seed">The simulation seed mixed with this system's constant by <see cref="World"/>.</param>
    /// <param name="isOccupied">Whether a live tank or a planted machine covers a cell; pyrite a killed creature drops keeps clear of them.</param>
    public EmberiteSwarm(TerrainGrid terrain, TankList tanks, TankBases bases, StepEventLog events, SimulationSettings settings, int seed,
        Func<Position, bool> isOccupied)
    {
        _terrain = terrain;
        _tanks = tanks;
        _bases = bases;
        _events = events;
        _isOccupied = isOccupied;
        _fullCharge = Tweaks.Emberites.BiteDegrees * (double)settings.TankHeatCapacity;
        _rng = new FastRandom(seed);
        _sectors = new WorldSectors(terrain.Size);
        _trails = new EmberiteTrails(terrain.Size);
    }

    /// <summary>The nests, in the order they were made (the order the deposits were placed).</summary>
    public IReadOnlyList<Nest> Nests => _nests;

    /// <summary>How many creatures are out of their nests.</summary>
    public int OutCount => _emberites.Count;

    /// <summary>Creature steps taken since the match began: zero for as long as nothing is out, which is the swarm's cost rule.</summary>
    public long CreatureSteps { get; private set; }

    /// <summary>The trails creatures have worn in soil.</summary>
    public EmberiteTrails Trails => _trails;

    /// <summary>The swarm's tick: what <see cref="EmberiteTrails"/> reads its fading by.</summary>
    public int Tick => _tick;

    /// <summary>The flow fields kept now: one per hunted tank and per awake nest that something reads.</summary>
    public IReadOnlyList<EmberiteFlowField> Fields => _fields;

    /// <summary>
    /// Makes a nest in each pocket and lump of <paramref name="deposits"/> that is at least
    /// <see cref="Tweaks.Emberites.MinNestDistanceFromBase"/> from every base.
    /// </summary>
    public void Seed(IReadOnlyList<OreDeposit> deposits)
    {
        foreach (OreDeposit deposit in deposits)
        {
            if (deposit.Kind == OreDepositKind.Speck || !FarFromBases(deposit.Center))
                continue;
            bool lump = deposit.Kind == OreDepositKind.Lump;
            int minCells = lump ? Tweaks.Ore.LumpMinCells : Tweaks.Ore.PocketMinCells;
            int maxCells = lump ? Tweaks.Ore.LumpMaxCells : Tweaks.Ore.PocketMaxCells;
            int min = lump ? Tweaks.Emberites.LumpMinCreatures : Tweaks.Emberites.PocketMinCreatures;
            int max = lump ? Tweaks.Emberites.LumpMaxCreatures : Tweaks.Emberites.PocketMaxCreatures;
            float share = Math.Clamp((deposit.Cells - minCells) / (float)Math.Max(1, maxCells - minCells), 0f, 1f);
            int radius = (int)MathF.Ceiling(MathF.Sqrt(deposit.Cells) * Tweaks.Emberites.NestRadiusPerSqrtCell) + Tweaks.Emberites.NestRadiusPadding;
            AddNest(deposit.Center, radius, min + (int)MathF.Round(share * (max - min)));
        }
    }

    /// <summary>Makes a nest of <paramref name="creatures"/> at <paramref name="centre"/>, whose ground reaches <paramref name="radius"/> cells.</summary>
    public Nest AddNest(Position centre, int radius, int creatures)
    {
        var nest = new Nest { Centre = centre, Radius = radius, NeverReleased = creatures };
        _nests.Add(nest);
        return nest;
    }

    private bool FarFromBases(Position at)
    {
        int distance = Tweaks.Emberites.MinNestDistanceFromBase;
        foreach (TankBase tankBase in _bases.Bases)
            if (Position.DistanceSquared(at, tankBase.Position) < distance * distance)
                return false;
        return true;
    }

    /// <summary>The heat the swarm holds: what the creatures out carry and what the nests have stored.</summary>
    public double TotalCharge()
    {
        double total = 0.0;
        foreach (Nest nest in _nests)
            total += nest.Stored;
        foreach (Emberite emberite in _emberites)
            total += emberite.Charge;
        return total;
    }

    /// <summary>The pyrite the creatures out are carrying (units): stolen from a hold, not yet home.</summary>
    public int PyriteCarried()
    {
        int total = 0;
        foreach (Emberite emberite in _emberites)
            total += emberite.Pyrite;
        return total;
    }

    /// <summary>Whether ground at <paramref name="cell"/> belongs to a nest that still has creatures: its ore is guarded.</summary>
    public bool IsGuarded(Position cell)
    {
        foreach (Nest nest in _nests)
            if (!nest.IsCleared && Position.DistanceSquared(cell, nest.Centre) <= nest.Radius * nest.Radius)
                return true;
        return false;
    }

    /// <summary>Whether any nest still has creatures.</summary>
    public bool AnyGuarded
    {
        get
        {
            foreach (Nest nest in _nests)
                if (!nest.IsCleared)
                    return true;
            return false;
        }
    }

    /// <summary>
    /// How far a tank at <paramref name="heat"/> is sensed from: nothing below <see cref="Tweaks.Emberites.WakeHeat"/>, then
    /// <see cref="Tweaks.Emberites.WakeRange"/> growing to <see cref="Tweaks.Emberites.FullRange"/> at <see cref="Tweaks.Emberites.FullHeat"/>.
    /// </summary>
    public static float SenseRange(float heat)
    {
        if (heat < Tweaks.Emberites.WakeHeat)
            return 0f;
        float t = Math.Clamp((heat - Tweaks.Emberites.WakeHeat) / (Tweaks.Emberites.FullHeat - Tweaks.Emberites.WakeHeat), 0f, 1f);
        return Tweaks.Emberites.WakeRange + t * (Tweaks.Emberites.FullRange - Tweaks.Emberites.WakeRange);
    }

    /// <summary>
    /// How far from its nest a nest keeps hunting a tank at <paramref name="heat"/> by heat alone: nothing below
    /// <see cref="Tweaks.Emberites.LoseScentHeat"/>, then <see cref="Tweaks.Emberites.HuntLeashMinCells"/> growing to
    /// <see cref="Tweaks.Emberites.HuntLeashCells"/> at <see cref="Tweaks.Emberites.FullHeat"/>. A tank in sight is kept anyway.
    /// </summary>
    public static float HeatLeash(float heat)
    {
        if (heat < Tweaks.Emberites.LoseScentHeat)
            return 0f;
        float t = Math.Clamp((heat - Tweaks.Emberites.LoseScentHeat) / (Tweaks.Emberites.FullHeat - Tweaks.Emberites.LoseScentHeat), 0f, 1f);
        return Tweaks.Emberites.HuntLeashMinCells + t * (Tweaks.Emberites.HuntLeashCells - Tweaks.Emberites.HuntLeashMinCells);
    }

    /// <summary>What a creature finds in a cell: the ground a nest's own creatures burrow through is soil to them.</summary>
    public static EmberiteGround Classify(TerrainPixel pixel, Position cell, Position zoneCentre, int zoneRadius)
    {
        if (Pixel.IsAnyBaseCell(pixel))
            return EmberiteGround.Blocked;
        if (Pixel.IsOpenGround(pixel))
            return EmberiteGround.Open;
        if (Pixel.IsSoil(pixel) || (zoneRadius > 0 && Position.DistanceSquared(cell, zoneCentre) <= zoneRadius * zoneRadius))
            return EmberiteGround.Soil;
        return EmberiteGround.Blocked;
    }

    /// <summary>One step: nests wake and sleep, release and recharge; creatures out move, bite and come home.</summary>
    public void Advance()
    {
        _tick++;
        WatchForThieves();
        for (int i = 0; i < _nests.Count; i++)
            AdvanceNest(i, _nests[i]);
        if (_emberites.Count > 0)
            AdvanceEmberites();
        AdvanceFields();
    }

    /// <summary>Mixes what <see cref="WorldStateHash.Compute"/> reads of the swarm: the nests and the creatures out.</summary>
    internal ulong MixState(ulong hash)
    {
        hash = Fnv1a.Mix(hash, _nests.Count);
        foreach (Nest nest in _nests)
        {
            hash = Fnv1a.Mix(Fnv1a.Mix(hash, nest.NeverReleased), nest.Resting);
            hash = Fnv1a.Mix(Fnv1a.Mix(hash, nest.Out), nest.Awake);
            hash = Fnv1a.Mix(hash, nest.Stored);
            if (nest.Pyrite != 0)
                hash = Fnv1a.Mix(hash, nest.Pyrite);
        }
        hash = Fnv1a.Mix(hash, _emberites.Count);
        foreach (Emberite emberite in _emberites)
        {
            hash = Fnv1a.Mix(Fnv1a.Mix(hash, emberite.Position.X), emberite.Position.Y);
            hash = Fnv1a.Mix(Fnv1a.Mix(hash, emberite.Velocity.X), emberite.Velocity.Y);
            hash = Fnv1a.Mix(Fnv1a.Mix(hash, emberite.Charge), (int)emberite.State);
            hash = Fnv1a.Mix(hash, emberite.WornBlock);
            if (emberite.Pyrite != 0)
                hash = Fnv1a.Mix(hash, emberite.Pyrite);
            hash = Fnv1a.Mix(Fnv1a.Mix(hash, emberite.SightTick), emberite.Sees);
        }
        return _trails.Mix(hash);
    }

    /// <summary>Mixes what a step reads before it shows: the generator, timers, targets, thieves and the fields.</summary>
    internal ulong MixLatent(ulong hash)
    {
        hash = Fnv1a.Mix(Fnv1a.Mix(hash, _rng.State), _tick);
        hash = Fnv1a.Mix(hash, _nextId);
        foreach (Nest nest in _nests)
        {
            hash = Fnv1a.Mix(Fnv1a.Mix(hash, nest.ReleaseTimer), nest.Target);
            hash = Fnv1a.Mix(Fnv1a.Mix(Fnv1a.Mix(hash, (int)nest.Phase), nest.PulseLeft), nest.HungerTicks);
            hash = Fnv1a.Mix(Fnv1a.Mix(hash, nest.Thief), nest.ThiefTicks);
        }
        foreach (Emberite emberite in _emberites)
            hash = Fnv1a.Mix(Fnv1a.Mix(Fnv1a.Mix(hash, emberite.Id), emberite.Nest), emberite.Target);
        hash = Fnv1a.Mix(hash, _fields.Count);
        foreach (EmberiteFlowField field in _fields)
        {
            hash = Fnv1a.Mix(Fnv1a.Mix(hash, field.Kind), field.Id);
            hash = Fnv1a.Mix(Fnv1a.Mix(hash, field.Age), field.Idle);
            foreach (int cost in field.Cost)
                hash = Fnv1a.Mix(hash, cost);
        }
        return hash;
    }

    /// <summary>The creatures out, as the renderer draws them. Returns how many were written.</summary>
    public int CopyRenderStates(Span<EmberiteRenderState> destination)
    {
        int count = Math.Min(destination.Length, _emberites.Count);
        for (int i = 0; i < count; i++)
        {
            Emberite emberite = _emberites[i];
            var cell = (Position)emberite.Position;
            bool burrowing = _terrain.IsInside(cell) && Pixel.IsSoil(_terrain.GetPixelRaw(cell));
            destination[i] = new EmberiteRenderState(emberite.Id, emberite.Position, emberite.Velocity, emberite.Charge > 0.0, burrowing);
        }
        return count;
    }

    /// <summary>Adds the occupied nests as radar targets: a nest at rest gives a life echo.</summary>
    internal void CollectRadarTargets(List<RadarTarget> targets)
    {
        foreach (Nest nest in _nests)
            if (nest.Inside > 0)
                targets.Add(new RadarTarget(nest.Centre, Math.Max(2, nest.Radius / 2), Alive: true));
    }

    /// <summary>The nests as the renderer draws them.</summary>
    public int CopyNestRenderStates(Span<NestRenderState> destination)
    {
        int count = Math.Min(destination.Length, _nests.Count);
        for (int i = 0; i < count; i++)
        {
            Nest nest = _nests[i];
            destination[i] = new NestRenderState(nest.Centre, nest.Radius, nest.Inside, nest.Awake, nest.IsCleared, nest.Phase == NestPhase.Stirring);
        }
        return count;
    }

    private Span<Emberite> EmberiteSpan => CollectionsMarshal.AsSpan(_emberites);
}
