namespace Tunnerer.Core;

using Tunnerer.Core.Types;
using Tunnerer.Core.Config;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Collision;
using Tunnerer.Core.Input;
using Tunnerer.Core.Entities.Projectiles;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Tanks;
using System.Diagnostics;
using Tunnerer.Core.Rendering;
using Tunnerer.Core.Sensing;
using Tunnerer.Core.Match;
using Tunnerer.Core.Network;
using Tunnerer.Core.Regrowth;

public class World
{
    private readonly TerrainGrid _terrain;
    private readonly TankBases _tankBases;
    private readonly TankList _tankList = new();
    private readonly ProjectileList _projectiles;
    private readonly MachineList _machines = new();
    private readonly LinkMap _linkMap;
    private readonly SpriteList _sprites = new();
    private readonly CollisionSolver _collisionSolver;
    private readonly TerrainRegrowth? _terrainRegrowth;
    private readonly List<Position> _hotRockPockets = new();
    private readonly List<Position> _frostRockPockets = new();
    private readonly List<LevelGen.OreDeposit> _oreDeposits = new();
    private readonly MatchResult _matchResult;
    private SectorGoal? _sectorGoal;
    // What tanks and machines see of the world each step; the same every step, so built once.
    private readonly SimulationContext _tankContext;
    private readonly MachineContext _machineContext;
    private WorldSystem[] _systems;
    private readonly int _simulationSeed;
    private readonly SimulationSettings _simulationSettings;
    private readonly StepEventLog _stepEvents = new();

    private int _advanceCount;
    private TimeSpan _elapsed;
    private double _auditThermalEnergy = double.NaN;
    private double _auditNetExternalEnergy;

    public TerrainGrid Terrain => _terrain;
    public TankBases TankBases => _tankBases;

    /// <summary>
    /// Total thermal energy of the whole simulation: the terrain and air fields, the heat stored in every tank
    /// and the heat stored in every base reactor. Together with <see cref="TerrainGrid.Ledger"/> this closes the
    /// energy balance.
    /// </summary>
    public double SumSystemThermalEnergy()
    {
        double total = _terrain.SumTotalThermalEnergy();
        foreach (var tank in _tankList.Tanks)
            total += tank.Heat * tank.ThermalCapacity;
        foreach (var tankBase in _tankBases.Bases)
            total += tankBase.ReactorHeatEnergy;
        return total;
    }
    /// <summary>
    /// Charge stored in every base and machine battery. Charge is converted heat, so
    /// <c>SumSystemThermalEnergy() + SumSystemCharge()</c> balances against <c>Injected - TotalSunk</c>.
    /// </summary>
    public double SumSystemCharge()
    {
        double total = 0.0;
        foreach (var tankBase in _tankBases.Bases)
            total += tankBase.Charge;
        foreach (var machine in _machines.Machines)
            total += machine.Charge;
        return total;
    }

    public TankList TankList => _tankList;
    public ProjectileList Projectiles => _projectiles;
    public MachineList Machines => _machines;
    public LinkMap LinkMap => _linkMap;
    public SpriteList Sprites => _sprites;
    public CollisionSolver CollisionSolver => _collisionSolver;
    public int AdvanceCount => _advanceCount;
    public SimulationSettings Settings => _simulationSettings;

    /// <summary>
    /// What happened during the last <see cref="Advance"/> step (shots, impacts, deaths, plantings, dockings...),
    /// valid until the next step begins. Presentation code learns about one-off happenings here instead of
    /// comparing state between steps; nothing in the simulation reads it.
    /// </summary>
    public ReadOnlySpan<StepEvent> StepEvents => _stepEvents.Events;

    public SimulationProfile Profile { get; } = new();

    /// <summary>Centres of the hot rock pockets placed at initialisation (empty unless the world was created with some).</summary>
    public IReadOnlyList<Position> HotRockPockets => _hotRockPockets;

    /// <summary>Centres of the frost pockets placed at initialisation (empty unless the world was created with some).</summary>
    public IReadOnlyList<Position> FrostRockPockets => _frostRockPockets;

