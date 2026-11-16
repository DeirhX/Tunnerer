namespace Tunnerer.Core.Entities.Tanks;

using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Links;
using Tunnerer.Core.Resources;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Thermal;
using Tunnerer.Core.Types;

/// <summary>
/// The tank's heat: work heat, the per-tick exchange with the grid, base sinks and refrigeration, network coverage
/// and its heat floor, and booking every joule created or destroyed in the <see cref="ThermalLedger"/>.
/// The heat value itself lives on <see cref="Tank.Heat"/>; this class owns the rules that change it.
/// </summary>
internal sealed class TankThermals
{
    private readonly Tank _tank;
    private readonly SimulationSettings _settings;
    private readonly TankHeatEngine _heatEngine;
    private readonly float _baseTemperature;
    private int _frameDugPixels;
    private bool _frameShotFired;

    public TankThermals(Tank tank, SimulationSettings settings)
    {
        _tank = tank;
        _settings = settings;
        _heatEngine = new TankHeatEngine(settings);
        _baseTemperature = settings.ThermalMaterials[ThermalMaterial.Base].FixedTemperature.GetValueOrDefault();
    }

    public float ThermalCapacity => _heatEngine.TankHeatCapacity;
    public TankCoverage Coverage { get; private set; }
    public bool IsOffNetwork { get; private set; }
    public float OffNetworkFloor { get; private set; }
    public bool IsOverheating { get; private set; }
    public bool IsFreezing { get; private set; }

    public void BeginTick()
    {
        IsOverheating = false;
        IsFreezing = false;
    }

    /// <summary>Terrain dug with the torch this tick; it heats the tank in <see cref="Advance"/>.</summary>
    public void RecordDugPixels(int count) => _frameDugPixels += count;

    /// <summary>A shot fired this tick; it heats the tank in <see cref="Advance"/>.</summary>
    public void RecordShot() => _frameShotFired = true;

    public void AddIdleHeat(ThermalLedger ledger)
    {
        _tank.Heat += _settings.TankIdleHeatGain;
        ledger.RecordInjected(_settings.TankIdleHeatGain * ThermalCapacity);
        SyncReactorHeat();
    }

    public void AddMoveHeat(ThermalLedger ledger)
    {
        _tank.Heat += _settings.TankMoveHeatGain;
        ledger.RecordInjected(_settings.TankMoveHeatGain * ThermalCapacity);
    }

    /// <summary>
    /// A base the tank is in destroys heat around it (a sink, not a transfer), more near its centre and more at the
    /// tank's own base. Only heat that is actually present can be removed; what was removed goes to the ledger.
    /// </summary>
    public void SinkAtBase(ITerrainHeatSource heat, TankBase baseColl, bool atOwnBase)
    {
        var position = _tank.Position;
        int baseCooldown = atOwnBase ? _settings.BaseHomeCooldownHeat : _settings.BaseForeignCooldownHeat;
        if (baseCooldown <= 0)
            return;

        int half = Math.Max(1, _settings.BaseSize / 2);
        int dist2 = Position.DistanceSquared(position, baseColl.Position);
        float radial = 1f - MathF.Sqrt(dist2) / half;
        float centerWeight = Math.Clamp(radial, 0f, 1f);
        float centerBonus = 1f + _settings.TankHeatBaseCoolBonus * centerWeight;
        int sinkAmount = (int)MathF.Round(Math.Max(0f, baseCooldown * centerBonus));
        if (sinkAmount > 0)
        {
            double terrainShare = Math.Round(sinkAmount * 0.40);
            double airShare = sinkAmount - terrainShare;
            int radius = Math.Max(1, _settings.TankDigRadius);
            double removed =
                heat.ExtractEnergyInRadiusArea(position, radius, terrainShare, TerrainGrid.ThermalField.Terrain) +
                heat.ExtractEnergyInRadiusArea(position, radius, airShare, TerrainGrid.ThermalField.Air);
            heat.Ledger.RecordSunkBase(removed);
        }
    }

