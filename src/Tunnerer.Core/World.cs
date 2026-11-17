namespace Tunnerer.Core;

using Tunnerer.Core.Types;
using Tunnerer.Core.Config;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Collision;
using Tunnerer.Core.Input;
using Tunnerer.Core.Entities.Projectiles;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Links;
using Tunnerer.Core.Entities.Tanks;
using System.Diagnostics;
using Tunnerer.Core.Rendering;
using Tunnerer.Core.Sensing;

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
    private readonly TerrainRegrowth _terrainRegrowth;
    private readonly int _hotRockPocketCount;
    private readonly List<Position> _hotRockPockets = new();
    private Position? _corePocket;
    private readonly int _frostRockPocketCount;
    private readonly List<Position> _frostRockPockets = new();
    private readonly bool _seedOre;
    private readonly List<LevelGen.OreDeposit> _oreDeposits = new();
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
        foreach (var other in _machines.Machines)
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

    public World(
        Size terrainSize,
        int simulationSeed = 0,
        bool enableTerrainRegrowth = true,
        int hotRockPocketCount = 0,
        SimulationSettings? simulationSettings = null,
        int frostRockPocketCount = 0,
        bool seedOre = false)
    {
        _hotRockPocketCount = hotRockPocketCount;
        _seedOre = seedOre;
        _frostRockPocketCount = frostRockPocketCount;
        _simulationSeed = simulationSeed != 0 ? simulationSeed : 0x51A7E3;
        _simulationSettings = simulationSettings ?? SimulationSettings.FromTweaks();
        _terrain = new TerrainGrid(terrainSize, _simulationSettings);
        _tankBases = new TankBases(_simulationSettings);
        _projectiles = new ProjectileList(_simulationSeed ^ 0x5f3759df, _stepEvents);
        _linkMap = new LinkMap(_simulationSeed ^ 0x13579BDF);
        _collisionSolver = new CollisionSolver(_terrain);
        _terrainRegrowth = new TerrainRegrowth(enableTerrainRegrowth, _simulationSeed);
    }

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

        if (_hotRockPocketCount > 0 || _frostRockPocketCount > 0 || _seedOre)
        {
            int pocketSeed = seed + 200;
            if (_hotRockPocketCount > 0)
                _hotRockPockets.AddRange(LevelGen.HotRockPockets.Place(_terrain, spawns, _hotRockPocketCount, pocketSeed, out _corePocket));
            if (_frostRockPocketCount > 0)
                _frostRockPockets.AddRange(LevelGen.FrostRockPockets.Place(
                    _terrain, spawns, _hotRockPockets, _frostRockPocketCount, pocketSeed + 1));
            // Ore last, so it fills the ground the pockets left.
            if (_seedOre)
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
        _terrainRegrowth.Survey(_terrain);
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
        var frameWatch = Stopwatch.StartNew();

        _advanceCount++;
        _stepEvents.Clear();
        _terrain.DropSeenChanges();
        _elapsed += _simulationSettings.AdvanceStep;

        ProfileSection(ref Profile.Collision, () => _collisionSolver.Update(_tankList, _machines));
        ProfileSection(ref Profile.Cooldown, _terrain.CoolDown);
        Profile.CooldownSim += _terrain.LastCoolDownProfile.Simulate;
        Profile.CooldownApply += _terrain.LastCoolDownProfile.Apply;
        Profile.CooldownActiveTiles += _terrain.LastCoolDownProfile.ActiveTiles;
        Profile.CooldownSteppedTiles += _terrain.LastCoolDownProfile.SteppedTiles;
        if (_terrain.LastCoolDownProfile.SteppedTiles > 0) Profile.CooldownSteppedFrames++;
        if (_terrain.LastCoolDownProfile.UsedParallel) Profile.CooldownParallelFrames++;
        ProfileSection(ref Profile.Regrow, AdvanceRegrowth);
        ProfileSection(ref Profile.Projectiles, () => _projectiles.Advance(_collisionSolver));
        var simCtx = new SimulationContext
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
            Events = _stepEvents,
        };
        _linkMap.AgeOreFlow();
        ProfileSection(ref Profile.Tanks, () => _tankList.Advance(in simCtx, getInput));
        _linkMap.ClearHeatFlow();
        ProfileSection(ref Profile.Harvesters, () => _machines.Advance(
            new MachineContext(_terrain, IsMachineSpotOccupied, _machines.Machines, IsMachineOnNetwork, _stepEvents),
            _simulationSettings.AdvanceStep));
        // Pylons act every few ticks; keep each one's last hand-out on its route so the drawn charge flow is steady.
        _linkMap.ClearChargeFlow();
        // Only chargers hand out charge; every other machine's LastChargeGiven stays zero, which carries nothing.
        foreach (var machine in _machines.Machines)
            if (machine.IsAlive)
                LinkMap.CarryCharge(machine.LinkPoint, machine.LastChargeGiven);
        ProfileSection(ref Profile.Sprites, () => _sprites.Advance(_simulationSettings.AdvanceStep));
        ProfileSection(ref Profile.Bases, () => _tankBases.Advance(_terrain));
        foreach (var tankBase in _tankBases.Bases)
            tankBase.AdvanceBotTrickle(_simulationSettings.AdvanceStep.TotalSeconds);
        AdvanceSectorGoal();
        LatchMatchResult();
        ProfileSection(ref Profile.Links, () => _linkMap.Advance(_terrain, _simulationSettings.AdvanceStep));
        foreach (var tank in _tankList.Tanks)
        {
            tank.ObserveBaseExit(_tankBases);
            tank.ObserveDocking(_stepEvents);
        }
        if (_trackedVisions.Count > 0)
            ProfileSection(ref Profile.Vision, UpdateVisions);
        _terrain.MarkChangesSeen();

        Profile.Total += frameWatch.Elapsed;
        Profile.FrameCount++;

        if (Profile.FrameCount >= 100)
        {
            AuditEnergy();
            Profile.Report();
        }
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
    private void AdvanceRegrowth()
    {
        _regrowthBlockers.Clear();
        foreach (var machine in _machines.Machines)
            if (machine.IsAlive && machine.State == MachineState.Planted && machine.Behavior.IsLinkNode)
                _regrowthBlockers.Add(new RegrowthBlocker(
                    machine.Position, Tweaks.Coverage.NodeRadius + (machine.OwnerBase?.Upgrades.CoverageBonusCells ?? 0)));
        _terrainRegrowth.Advance(_terrain, _regrowthBlockers);
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

    private readonly List<CoverageNode> _coverageNodes = new();

    /// <summary>The network coverage of <paramref name="tank"/>'s own base where the tank stands (DESIGN.md §4.6).</summary>
    public TankCoverage CoverageFor(Tank tank)
    {
        if (tank.Base == null)
            return TankCoverage.None;
        NetworkCoverage.CollectNodes(tank.Base, _machines.Machines, _coverageNodes);
        return NetworkCoverage.At(tank.Position, _coverageNodes, _terrain);
    }

    private readonly List<CoverageNode> _machineCoverageNodes = new();

    /// <summary>Whether <paramref name="machine"/> stands in the coverage of its own base's linked network.</summary>
    private bool IsMachineOnNetwork(Machine machine)
    {
        if (machine.OwnerBase is not TankBase owner)
            return false;
        NetworkCoverage.CollectNodes(owner, _machines.Machines, _machineCoverageNodes);
        return NetworkCoverage.At(machine.Position, _machineCoverageNodes, _terrain).Covered;
    }

    /// <summary>
    /// Marks the side of spawn <paramref name="spawnIndex"/> as played by the computer: the bot does not prospect, so
    /// its base gets <see cref="Tweaks.Ore.BotOrePerSecond"/> of ore instead (a stand-in, DESIGN.md §6.4).
    /// </summary>
    public void SetBotControlled(int spawnIndex)
    {
        if (_tankBases.GetSpawn(spawnIndex) is TankBase tankBase)
            tankBase.IsBotControlled = true;
    }

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
    /// Whether the first base to tap the core wins the match (a race, DESIGN.md §4.8). Off by default: the core is
    /// then a goal that does not end the match. Set before the first step.
    /// </summary>
    public bool CoreTapWins { get; set; }

    /// <summary>The base that tapped the core first, once one has.</summary>
    private TankBase? _firstToTapCore;

    /// <summary>True from the step in which the match was decided; its result is then fixed.</summary>
    private bool _matchDecided;

    private (Tank Tank, MatchWinReason Reason)? _decidedWinner;

    /// <summary>
    /// The tank that has won, and how: the first to tap the core when <see cref="CoreTapWins"/>, or the only tank
    /// with lives left. Null while the match is undecided, and after a match that ended with no tank left. Once the
    /// step that decided the match has run, it never changes.
    /// </summary>
    public (Tank Tank, MatchWinReason Reason)? MatchWinner =>
        _matchDecided ? _decidedWinner : MatchReferee.Winner(_tankList.Tanks, CoreTapWins ? _firstToTapCore : null);

    /// <summary>A match is over once a tank has won, or no tank has a life left. It stays over.</summary>
    public bool IsMatchOver => _matchDecided || MatchWinner != null || MatchReferee.NobodyIsLeft(_tankList.Tanks);

    /// <summary>
    /// Records the result the first time the referee has one, so that nothing that happens afterwards (the winner
    /// dying, another base finishing its hold) can change it.
    /// </summary>
    private void LatchMatchResult()
    {
        if (_matchDecided)
            return;
        var winner = MatchWinner;
        if (winner == null && !MatchReferee.NobodyIsLeft(_tankList.Tanks))
            return;
        _decidedWinner = winner;
        _matchDecided = true;
    }

    /// <summary>How the match stands for <paramref name="player"/>.</summary>
    public MatchOutcome MatchOutcomeFor(Tank player) => MatchReferee.OutcomeFor(player, MatchWinner?.Tank);

    /// <summary>
    /// The hot core, the sector goal (DESIGN.md §4.8): the large hot rock pocket at the map centre, or the nearest
    /// spot to it clear of the bases. Null when the world has no hot rock.
    /// </summary>
    public Position? CorePocket => _corePocket;

    /// <summary>
    /// Sums the charge each base's relays near the core made this tick and advances that base's hold timer: it grows
    /// while the tap meets the goal rate and drains faster while it falls short. The base that holds it long enough
    /// has tapped the core.
    /// </summary>
    private void AdvanceSectorGoal()
    {
        if (CorePocket is not Position core)
            return;
        int radiusSq = Tweaks.Goal.CoreTapRadius * Tweaks.Goal.CoreTapRadius;
        double dt = _simulationSettings.AdvanceStep.TotalSeconds;
        foreach (var tankBase in _tankBases.Bases)
        {
            double tap = 0.0;
            foreach (var machine in _machines.Machines)
                if (machine.IsAlive && machine.Type == MachineType.Relay && machine.OwnerBase == tankBase &&
                    Position.DistanceSquared(machine.Position, core) <= radiusSq)
                    tap += machine.LastChargeMade;
            tankBase.CoreTapPerTick = tap;
            tankBase.CoreHoldSeconds = NextCoreHold(tankBase.CoreHoldSeconds, tap, dt);
            if (tankBase.CoreHoldSeconds >= Tweaks.Goal.GoalHoldSeconds)
            {
                tankBase.HasTappedCore = true;
                // Bases are visited in slot order, so two that finish on the same tick always resolve the same way.
                // A base whose tank is out of lives is out of the race, even if its hold was already running.
                if (!MatchReferee.IsEliminated(_tankList.Tanks, tankBase))
                    _firstToTapCore ??= tankBase;
            }
        }
    }

    /// <summary>
    /// One step of the core-tap hold timer: it grows by <paramref name="dt"/> while the tap meets the goal rate and
    /// drains <c>GoalHoldDecay</c> times as fast (never below zero) while it falls short.
    /// </summary>
    public static double NextCoreHold(double hold, double tapPerTick, double dt)
    {
        double goalPerTick = Tweaks.Goal.GoalChargePerSecond * dt;
        return tapPerTick >= goalPerTick ? hold + dt : Math.Max(0.0, hold - dt * Tweaks.Goal.GoalHoldDecay);
    }

    private static void ProfileSection(ref TimeSpan accumulator, Action action)
    {
        var w = Stopwatch.StartNew();
        action();
        accumulator += w.Elapsed;
    }

}