    /// <summary>The emberite deposits seeded at initialisation (empty unless the world was created with ore).</summary>
    public IReadOnlyList<LevelGen.OreDeposit> OreDeposits => _oreDeposits;

    /// <summary>Whether a live tank's shape or a planted machine's square covers <paramref name="cell"/>.</summary>
    private bool IsCellOccupied(Position cell) =>
        _collisionSolver.TestPoint(cell, onTank: static _ => true, onMachine: static _ => true);

    /// <summary>
    /// Whether <paramref name="machine"/> centred on <paramref name="center"/> would overlap a live tank or another
    /// planted machine: the machine's square against the tank's shape (<see cref="TankSpriteShape"/>) or the other
    /// machine's square.
    /// </summary>
    private bool IsMachineSpotOccupied(Machine machine, Position center)
    {
        int half = machine.BoundingBox.HalfWidth;
        foreach (var tank in _tankList.Tanks)
        {
            if (tank.IsDead) continue;
            if (TankSpriteShape.OverlapsSquare(tank.Direction, tank.Position, center, half))
                return true;
        }
        _machines.CollectNear(center, half + Tweaks.Machine.BoundingBoxHalfSize, _nearMachines, inListOrder: false);
        foreach (var other in _nearMachines)
        {
            if (ReferenceEquals(other, machine) || !other.IsAlive || other.State != MachineState.Planted) continue;
            int reach = half + other.BoundingBox.HalfWidth;
            if (Math.Abs(other.Position.X - center.X) <= reach && Math.Abs(other.Position.Y - center.Y) <= reach)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Plants a carried machine where it stands: it becomes solid, joins the link network and belongs to
    /// <paramref name="owner"/> (the base it delivers to).
    /// </summary>
    public void PlantMachine(Machine machine, TankBase? owner)
    {
        machine.State = MachineState.Planted;
        machine.OwnerBase = owner;
        machine.Anchor = machine.Position;
        if (owner != null)
            machine.Charge += owner.DrawCharge(machine.ChargeCapacity - machine.Charge);
        if (machine.Behavior.IsLinkNode)
            machine.LinkPoint = _linkMap.RegisterPoint(machine.Position, LinkPointType.Machine);
        _stepEvents.Record(new StepEvent(StepEventKind.MachinePlanted, machine.Position, machine.OwnerColor, machine.Type));
    }

    /// <param name="rules">Which optional systems the world runs; null is <c>new MatchRules()</c> (see <see cref="MatchRules"/>).</param>
    public World(
        Size terrainSize,
        int simulationSeed = 0,
        MatchRules? rules = null,
        SimulationSettings? simulationSettings = null)
    {
        Rules = rules ?? new MatchRules();
        _simulationSeed = simulationSeed != 0 ? simulationSeed : 0x51A7E3;
        _simulationSettings = simulationSettings ?? SimulationSettings.FromTweaks();
        _terrain = new TerrainGrid(terrainSize, _simulationSettings);
        _tankBases = new TankBases(_simulationSettings);
        _projectiles = new ProjectileList(_simulationSeed ^ 0x5f3759df, _stepEvents);
        _linkMap = new LinkMap(_simulationSeed ^ 0x13579BDF);
        _collisionSolver = new CollisionSolver(_terrain);
        if (Rules.TerrainRegrowth)
            _terrainRegrowth = new TerrainRegrowth(_simulationSeed);
        _matchResult = new MatchResult(_tankList.Tanks);
        _tankContext = new SimulationContext
        {
            Terrain = _terrain,
            Heat = _terrain,
            Projectiles = _projectiles,
            CheckBaseCollision = _tankBases.CheckBaseCollision,
            Settings = _simulationSettings,
            Machines = _machines,
            PlantMachine = PlantMachine,
            CanPlantRelayAt = pos => _linkMap.PreviewLink(pos, _terrain).Status == LinkPreviewStatus.Live,
            CoverageFor = CoverageFor,
            IsOccupied = IsCellOccupied,
            IsTankBlocked = _collisionSolver.BlocksTank,
            Events = _stepEvents,
        };
        _machineContext = new MachineContext(_terrain, IsMachineSpotOccupied, _machines, IsMachineOnNetwork, _stepEvents);
        _systems = BuildSystems();
    }

    /// <summary>The optional systems this world runs with, fixed at construction.</summary>
    public MatchRules Rules { get; }

    public void Initialize(TerrainGrid generatedTerrain, Position[] spawns,
        int? decorationSeed = null, bool parallelMaterialize = false)
    {
        for (int i = 0; i < generatedTerrain.Size.Area; i++)
            _terrain[i] = generatedTerrain[i];

        _terrain.MaterializeTerrain(parallel: parallelMaterialize);
        // Without a decoration seed, decorations and pockets follow the generated level (its spawns) and the
        // simulation seed, so the same inputs always give the same world while different levels still differ.
        int seed = decorationSeed ?? SeedFromSpawns(spawns) ^ _simulationSeed;
        _terrain.DecorateTerrain(seed + 100);

        for (int i = 0; i < spawns.Length; i++)
            _tankBases.AddBase(spawns[i], i);

        _tankBases.CreateBasesInTerrain(_terrain);
        foreach (var tankBase in _tankBases.Bases)
            tankBase.LinkPoint = _linkMap.RegisterPoint(tankBase.Position, LinkPointType.Base);

        if (Rules.HotRockPockets > 0 || Rules.FrostRockPockets > 0 || Rules.SeedOre)
        {
            int pocketSeed = seed + 200;
            if (Rules.HotRockPockets > 0)
            {
                _hotRockPockets.AddRange(LevelGen.HotRockPockets.Place(
                    _terrain, spawns, Rules.HotRockPockets, pocketSeed, out Position? core));
                if (core is Position corePosition)
                    _sectorGoal = new SectorGoal(corePosition);
            }
            if (Rules.FrostRockPockets > 0)
                _frostRockPockets.AddRange(LevelGen.FrostRockPockets.Place(
                    _terrain, spawns, _hotRockPockets, Rules.FrostRockPockets, pocketSeed + 1));
            // Ore last, so it fills the ground the pockets left.
            if (Rules.SeedOre)
                _oreDeposits.AddRange(LevelGen.OreDeposits.Place(_terrain, spawns, pocketSeed + 2));

            // Let the heat (and cold) spread a little before play starts so the pockets and ore cores do not begin as
            // isolated dots.
            for (int i = 0; i < Tweaks.World.HotRockPrewarmTicks; i++)
                _terrain.CoolDown();
        }

        for (int i = 0; i < spawns.Length; i++)
        {
            var tankBase = _tankBases.GetSpawn(i);
            if (tankBase != null)
            {
                uint mixed = (uint)_simulationSeed ^ (uint)(i + 1) * 0x9e3779b9u;
                int tankSeed = unchecked((int)mixed);
                _tankList.AddTank(i, tankBase, tankSeed, _simulationSettings);
            }
        }
        _terrainRegrowth?.Survey(_terrain);
        // The world now knows whether it has a core, and so whether the sector goal runs.
        _systems = BuildSystems();
    }

    private static int SeedFromSpawns(Position[] spawns)
    {
        uint hash = 2166136261;
        foreach (var spawn in spawns)
        {
            hash = (hash ^ (uint)spawn.X) * 16777619;
            hash = (hash ^ (uint)spawn.Y) * 16777619;
        }
        return (int)hash;
    }

    public void Advance(Func<int, ControllerOutput> getInput)
    {
        long stepStart = Stopwatch.GetTimestamp();

        _advanceCount++;
        _stepEvents.Clear();
        _terrain.DropSeenChanges();
        _elapsed += _simulationSettings.AdvanceStep;

        for (int i = 0; i < _systems.Length; i++)
        {
            long systemStart = Stopwatch.GetTimestamp();
            _systems[i].Advance(getInput);
            Profile.AddSectionTime(i, Stopwatch.GetElapsedTime(systemStart));
        }
        _terrain.MarkChangesSeen();

        Profile.Total += Stopwatch.GetElapsedTime(stepStart);
        Profile.FrameCount++;

        if (Profile.FrameCount >= 100)
        {
            AuditEnergy();
            Profile.Report();
        }
    }

    /// <summary>
    /// The systems one step runs, in order. A system the world does not have (no regrowth in its rules, no core to
    /// tap) is left out, and so costs nothing. The profile reports one section per system, under its label.
    /// </summary>
    private WorldSystem[] BuildSystems()
    {
        double seconds = _simulationSettings.AdvanceStep.TotalSeconds;
        var systems = new List<WorldSystem>
        {
            new("coll", _ => _collisionSolver.Update(_tankList, _machines)),
            new("heat", _ => AdvanceHeat()),
        };
        if (_terrainRegrowth is TerrainRegrowth regrowth)
            systems.Add(new("regrow", _ => AdvanceRegrowth(regrowth)));
        systems.Add(new("proj", _ => _projectiles.Advance(_collisionSolver)));
        systems.Add(new("tanks", getInput =>
        {
            _linkMap.AgeOreFlow();
            _tankList.Advance(in _tankContext, getInput);
        }));
        systems.Add(new("machines", _ => AdvanceMachines()));
        systems.Add(new("sprites", _ => _sprites.Advance(_simulationSettings.AdvanceStep)));
        systems.Add(new("bases", _ =>
        {
            _tankBases.Advance(_terrain);
            foreach (var tankBase in _tankBases.Bases)
                tankBase.AdvanceBotTrickle(seconds);
        }));
        if (_sectorGoal is SectorGoal goal)
            systems.Add(new("goal", _ => goal.Advance(_tankBases.Bases, _machines, _tankList.Tanks, seconds)));
        systems.Add(new("match", _ => _matchResult.Latch(RaceWinner)));
        systems.Add(new("links", _ => _linkMap.Advance(_terrain, _simulationSettings.AdvanceStep)));
        systems.Add(new("dock", _ =>
        {
            foreach (var tank in _tankList.Tanks)
            {
                tank.ObserveBaseExit(_tankBases);
                tank.ObserveDocking(_stepEvents);
            }
        }));
        systems.Add(new("vision", _ => UpdateVisions()));

        Profile.SetSections(systems.Select(system => system.Label));
        return systems.ToArray();
    }

    /// <summary>One step of the heat solver, with its parts added to the profile.</summary>
    private void AdvanceHeat()
    {
        _terrain.CoolDown();
        var heat = _terrain.LastCoolDownProfile;
        Profile.CooldownSim += heat.Simulate;
        Profile.CooldownApply += heat.Apply;
        Profile.CooldownActiveTiles += heat.ActiveTiles;
        Profile.CooldownSteppedTiles += heat.SteppedTiles;
        if (heat.SteppedTiles > 0) Profile.CooldownSteppedFrames++;
        if (heat.UsedParallel) Profile.CooldownParallelFrames++;
    }

    /// <summary>Machines act, then each pylon's last hand-out is booked on its route.</summary>
    private void AdvanceMachines()
    {
        _linkMap.ClearHeatFlow();
        _machines.Advance(_machineContext, _simulationSettings.AdvanceStep);
        // Pylons act every few ticks; keep each one's last hand-out on its route so the drawn charge flow is steady.
        _linkMap.ClearChargeFlow();
        // Only chargers hand out charge; every other machine's LastChargeGiven stays zero, which carries nothing.
        foreach (var machine in _machines.Machines)
            if (machine.IsAlive)
                LinkMap.CarryCharge(machine.LinkPoint, machine.LastChargeGiven);
    }

    /// <summary>
    /// The energy balance over one report window, for <c>[ProfileE]</c>: whatever the system gained must equal what
    /// the ledger says came in from outside, and a residual means energy was created or destroyed somewhere
    /// unmetered. Summing the grid visits every cell, so it runs once per report and only when
    /// <see cref="SimulationProfile.AuditEnergy"/> asks for it, never every tick.
    /// </summary>
    private void AuditEnergy()
    {
        if (!Profile.AuditEnergy || Profile.ReportSink is null)
        {
            _auditThermalEnergy = double.NaN;
            return;
        }

        double thermalEnergy = SumSystemThermalEnergy();
        double netExternal = _terrain.Ledger.NetExternal;
        Profile.ThermalEnergy = thermalEnergy;
        Profile.ThermalLedgerResidual = double.IsNaN(_auditThermalEnergy)
            ? 0.0
            : (thermalEnergy - _auditThermalEnergy) - (netExternal - _auditNetExternalEnergy);
        _auditThermalEnergy = thermalEnergy;
        _auditNetExternalEnergy = netExternal;
    }

    private readonly List<RegrowthBlocker> _regrowthBlockers = new();

    /// <summary>Regrows dirt, except within the range of a planted relay or pylon, which keeps its ground open.</summary>
    private void AdvanceRegrowth(TerrainRegrowth regrowth)
    {
        _regrowthBlockers.Clear();
        foreach (var machine in _machines.Machines)
            if (machine.IsAlive && machine.State == MachineState.Planted && machine.Behavior.IsLinkNode)
                _regrowthBlockers.Add(new RegrowthBlocker(
                    machine.Position, Tweaks.Coverage.NodeRadius + (machine.OwnerBase?.Upgrades.CoverageBonusCells ?? 0)));
        regrowth.Advance(_terrain, _regrowthBlockers);
    }

    private readonly List<(Tank Tank, VisionMap Map)> _trackedVisions = new();
    private readonly List<VisionSource> _visionSources = new();

    /// <summary>
    /// Starts tracking what <paramref name="tank"/>'s side sees (fog of war) and returns the map, which every
    /// <see cref="Advance"/> then keeps up to date. The side sees through its tank, its base and its machines:
    /// the tank and the base along open tunnel, link nodes (relays, pylons) over their whole coverage circles, every
    /// machine a little around itself. Vision is output only: nothing in the simulation reads it, and a world nobody asks
    /// pays nothing for it.
    /// </summary>
    public VisionMap TrackVision(Tank tank)
    {
        foreach (var tracked in _trackedVisions)
            if (tracked.Tank == tank)
                return tracked.Map;
        var map = new VisionMap(_terrain);
        _trackedVisions.Add((tank, map));
        UpdateVision(tank, map);
        return map;
    }

    private readonly List<(Tank Tank, Radar Radar)> _trackedRadars = new();
    private readonly List<RadarTarget> _radarTargets = new();

    /// <summary>
    /// Starts <paramref name="tank"/>'s radar and returns it; every <see cref="Advance"/> then runs its pulses
    /// while the tank lives. It reports only what the tank's side cannot already see, so it tracks that side's vision too
    /// (<see cref="TrackVision"/>). Output only, like vision.
    /// </summary>
    public Radar TrackRadar(Tank tank)
    {
        foreach (var tracked in _trackedRadars)
            if (tracked.Tank == tank)
                return tracked.Radar;
        var radar = new Radar(TrackVision(tank));
        _trackedRadars.Add((tank, radar));
        return radar;
    }

    /// <summary>The radar <see cref="TrackRadar"/> started for <paramref name="tank"/>, or null if none was.</summary>
    public Radar? RadarOf(Tank tank)
    {
        foreach (var tracked in _trackedRadars)
            if (tracked.Tank == tank)
                return tracked.Radar;
        return null;
    }

    private void UpdateVisions()
    {
        foreach (var (tank, map) in _trackedVisions)
            UpdateVision(tank, map);

        foreach (var (tank, radar) in _trackedRadars)
        {
            if (tank.IsDead)
            {
                radar.Idle();
                continue;
            }
            // Everything that is not terrain and reflects: the other tanks and planted machines. The side's own
            // machines are left out: it knows where it put them, so their echo would tell it nothing.
            _radarTargets.Clear();
            foreach (var machine in _machines.Machines)
                if (machine.IsAlive && machine.State == MachineState.Planted &&
                    (tank.Base is null || machine.OwnerBase != tank.Base))
                    _radarTargets.Add(new RadarTarget(machine.Position, machine.BoundingBox.HalfWidth));
            foreach (var other in _tankList.Tanks)
                if (other != tank && !other.IsDead)
                    _radarTargets.Add(new RadarTarget(other.Position, Tweaks.Radar.TankHalfSize, Alive: true));
            radar.Advance(_terrain, _terrain, tank.Position, tank.Color, _radarTargets,
                tank.Base is { } home ? RadarReach.Scaled(home.Upgrades.RadarRangeScale, home.Upgrades.RadarHeatBudgetScale)
                    : RadarReach.Base);
        }
    }

    /// <summary>
    /// Hands the map everything <paramref name="tank"/>'s side sees through and the cells that changed since the map
    /// last looked. The map keeps the cast of every source that is where it was, so listing them costs next to
    /// nothing.
    /// </summary>
    private void UpdateVision(Tank tank, VisionMap map)
    {
        _visionSources.Clear();
        if (!tank.IsDead)
            _visionSources.Add(new VisionSource(tank.Position, Tweaks.Vision.TankRadius, Tweaks.Vision.TankNearRadius));
        if (tank.Base is TankBase owner)
        {
            int bonus = owner.Upgrades.CoverageBonusCells;
            const int near = Tweaks.Vision.MachineNearRadius;
            _visionSources.Add(new VisionSource(owner.Position, Tweaks.Coverage.BaseRadius + bonus, near));
            foreach (var machine in _machines.Machines)
            {
                if (!machine.IsAlive || machine.State != MachineState.Planted || machine.OwnerBase != owner)
                    continue;
                // A relay or pylon sees its whole coverage circle, through the ground, linked or not.
                int reach = machine.Behavior.IsLinkNode ? Tweaks.Coverage.NodeRadius + bonus : near;
                _visionSources.Add(new VisionSource(machine.Position, reach, reach));
            }
        }
        map.Update(_terrain, _visionSources, _terrain.GetChangeList());
    }

    // Scratch for one question about the machines near a place; no such question is asked while another is open.
    private readonly List<Machine> _nearMachines = new();

    /// <summary>The network coverage of <paramref name="tank"/>'s own base where the tank stands (DESIGN.md §4.6).</summary>
    public TankCoverage CoverageFor(Tank tank) => tank.Base == null
        ? TankCoverage.None
        : NetworkCoverage.At(tank.Position, tank.Base, _machines, _terrain, _nearMachines);

    /// <summary>Whether <paramref name="machine"/> stands in the coverage of its own base's linked network.</summary>
    private bool IsMachineOnNetwork(Machine machine) => machine.OwnerBase is TankBase owner &&
        NetworkCoverage.At(machine.Position, owner, _machines, _terrain, _nearMachines).Covered;

    /// <summary>
    /// Marks the side of spawn <paramref name="spawnIndex"/> as played by the computer: the bot does not prospect, so
    /// its base gets <see cref="Tweaks.Ore.BotOrePerSecond"/> of ore instead (a stand-in, DESIGN.md §6.4).
    /// </summary>
    public void SetBotControlled(int spawnIndex)
    {
        if (_tankBases.GetSpawn(spawnIndex) is TankBase tankBase)
            tankBase.IsBotControlled = true;
    }

    /// <summary>
    /// What would stop <paramref name="tank"/> if it stepped by <paramref name="move"/> now: the answer its own move
    /// gets (<see cref="CollisionSolver.ProbeTankStep"/>). The bot asks this instead of reading single cells.
    /// </summary>
    public StepObstacle ProbeTankStep(Tank tank, Offset move) => _collisionSolver.ProbeTankStep(tank, move);

    /// <summary>
    /// The ground temperature a tank standing at <paramref name="at"/> would exchange heat with: the average over the
    /// cells under and around it, as the tank's own heat step samples it. The bot asks this to stay off scorching ground.
    /// </summary>
    public float GroundHeatAt(Position at) =>
        _terrain.SampleAverageHeat(at, _simulationSettings.TankDigRadius) * 255f;

    /// <summary>What a machine of <paramref name="type"/> would do if planted at <paramref name="at"/>.</summary>
    public MachinePlacementPreview PreviewPlacement(MachineType type, Position at) =>
        MachineBehaviors.Get(type).RequiresLinkToPlant
            ? new MachinePlacementPreview(_linkMap.PreviewLink(at, _terrain), 0f)
            : new MachinePlacementPreview(default,
                MachineBehaviors.HarvesterSenseHeat(MachineBehaviors.HarvesterSenseTemperature(_terrain, at)));

    /// <summary>True when <paramref name="at"/> lies inside any base, the tank's own or another.</summary>
    public bool IsInsideAnyBase(Position at) => _tankBases.CheckBaseCollision(at) != null;

    /// <summary>
    /// Where the machine <paramref name="tank"/> carries would link if planted where it stands. A machine that needs
    /// no link, an empty hand, or a tank inside a base (where nothing plants) reports
    /// <see cref="LinkPreviewStatus.NoNetwork"/>.
    /// </summary>
    public LinkPreviewStatus CarriedPlantLink(Tank tank) =>
        tank.CarriedMachineType is MachineType type && !IsInsideAnyBase(tank.Position) &&
        MachineBehaviors.Get(type).RequiresLinkToPlant
            ? PreviewPlacement(type, tank.Position).Link.Status
            : LinkPreviewStatus.NoNetwork;

    /// <summary>How <paramref name="tankBase"/>'s network of harvesters and relays stands as of the last tick.</summary>
    public NetworkStatus GetNetworkStatus(TankBase tankBase) => NetworkStatus.Collect(tankBase, _machines.Machines);

    /// <summary>Fills <paramref name="into"/> with the coverage nodes of <paramref name="owner"/>'s network.</summary>
    public void CollectCoverageNodes(TankBase owner, List<CoverageNode> into) =>
        NetworkCoverage.CollectNodes(owner, _machines.Machines, into);

    /// <summary>
    /// The base whose win the match counts as a race win: the first to tap the core, when the rules make the core a
    /// race (<see cref="MatchRules.CoreTapWins"/>); otherwise null.
    /// </summary>
    private TankBase? RaceWinner => Rules.CoreTapWins ? _sectorGoal?.FirstToTapCore : null;

    /// <summary>
    /// The tank that has won, and how: the first to tap the core when <see cref="MatchRules.CoreTapWins"/>, or the
    /// only tank with lives left. Null while the match is undecided, and after a match that ended with no tank left.
    /// Once the step that decided the match has run, it never changes.
    /// </summary>
    public (Tank Tank, MatchWinReason Reason)? MatchWinner => _matchResult.Winner(RaceWinner);

    /// <summary>A match is over once a tank has won, or no tank has a life left. It stays over.</summary>
    public bool IsMatchOver => _matchResult.IsOver(RaceWinner);

    /// <summary>How the match stands for <paramref name="player"/>.</summary>
    public MatchOutcome MatchOutcomeFor(Tank player) => MatchReferee.OutcomeFor(player, MatchWinner?.Tank);

    /// <summary>
    /// The hot core, the sector goal (DESIGN.md §4.8): the large hot rock pocket at the map centre, or the nearest
    /// spot to it clear of the bases. Null when the world has no hot rock.
    /// </summary>
    public Position? CorePocket => _sectorGoal?.Core;

}


/// <summary>
/// One system of a world step (<see cref="World.Advance"/>): the label its time is reported under, and what it does
/// each step, given the step's input.
/// </summary>
internal readonly record struct WorldSystem(string Label, Action<Func<int, ControllerOutput>> Advance);
