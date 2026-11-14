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
using System.Diagnostics;
using Tunnerer.Core.Rendering;

public class World
{
    private readonly TerrainGrid _terrain;
    private readonly TankBases _tankBases = new();
    private readonly TankList _tankList = new();
    private readonly ProjectileList _projectiles;
    private readonly MachineList _machines = new();
    private readonly LinkMap _linkMap;
    private readonly SpriteList _sprites = new();
    private readonly CollisionSolver _collisionSolver;
    private readonly TerrainRegrowth _terrainRegrowth;
    private readonly bool _deterministicSimulation;
    private readonly int _simulationSeed;
    private readonly SimulationSettings _simulationSettings;

    private int _advanceCount;
    private TimeSpan _elapsed;
    private bool _gameOver;
    private double _lastThermalEnergy;

    public TerrainGrid Terrain => _terrain;
    public TankBases TankBases => _tankBases;
    public TankList TankList => _tankList;
    public ProjectileList Projectiles => _projectiles;
    public MachineList Machines => _machines;
    public LinkMap LinkMap => _linkMap;
    public SpriteList Sprites => _sprites;
    public CollisionSolver CollisionSolver => _collisionSolver;
    public int AdvanceCount => _advanceCount;
    public bool IsGameOver => _gameOver;
    public SimulationSettings Settings => _simulationSettings;

    public SimulationProfile Profile { get; } = new();

    public World(
        Size terrainSize,
        bool deterministicSimulation = false,
        int simulationSeed = 0,
        bool enableTerrainRegrowth = true,
        SimulationSettings? simulationSettings = null)
    {
        _deterministicSimulation = deterministicSimulation;
        _simulationSeed = simulationSeed != 0 ? simulationSeed : 0x51A7E3;
        _simulationSettings = simulationSettings ?? SimulationSettings.FromTweaks();
        _terrain = new TerrainGrid(terrainSize, _simulationSettings);
        _projectiles = new ProjectileList(deterministicSimulation ? _simulationSeed ^ 0x5f3759df : null);
        _linkMap = new LinkMap(deterministicSimulation, _simulationSeed ^ 0x13579BDF);
        _collisionSolver = new CollisionSolver(_terrain);
        _terrainRegrowth = new TerrainRegrowth(deterministicSimulation, enableTerrainRegrowth, _simulationSeed);
    }

    public void Initialize(TerrainGrid generatedTerrain, Position[] spawns,
        int? decorationSeed = null, bool parallelMaterialize = false)
    {
        for (int i = 0; i < generatedTerrain.Size.Area; i++)
            _terrain[i] = generatedTerrain[i];

        _terrain.MaterializeTerrain(parallel: parallelMaterialize);
        _terrain.DecorateTerrain(decorationSeed.HasValue ? decorationSeed.Value + 100 : null);

        for (int i = 0; i < spawns.Length; i++)
            _tankBases.AddBase(spawns[i], i);

        _tankBases.CreateBasesInTerrain(_terrain);

        for (int i = 0; i < spawns.Length; i++)
        {
            var tankBase = _tankBases.GetSpawn(i);
            if (tankBase != null)
            {
                uint mixed = (uint)_simulationSeed ^ (uint)(i + 1) * 0x9e3779b9u;
                int tankSeed = unchecked((int)mixed);
                _tankList.AddTank(i, tankBase,
                    _deterministicSimulation ? tankSeed : 0,
                    _simulationSettings);
            }
        }
    }