    /// <summary>
    /// A tank docked at its own base has a share of its heat above the base temperature pumped into the base reservoir
    /// each tick (<c>DockHeatPumpRate</c>), so it cools fast while hot and levels off near 0, never below. This is an
    /// exact transfer (the tank cools, the reservoir fills), and it stops when the reservoir is full. A cold tank is
    /// warmed back toward 0 by ordinary conduction with the pinned base floor.
    /// </summary>
    public void PumpHeatAtBase(TankBase tankBase)
    {
        float excess = _tank.Heat - _baseTemperature;
        if (excess <= 0f)
            return;

        double wanted = excess * _settings.TankDockHeatPumpRate * (double)ThermalCapacity;
        double accepted = tankBase.AcceptTankHeat(wanted);
        _tank.Heat -= (float)(accepted / ThermalCapacity);
    }

    private float OffNetworkHeatPerTick(SimulationSettings settings) =>
        settings.TankOffNetworkHeatPerTick * (_tank.Base?.Upgrades.OffNetworkHeatFactor ?? 1f);

    /// <summary>
    /// Network coverage (DESIGN.md §4.6). Inside its base's coverage the tank sheds heat down the links into the base
    /// reactor (an exact transfer, limited by the reactor's room, never below zero heat),
    /// and its heat floor falls at the same rate. Outside it, the floor rises at a steady rate and the tank cannot cool
    /// below it (<see cref="Advance"/>), so the time it can stay out is set by how far the floor has to climb.
    /// </summary>
    public void AdvanceNetworkCoverage(in SimulationContext ctx)
    {
        IsOffNetwork = false;
        var tankBase = _tank.Base;
        if (ctx.CoverageFor == null || tankBase == null)
        {
            Coverage = TankCoverage.None;
            return;
        }

        Coverage = ctx.CoverageFor(_tank);
        if (ctx.CheckBaseCollision(_tank.Position) != null)
        {
            // Docked or passing through a base: the base itself handles cooling, so the network is not cooling the tank.
            Coverage = TankCoverage.None;
            OffNetworkFloor = Math.Min(_tank.Heat, 0f);
            return;
        }

        float capacity = ThermalCapacity;
        if (Coverage.Covered)
        {
            float cooling = ctx.Settings.TankNetworkCoolingPerTick * Coverage.Strength;
            // The floor falls at the network's cooling rate (so a moment in coverage is not a full reset), but never
            // below the tank's own sub-zero heat: coverage only takes heat away, it does not bank cold.
            OffNetworkFloor = Math.Min(Math.Max(OffNetworkFloor - cooling, Math.Min(_tank.Heat, 0f)), _tank.Heat);
            if (_tank.Heat <= 0f)
                return;
            double wanted = Math.Min(cooling, _tank.Heat) * (double)capacity;
            double accepted = tankBase.AcceptTankHeat(wanted);
            _tank.Heat -= (float)(accepted / capacity);
        }
        else
        {
            IsOffNetwork = true;
            OffNetworkFloor = Math.Min(OffNetworkFloor, _tank.Heat) + OffNetworkHeatPerTick(ctx.Settings);
        }
    }

    /// <summary>A cooling pickup (gold) takes heat out of the tank; it never chills the tank below ambient.</summary>
    public void CoolFromPickup(int cooling, ThermalLedger ledger)
    {
        float cooledHeat = Math.Max(_tank.Heat - cooling, Math.Min(_tank.Heat, 0f));
        ledger.RecordSunkTank((_tank.Heat - cooledHeat) * ThermalCapacity);
        _tank.Heat = cooledHeat;
    }

