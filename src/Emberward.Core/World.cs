namespace Emberward.Core;

using Emberward.Core.Snapshots;
using Emberward.Core.Types;
using Emberward.Core.Config;
using Emberward.Core.Terrain;
using Emberward.Core.Entities;
using Emberward.Core.Collision;
using Emberward.Core.Input;
using Emberward.Core.Entities.Projectiles;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Tanks;
using System.Diagnostics;
using Emberward.Core.Rendering;
using Emberward.Core.Sensing;
using Emberward.Core.Match;
using Emberward.Core.Network;
using Emberward.Core.Regrowth;
using Emberward.Core.Entities.Bases;

public partial class World
{
    private readonly TerrainGrid _terrain;
    private readonly TankBases _tankBases;
    private readonly TankList _tankList = new();
    private readonly ProjectileList _projectiles;
    private readonly MachineList _machines = new();
    private readonly LinkMap _linkMap;
    private readonly CollisionSolver _collisionSolver;
    private readonly TerrainRegrowth? _terrainRegrowth;
    private readonly List<Position> _hotRockPockets = new();
    private readonly List<Position> _frostRockPockets = new();
    private readonly List<LevelGen.OreDeposit> _oreDeposits = new();
    private readonly MatchResult _matchResult;
    private SectorGoal? _sectorGoal;
    private readonly EnergyNetwork? _energy;
    private readonly BasePowerSystem? _basePower;
    private readonly MachineUpgradeSystem? _machineUpgrades;
    private readonly TankReserveSystem? _reserve;
    // What tanks and machines see of the world each step; the same every step, so built once.
    [NotSaved("fixed for the match: closures over this world")]
    private readonly SimulationContext _tankContext;
    [NotSaved("fixed for the match: closures over this world")]
    private readonly MachineContext _machineContext;
    [NotSaved("fixed for the match: the step's systems, chosen by the rules")]
    private WorldSystem[] _systems;
    private readonly int _simulationSeed;
    private readonly SimulationSettings _simulationSettings;
    private readonly StepEventLog _stepEvents = new();
    private readonly SideSensing _sensing;

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
    /// Charge stored in bases, capacitors and generator reserves, with what the energy network has paid out and not
    /// yet turned into heat (<see cref="EnergyNetwork.StoredCharge"/>). Charge is converted heat, so
    /// <c>SumSystemThermalEnergy() + SumSystemCharge()</c> balances against <c>Injected - TotalSunk</c>.
    /// </summary>
    public double SumSystemCharge()
    {
        double total = 0.0;
        foreach (var tankBase in _tankBases.Bases)
            total += tankBase.Charge;
        foreach (var machine in _machines.Machines)
            total += machine.Charge;
        if (_energy != null)
            total += _energy.StoredCharge();
        return total;
    }

