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
    private int _frameDugPixels;
    private bool _frameShotFired;

    public TankThermals(Tank tank, SimulationSettings settings)
    {
        _tank = tank;
        _settings = settings;
        _heatEngine = new TankHeatEngine(settings);
    }

    public float ThermalCapacity => _heatEngine.TankHeatCapacity;
    public TankCoverage Coverage { get; private set; }
    public bool IsOffNetwork { get; private set; }
    public float OffNetworkFloor { get; private set; }
    public bool IsOverheating { get; private set; }

    public void BeginTick() => IsOverheating = false;

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
    /// Pre-cooling: a tank docked at its own base is chilled toward a full coolant charge, handing its heat to the
    /// base reservoir. This is an exact transfer (the tank goes colder, the reservoir fills), and it stops when the
    /// reservoir is full.
    /// </summary>
    public void RefrigerateAtBase(TankBase tankBase)
    {
        float target = -_settings.TankCoolantCapacity;
        if (_tank.Heat <= target)
            return;

        double wanted = Math.Min(_settings.TankDockCoolingPerTick, _tank.Heat - target) * (double)ThermalCapacity;
        double accepted = tankBase.AcceptTankHeat(wanted);
        _tank.Heat -= (float)(accepted / ThermalCapacity);
    }

    /// <summary>
    /// Network coverage (DESIGN.md §4.6). Inside its base's coverage the tank sheds heat down the links into the base
    /// reactor (an exact transfer, limited by the reactor's room, never below zero heat: only docking refills coolant),
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
            // below the tank's coolant level: coverage only takes heat away, it does not bank coolant.
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
            OffNetworkFloor = Math.Min(OffNetworkFloor, _tank.Heat) + ctx.Settings.TankOffNetworkHeatPerTick;
        }
    }

    /// <summary>A cooling pickup (gold) takes heat out of the tank, never below a full coolant charge.</summary>
    public void CoolFromPickup(int cooling, SimulationSettings settings, ThermalLedger ledger)
    {
        float cooledHeat = TankHeatModel.ClampHeat(in settings, _tank.Heat - cooling);
        ledger.RecordSunkTank((_tank.Heat - cooledHeat) * ThermalCapacity);
        _tank.Heat = cooledHeat;
    }

    /// <summary>
    /// The end-of-tick heat step: action heat from digging and shooting, exchange with the terrain and air under the
    /// tank, the coolant clamp, overheat damage and the off-network floor.
    /// </summary>
    public void Advance(ITerrainHeatSource heat, SimulationSettings settings)
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

        // Exchange with the grid is an exact transfer: whatever the tank lost, the cells under it gained, and a tank
        // can only take heat that is actually there (cells never drop below ambient).
        float nextHeatAfterExchange = step.NextHeat;
        nextHeatAfterExchange -= ExchangeWithGrid(heat, position, radius, step.EnergyToTerrain, TerrainGrid.ThermalField.Terrain);
        nextHeatAfterExchange -= ExchangeWithGrid(heat, position, radius, step.EnergyToAir, TerrainGrid.ThermalField.Air);

        // Digging and shooting create heat from nothing; the heat floor can also create a little.
        heat.Ledger.RecordInjected(step.ActionHeat * ThermalCapacity);
        float nextHeat = TankHeatModel.ClampHeat(in settings, nextHeatAfterExchange);
        heat.Ledger.RecordSunkTank((nextHeatAfterExchange - nextHeat) * ThermalCapacity);

        if (step.OverheatDamage > 0)
        {
            IsOverheating = true;
            _tank.TakeDamage(step.OverheatDamage, DamageCause.Overheat);
        }

        // Off network the tank cannot cool below its rising heat floor; holding it there is reactor heat, created here.
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
    /// Moves heat between the tank and the grid for one field. A positive <paramref name="energyToField"/> is given to
    /// the cells; a negative one is taken from the cells' heat above ambient, and if they hold less than wanted the
    /// tank gets less. Returns how much tank heat must be taken back for that shortfall (0 when everything moved).
    /// </summary>
    private float ExchangeWithGrid(ITerrainHeatSource terrain, Position position, int radius, double energyToField, TerrainGrid.ThermalField field)
    {
        if (energyToField > 0.0)
        {
            terrain.AddEnergyInRadiusArea(position, radius, energyToField, field);
            return 0f;
        }

        if (energyToField < 0.0)
        {
            double wanted = -energyToField;
            double taken = terrain.ExtractEnergyInRadiusArea(position, radius, wanted, field);
            return (float)((wanted - taken) / ThermalCapacity);
        }

        return 0f;
    }
}