    /// <summary>
    /// The end-of-tick heat step: action heat from digging and shooting, exchange with the terrain and air under the
    /// tank, overheat and frostbite damage, and the off-network floor.
    /// </summary>
    public void Advance(ITerrainHeatSource heat)
    {
        var position = _tank.Position;
        int radius = _settings.TankDigRadius;
        float sampledTerrainTemperature = heat.SampleAverageHeat(position, radius) * 255f;
        float sampledAirTemperature = heat.SampleAverageAirTemperature(position, radius);
        var step = _heatEngine.Advance(
            currentHeat: _tank.Heat,
            frameDugPixels: _frameDugPixels,
            frameShotFired: _frameShotFired,
            sampledTerrainTemperature: sampledTerrainTemperature,
            sampledAirTemperature: sampledAirTemperature,
            terrainCapacity: heat.SumHeatCapacityInRadiusArea(position, radius, TerrainGrid.ThermalField.Terrain),
            airCapacity: heat.SumHeatCapacityInRadiusArea(position, radius, TerrainGrid.ThermalField.Air));

        // Exchange with the grid is an exact transfer both ways: whatever the tank lost, the cells under it gained, and
        // a cold tank draws heat from its surroundings.
        float nextHeat = step.NextHeat;
        nextHeat -= ExchangeWithGrid(heat, position, radius, step.EnergyToTerrain, TerrainGrid.ThermalField.Terrain);
        nextHeat -= ExchangeWithGrid(heat, position, radius, step.EnergyToAir, TerrainGrid.ThermalField.Air);

        // Digging and shooting create heat from nothing.
        heat.Ledger.RecordInjected(step.ActionHeat * ThermalCapacity);

        if (step.OverheatDamage > 0)
        {
            IsOverheating = true;
            _tank.TakeDamage(step.OverheatDamage, DamageCause.Overheat);
        }
        if (step.FrostbiteDamage > 0)
        {
            IsFreezing = true;
            _tank.TakeDamage(step.FrostbiteDamage, DamageCause.Frostbite);
        }

        // Off network the tank cannot cool below its rising heat floor by shedding into ordinary ground; holding it there
        // is reactor heat, created here. Sub-zero surroundings (a frost pocket) are a real cold sink, though: there the
        // floor follows the tank down, so cooling off at a frost pocket buys back time.
        if (IsOffNetwork && (sampledTerrainTemperature < 0f || sampledAirTemperature < 0f))
            OffNetworkFloor = Math.Min(OffNetworkFloor, nextHeat);
        if (IsOffNetwork && nextHeat < OffNetworkFloor)
        {
            heat.Ledger.RecordInjected((OffNetworkFloor - nextHeat) * (double)ThermalCapacity);
            nextHeat = OffNetworkFloor;
        }

        _tank.Heat = nextHeat;
        SyncReactorHeat();

        _frameDugPixels = 0;
        _frameShotFired = false;
    }

    /// <summary>A fresh tank starts cold: whatever heat the wreck still held is removed from the system.</summary>
    public void ResetForSpawn(ThermalLedger? ledger)
    {
        ledger?.RecordSunkTank(_tank.Heat * ThermalCapacity);
        _tank.Heat = 0f;
        OffNetworkFloor = 0f;
        _tank.Reactor.Current.Heat = new Heat(0);
    }

    /// <summary>Copies the tank's heat into the reactor's whole-number mirror.</summary>
    private void SyncReactorHeat() => _tank.Reactor.Current.Heat = new Heat((int)MathF.Round(_tank.Heat));

    /// <summary>
    /// Moves heat between the tank and the grid for one field: a positive <paramref name="energyToField"/> is given to
    /// the cells, a negative one taken from them (which can chill them below ambient: a cold tank draws heat from its
    /// surroundings). The flow is bounded by the temperature difference, so this is an exact transfer. Returns how
    /// much tank heat must be taken back because the area had no cells to exchange with (0 when everything moved).
    /// </summary>
    private float ExchangeWithGrid(ITerrainHeatSource terrain, Position position, int radius, double energyToField, TerrainGrid.ThermalField field)
    {
        if (energyToField == 0.0)
            return 0f;
        double moved = terrain.AddEnergyInRadiusArea(position, radius, energyToField, field);
        // The tank already changed by -energyToField; whatever did not move goes back to it (the caller subtracts).
        return (float)((moved - energyToField) / ThermalCapacity);
    }
}