    public TankList TankList => _tankList;
    public ProjectileList Projectiles => _projectiles;
    public MachineList Machines => _machines;
    public LinkMap LinkMap => _linkMap;
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
            if (ReferenceEquals(other, machine) || !other.IsStanding) continue;
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
        // On the energy network every machine has its whole capacitor, filled from its base's store so no charge
        // appears from nothing. Without it no machine has a capacitor.
        if (_energy != null)
        {
            machine.ChargeCapacity = machine.Behavior.Capacitor.Capacity;
            if (owner is { IsDestroyed: false } && _basePower?.IsDark(owner) != true)
                machine.Charge += owner.DrawCharge(machine.ChargeCapacity - machine.Charge);
        }
        if (machine.Behavior.IsLinkNode)
            machine.LinkPoint = _linkMap.RegisterPoint(machine.Position, LinkPointType.Machine);
        _energy?.Register(machine);
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
        if (Rules.EnergyNetwork)
        {
            _energy = new EnergyNetwork(_simulationSettings);
            _basePower = new BasePowerSystem();
        }
        if (Rules.MachineUpgrades)
            _machineUpgrades = new MachineUpgradeSystem(_machines, _energy, _terrain, _stepEvents, Rules);
        if (Rules.TankReserve)
            _reserve = new TankReserveSystem(_machines, _terrain, _energy);
        _projectiles = new ProjectileList(_simulationSeed ^ 0x5f3759df, _stepEvents, _basePower);
        _linkMap = new LinkMap(_simulationSeed ^ 0x13579BDF);
        _collisionSolver = new CollisionSolver(_terrain, _tankBases, _basePower);
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
            Rules = Rules,
            BasePower = _basePower,
            Machines = _machines,
            PlantMachine = PlantMachine,
            CanPlantRelayAt = (pos, owner) => PreviewMachineLink(pos, owner).Status == LinkPreviewStatus.Live,
            CoverageFor = CoverageFor,
            IsOccupied = IsCellOccupied,
            IsTankBlocked = _collisionSolver.BlocksTank,
            Events = _stepEvents,
            MachineUpgrades = _machineUpgrades,
            Reserve = _reserve,
        };
        _sensing = new SideSensing(_terrain, _machines, _tankList, _basePower, _machineUpgrades, _reserve);
        _machineContext = new MachineContext(_terrain, IsMachineSpotOccupied, _machines, MachineCoverage, _stepEvents, _energy,
            _basePower, _machineUpgrades, _tankList, _projectiles);
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
        {
            // The tank spawns inside the gate that faces into the map.
            TankBase tankBase = _tankBases.AddBase(spawns[i], i, BaseLayout.FrontOf(_terrain.Size, spawns[i]));
            _basePower?.Register(tankBase);
        }

        _tankBases.CreateBasesInTerrain(_terrain, gatesShut: _basePower != null);
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
                    _sectorGoal = new SectorGoal(corePosition, _tankBases.Bases);
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
        if (_basePower is { } basePower)
            systems.Add(new("blackout", _ => basePower.LatchBlackouts()));
        systems.Add(new("proj", _ => _projectiles.Advance(_collisionSolver)));
        systems.Add(new("tanks", getInput =>
        {
            _linkMap.AgeOreFlow();
            _tankList.Advance(in _tankContext, getInput);
            _basePower?.LatchBlackouts();
        }));
        if (_energy is EnergyNetwork network)
            systems.Add(new("islands", _ => network.Refresh(_linkMap, _tankBases.Bases)));
        systems.Add(new("machines", _ => AdvanceMachines()));
        if (_energy is EnergyNetwork energy)
            systems.Add(new("power", _ => energy.Advance(_terrain, _basePower, _stepEvents)));
        systems.Add(new("bases", _ =>
        {
            _tankBases.Advance(_terrain, _basePower);
            _basePower?.Advance(_terrain, _stepEvents);
            foreach (var tankBase in _tankBases.Bases)
                tankBase.AdvanceBotTrickle(seconds);
        }));
        if (_sectorGoal is SectorGoal goal)
            systems.Add(new("goal", _ => goal.Advance(_machines, seconds)));
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
        systems.Add(new("vision", _ => _sensing.Advance()));

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

    /// <summary>Machines act; relays book the heat they pump on their routes.</summary>
    private void AdvanceMachines()
    {
        _linkMap.ClearHeatFlow();
        _machines.Advance(_machineContext, _simulationSettings.AdvanceStep);
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

    /// <summary>Regrows dirt, except within the range of a planted link node, which keeps its ground open.</summary>
    private void AdvanceRegrowth(TerrainRegrowth regrowth)
    {
        _regrowthBlockers.Clear();
        foreach (var machine in _machines.Machines)
            if (machine.IsStanding && machine.Behavior.IsLinkNode)
                _regrowthBlockers.Add(new RegrowthBlocker(
                    machine.Position, machine.OwnerBase?.Upgrades.NodeCoverageRadius ?? Tweaks.Coverage.NodeRadius));
        regrowth.Advance(_terrain, _regrowthBlockers);
    }

    /// <summary>
    /// Starts tracking what <paramref name="tank"/>'s side sees (fog of war) and returns the map, which every
    /// <see cref="Advance"/> then keeps up to date (<see cref="SideSensing.TrackVision"/>). Output only.
    /// </summary>
    public VisionMap TrackVision(Tank tank) => _sensing.TrackVision(tank);

    /// <summary>
    /// Starts <paramref name="tank"/>'s radar and returns it (<see cref="SideSensing.TrackRadar"/>); it tracks that
    /// side's vision too. Output only, like vision.
    /// </summary>
    public Radar TrackRadar(Tank tank) => _sensing.TrackRadar(tank);

    /// <summary>The radar <see cref="TrackRadar"/> started for <paramref name="tank"/>, or null if none was.</summary>
    public Radar? RadarOf(Tank tank) => _sensing.RadarOf(tank);

    // Scratch for one question about the machines near a place; no such question is asked while another is open.
    private readonly List<Machine> _nearMachines = new();

    /// <summary>The network coverage of <paramref name="tank"/>'s own base where the tank stands (DESIGN.md §4.6).</summary>
    public TankCoverage CoverageFor(Tank tank) => tank.Base is not { IsDestroyed: false }
        ? TankCoverage.None
        : NetworkCoverage.At(tank.Position, tank.Base, _machines, _terrain, _nearMachines);

    /// <summary>The coverage of <paramref name="machine"/>'s own base's linked network where it stands.</summary>
    private TankCoverage MachineCoverage(Machine machine) => machine.OwnerBase is TankBase { IsDestroyed: false } owner
        ? NetworkCoverage.At(machine.Position, owner, _machines, _terrain, _nearMachines, requireOwnPowerSource: _energy != null)
        : TankCoverage.None;

    /// <summary>
    /// Marks the side of spawn <paramref name="spawnIndex"/> as played by the computer, or by a player again: the bot
    /// does not prospect, so while it plays, its base gets <see cref="Tweaks.Ore.BotOrePerSecond"/> of ore instead (a
    /// stand-in, DESIGN.md §6.4). Part of the simulation: in a network match every client changes it at the same tick.
    /// </summary>
    public void SetBotControlled(int spawnIndex, bool botControlled)
    {
        if (_tankBases.GetSpawn(spawnIndex) is TankBase tankBase)
            tankBase.SetBotControlled(botControlled);
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
    public MachinePlacementPreview PreviewPlacement(MachineType type, Position at, TankBase? owner = null) =>
        MachineBehaviors.Get(type).RequiresLinkToPlant
            ? new MachinePlacementPreview(PreviewMachineLink(at, owner), 0f)
            : MachineBehaviors.Get(type).IsMiner ? new MachinePlacementPreview(default,
                MachineBehaviors.HarvesterSenseHeat(MachineBehaviors.HarvesterSenseTemperature(_terrain, at)))
            : MachineBehaviors.Get(type).IsTurret ? new MachinePlacementPreview(default,
                TurretAction.SenseTemperature(_terrain, at) / Tweaks.Turret.MaxSlowTemperature) : default;

    private LinkPreview PreviewMachineLink(Position at, TankBase? owner)
    {
        var preview = _linkMap.PreviewLink(at, _terrain);
        if (preview.Status != LinkPreviewStatus.Live && owner != null &&
            _energy?.LocalLinkAt(at, owner, _machines, _terrain, _nearMachines) is LinkPoint local)
            return new LinkPreview(LinkPreviewStatus.Live, local.Position, default);
        return preview;
    }

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
            ? PreviewPlacement(type, tank.Position, tank.Base).Link.Status
            : LinkPreviewStatus.NoNetwork;

    /// <summary>How <paramref name="tankBase"/>'s network of harvesters and relays stands as of the last tick.</summary>
    public NetworkStatus GetNetworkStatus(TankBase tankBase) => NetworkStatus.Collect(tankBase, _machines.Machines, IsBaseDark(tankBase));

    /// <summary>Fills <paramref name="into"/> with the coverage nodes of <paramref name="owner"/>'s network.</summary>
    public void CollectCoverageNodes(TankBase owner, List<CoverageNode> into) =>
        NetworkCoverage.CollectNodes(owner, _machines.Machines, into);

    /// <summary>
    /// The base whose win the match counts as a race win: the first to tap the core, when the rules make the core a
    /// race (<see cref="MatchRules.CoreTapWins"/>); otherwise null.
    /// </summary>
    /// <summary>The race to the core, for the sync digest; null in a world without a core.</summary>
    internal SectorGoal? Goal => _sectorGoal;

    /// <summary>The match result, for the sync digest.</summary>
    internal MatchResult Result => _matchResult;

    /// <summary>
    /// The energy network (DESIGN.md §4.9); null when the match rules leave it out. Desktop asks
    /// <see cref="MachinePowerOf"/> and <see cref="BasePowerOf"/> instead.
    /// </summary>
    public EnergyNetwork? Energy => _energy;

    /// <summary>
    /// A machine's capacitor as an overlay shows it: charge now, capacity, and charge a second in and out over the last
    /// second (DESIGN.md §4.9). Null when the rules run no energy network or the machine is not planted. Output only.
    /// </summary>
    public MachinePower? MachinePowerOf(Machine machine) => _energy?.PowerOf(machine);

    /// <summary>
    /// A base's charge store as an overlay shows it: level, capacity, and what its engine and linked generators made and its island drew a
    /// second over the last second. Null when the rules run no energy network. Output only.
    /// </summary>
    public BasePower? BasePowerOf(TankBase tankBase) => _energy is { } energy
        ? energy.PowerOf(tankBase) with
        {
            IsDark = IsBaseDark(tankBase),
            ShieldFill = _basePower?.ShieldFill(tankBase) ?? 0.0,
            GatesShut = AreGatesShut(tankBase),
        }
        : null;

    public bool IsBaseDark(TankBase tankBase) => _basePower?.IsDark(tankBase) == true;

    /// <summary>
    /// Whether <paramref name="tankBase"/>'s gates are shut to the enemy: its gate shield holds (DESIGN.md §4.9).
    /// Always false when the rules run no energy network, where a gate stops nothing.
    /// </summary>
    public bool AreGatesShut(TankBase tankBase) => _basePower?.GatesShut(tankBase) == true;

    /// <summary>
    /// What <paramref name="tank"/> could buy for the nearest of its machines in reach (DESIGN.md §4.9): the machine, its
    /// upgrade so far and the next price. Null when the rules run no machine upgrades or nothing is in reach.
    /// </summary>
    public MachineUpgradeOffer? MachineUpgradeOfferFor(Tank tank) => _machineUpgrades?.OfferFor(tank);

    /// <summary>The path <paramref name="machine"/> took and its level; no path and level 0 without machine upgrades.</summary>
    public MachineUpgradeLevel MachineUpgradeOf(Machine machine) => _machineUpgrades?.LevelOf(machine) ?? default;

    /// <summary>Why <paramref name="tank"/>'s last machine upgrade press bought nothing, while that is still worth saying.</summary>
    public (MachineUpgradeRefusal Refusal, int Ticks) MachineUpgradeRefusalOf(Tank tank) =>
        _machineUpgrades?.RefusalOf(tank) ?? (MachineUpgradeRefusal.None, 0);

    /// <summary>Per-machine upgrades, for the hashes; null when the match rules leave them out.</summary>
    internal MachineUpgradeSystem? UpgradeSystem => _machineUpgrades;

    internal ulong MixBasePower(ulong hash) => _basePower?.MixState(hash) ?? hash;

    /// <summary>Dirt regrowth, for the sync digest; null when the match rules leave it out.</summary>
    internal TerrainRegrowth? RegrowthSystem => _terrainRegrowth;

    private TankBase? RaceWinner => Rules.CoreTapWins ? _sectorGoal?.FirstToTapCore : null;

    /// <summary>
    /// The tank that has won, and how: the first to tap the core when <see cref="MatchRules.CoreTapWins"/>, or the
    /// only side with an intact base. Null while undecided or after simultaneous elimination.
    /// Once the step that decided the match has run, it never changes.
    /// </summary>
    public (Tank Tank, MatchWinReason Reason)? MatchWinner => _matchResult.Winner(RaceWinner);

    /// <summary>A match is over once a side has won or every base has been destroyed. It stays over.</summary>
    public bool IsMatchOver => _matchResult.IsOver(RaceWinner);

    /// <summary>How the match stands for <paramref name="player"/>.</summary>
    public MatchOutcome MatchOutcomeFor(Tank player) => MatchReferee.OutcomeFor(player, MatchWinner?.Tank);

    /// <summary>
    /// The hot core, the sector goal (DESIGN.md §4.8): the large hot rock pocket at the map centre, or the nearest
    /// spot to it clear of the bases. Null when the world has no hot rock.
    /// </summary>
    public Position? CorePocket => _sectorGoal?.Core;

    /// <summary>
    /// How <paramref name="tankBase"/> stands on the sector goal: its tap, its hold timer and whether it has tapped
    /// the core. Null in a world without a core, and for a null or foreign base.
    /// </summary>
    public CoreTap? CoreTapOf(TankBase? tankBase) => tankBase is null ? null : _sectorGoal?.TapOf(tankBase);

}


/// <summary>
/// One system of a world step (<see cref="World.Advance"/>): the label its time is reported under, and what it does
/// each step, given the step's input.
/// </summary>
internal readonly record struct WorldSystem(string Label, Action<Func<int, ControllerOutput>> Advance);