    public void Advance(Func<int, ControllerOutput> getInput)
    {
        if (_gameOver) return;

        var frameWatch = Stopwatch.StartNew();

        _advanceCount++;
        _elapsed += _simulationSettings.AdvanceStep;

        ProfileSection(ref Profile.Collision, () => _collisionSolver.Update(_tankList, _machines));
        ProfileSection(ref Profile.Cooldown, () => _terrain.CoolDown(_simulationSettings.ThermalArtificialCoolingPerFrame));
        Profile.CooldownPrep += _terrain.LastCoolDownProfile.Prep;
        Profile.CooldownSim += _terrain.LastCoolDownProfile.Simulate;
        Profile.CooldownMark += _terrain.LastCoolDownProfile.MarkDirty;
        Profile.CooldownWriteBack += _terrain.LastCoolDownProfile.WriteBack;
        Profile.CooldownActiveTiles += _terrain.LastCoolDownProfile.ActiveTiles;
        Profile.CooldownRegionCount += _terrain.LastCoolDownProfile.RegionCount;
        if (_terrain.LastCoolDownProfile.UsedSparse) Profile.CooldownSparseFrames++;
        if (_terrain.LastCoolDownProfile.UsedParallel) Profile.CooldownParallelFrames++;
        double thermalEnergy = _terrain.SumTotalThermalEnergy();
        Profile.ThermalEnergy += thermalEnergy;
        if (_advanceCount > 1)
        {
            double drift = thermalEnergy - _lastThermalEnergy;
            Profile.ThermalEnergyDriftSigned += drift;
            Profile.ThermalEnergyDriftAbs += Math.Abs(drift);
        }
        _lastThermalEnergy = thermalEnergy;

        ProfileSection(ref Profile.Regrow, () => _terrainRegrowth.Advance(_terrain, _advanceCount));
        ProfileSection(ref Profile.Projectiles, () => _projectiles.Advance(_collisionSolver));
        var simCtx = new SimulationContext
        {
            Terrain = _terrain,
            Heat = _terrain,
            Projectiles = _projectiles,
            CheckBaseCollision = _tankBases.CheckBaseCollision,
            Settings = _simulationSettings,
        };
        ProfileSection(ref Profile.Tanks, () => _tankList.Advance(in simCtx, getInput));
        ProfileSection(ref Profile.Harvesters, () => _machines.Advance(_terrain, _simulationSettings.AdvanceStep));
        ProfileSection(ref Profile.Sprites, () => _sprites.Advance(_simulationSettings.AdvanceStep));
        ProfileSection(ref Profile.Bases, () => _tankBases.Advance(_terrain));
        ProfileSection(ref Profile.Links, () => _linkMap.Advance(_terrain, _simulationSettings.AdvanceStep));

        Profile.Total += frameWatch.Elapsed;
        Profile.FrameCount++;

        if (Profile.FrameCount >= 100)
            Profile.Report();
    }

    public void SetGameOver() => _gameOver = true;

    public TerrainRenderSnapshot CaptureTerrainRenderSnapshot() => new(_terrain);

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

    public TimeSpan Collision;
    public TimeSpan Cooldown;
    public TimeSpan CooldownPrep;
    public TimeSpan CooldownSim;
    public TimeSpan CooldownMark;
    public TimeSpan CooldownWriteBack;
    public long CooldownActiveTiles;
    public long CooldownRegionCount;
    public int CooldownSparseFrames;
    public int CooldownParallelFrames;
    public double ThermalEnergy;
    public double ThermalEnergyDriftSigned;
    public double ThermalEnergyDriftAbs;
    public TimeSpan Regrow;
    public TimeSpan Projectiles;
    public TimeSpan Tanks;
    public TimeSpan Harvesters;
    public TimeSpan Sprites;
    public TimeSpan Bases;
    public TimeSpan Links;
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
            $"spr={Avg(Sprites):F3} links={Avg(Links):F3} " +
            $"| total={Avg(Total):F3} ms (avg over {FrameCount} frames)");
        ReportSink($"[Profile+] coolPrep={Avg(CooldownPrep):F3} coolSim={Avg(CooldownSim):F3} " +
            $"coolMark={Avg(CooldownMark):F3} coolCopy={Avg(CooldownWriteBack):F3} ms");
        ReportSink($"[Profile++] activeTiles={AvgCount(CooldownActiveTiles):F1} regions={AvgCount(CooldownRegionCount):F1} " +
            $"sparseFrames={CooldownSparseFrames}/{FrameCount} parallelFrames={CooldownParallelFrames}/{FrameCount}");
        ReportSink($"[ProfileE] thermal={AvgValue(ThermalEnergy):F3} driftSigned={AvgValue(ThermalEnergyDriftSigned):F5} " +
            $"driftAbs={AvgValue(ThermalEnergyDriftAbs):F5}");
        Reset();
    }

    private double Avg(TimeSpan ts) => ts.TotalMilliseconds / FrameCount;
    private double AvgCount(long count) => FrameCount == 0 ? 0 : (double)count / FrameCount;
    private double AvgValue(double value) => FrameCount == 0 ? 0 : value / FrameCount;

    public void Reset()
    {
        Collision = Cooldown = CooldownPrep = CooldownSim = CooldownMark = CooldownWriteBack
            = Regrow = Projectiles = Tanks = Harvesters = Sprites = Bases = Links = Total = TimeSpan.Zero;
        CooldownActiveTiles = 0;
        CooldownRegionCount = 0;
        CooldownSparseFrames = 0;
        CooldownParallelFrames = 0;
        ThermalEnergy = 0;
        ThermalEnergyDriftSigned = 0;
        ThermalEnergyDriftAbs = 0;
        FrameCount = 0;
    }
}