public class SimulationProfile
{
    public Action<string>? ReportSink { get; set; }

    /// <summary>
    /// Adds the <c>[ProfileE]</c> line to each report: the system's thermal energy and how far it strays from the
    /// ledger over the report window. Off by default, because it sums every cell of the map.
    /// </summary>
    public bool AuditEnergy { get; set; }

    public TimeSpan Collision;
    public TimeSpan Cooldown;
    public TimeSpan CooldownSim;
    public TimeSpan CooldownApply;
    public long CooldownActiveTiles;
    public long CooldownSteppedTiles;
    public int CooldownSteppedFrames;
    public int CooldownParallelFrames;
    public double ThermalEnergy;
    public double ThermalLedgerResidual;
    public TimeSpan Regrow;
    public TimeSpan Projectiles;
    public TimeSpan Tanks;
    public TimeSpan Harvesters;
    public TimeSpan Sprites;
    public TimeSpan Bases;
    public TimeSpan Links;
    public TimeSpan Vision;
    public TimeSpan Total;
    public int FrameCount;

    public void Report()
    {
        if (FrameCount == 0) return;
        if (ReportSink is null)
        {
            Reset();
            return;
        }

        ReportSink($"[Profile] coll={Avg(Collision):F3} cool={Avg(Cooldown):F3} regrow={Avg(Regrow):F3} " +
            $"bases={Avg(Bases):F3} proj={Avg(Projectiles):F3} tanks={Avg(Tanks):F3} harv={Avg(Harvesters):F3} " +
            $"spr={Avg(Sprites):F3} links={Avg(Links):F3} vision={Avg(Vision):F3} " +
            $"| total={Avg(Total):F3} ms (avg over {FrameCount} frames)");
        ReportSink($"[Profile+] coolSim={Avg(CooldownSim):F3} coolApply={Avg(CooldownApply):F3} ms");
        ReportSink($"[Profile++] activeTiles={AvgCount(CooldownActiveTiles):F1} steppedTiles={AvgCount(CooldownSteppedTiles):F1} " +
            $"steppedFrames={CooldownSteppedFrames}/{FrameCount} parallelFrames={CooldownParallelFrames}/{FrameCount}");
        if (AuditEnergy)
            ReportSink($"[ProfileE] thermal={ThermalEnergy:F3} ledgerResidual={ThermalLedgerResidual:F5} (over {FrameCount} frames)");
        Reset();
    }

    private double Avg(TimeSpan ts) => ts.TotalMilliseconds / FrameCount;
    private double AvgCount(long count) => FrameCount == 0 ? 0 : (double)count / FrameCount;

    public void Reset()
    {
        Collision = Cooldown = CooldownSim = CooldownApply
            = Regrow = Projectiles = Tanks = Harvesters = Sprites = Bases = Links = Vision = Total = TimeSpan.Zero;
        CooldownActiveTiles = 0;
        CooldownSteppedTiles = 0;
        CooldownSteppedFrames = 0;
        CooldownParallelFrames = 0;
        ThermalEnergy = 0;
        ThermalLedgerResidual = 0;
        FrameCount = 0;
    }
}
