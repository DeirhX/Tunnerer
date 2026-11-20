namespace Emberward.Core.Entities.Tanks;

using Emberward.Core.Config;
using Emberward.Core.Resources;
using Emberward.Core.Terrain;
using Emberward.Core.Thermal;
using Emberward.Core.Types;
using Emberward.Core.Network;
using Emberward.Core.Entities.Bases;

/// <summary>
/// The tank's heat: cargo and work heat, the per-tick exchange with the grid, docking-area cooling, network coverage
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
    /// Any base the tank visits draws nearby heat into its reservoir, more near its centre and at the tank's own base.
    /// </summary>
    public void CoolDockingArea(ITerrainHeatSource heat, TankBase baseColl, bool atOwnBase)
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
            baseColl.AbsorbNearbyHeat(heat, position, radius, terrainShare, TerrainGrid.ThermalField.Terrain);
            baseColl.AbsorbNearbyHeat(heat, position, radius, airShare, TerrainGrid.ThermalField.Air);
        }
    }

    /// <summary>
    /// A tank docked at its own base transfers a share of its heat above the reactor temperature into the reservoir
    /// each tick (<c>DockHeatPumpRate</c>). The transfer stops when the reservoir is full. The tank also exchanges
    /// heat through ordinary conduction with the surrounding ground and air.
    /// </summary>
    public void PumpHeatAtBase(TankBase tankBase)
    {
        double excess = _tank.Heat - tankBase.ReactorTemperature;
        if (excess <= 0.0)
            return;

        double wanted = excess * _settings.TankDockHeatPumpRate * ThermalCapacity;
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
        if (ctx.CheckBaseCollision(_tank.Position) is { IsDestroyed: false })
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
            // Under the reserve rule a generator of the tank's side nearby holds the clock: the floor stops rising.
            float rise = ctx.Reserve?.HoldsClock(_tank) == true ? 0f : OffNetworkHeatPerTick(ctx.Settings);
            OffNetworkFloor = Math.Min(OffNetworkFloor, _tank.Heat) + rise;
        }
    }

    /// <summary>
    /// A burn of carried emberite (<see cref="TankReserveSystem"/>): the off-network floor falls by
    /// <paramref name="degrees"/>, never below zero. The tank's heat is left alone; above the lower floor it sheds into
    /// the ground as usual, so no heat is made or destroyed here.
    /// </summary>
    public void LowerOffNetworkFloor(float degrees) => OffNetworkFloor = Math.Max(0f, OffNetworkFloor - degrees);

    /// <summary>
    /// The end-of-tick heat step: heat from carried ore, digging and shooting, exchange with the terrain and air under the
    /// tank, overheat and frostbite damage, and the off-network floor.
    /// </summary>
    public void Advance(ITerrainHeatSource heat)
    {
        var position = _tank.Position;
        int radius = _settings.TankDigRadius;
        var contact = heat.SampleTankContact(position, radius);
        float cargoHeat = _tank.OreHeld * _settings.TankHeatPerOrePerTick;
        var step = _heatEngine.Advance(
            currentHeat: _tank.Heat + cargoHeat,
            frameDugPixels: _frameDugPixels,
            frameShotFired: _frameShotFired,
            contact: contact);

        // Exchange with the grid is an exact transfer both ways: whatever the tank lost, the cells under it gained, and
        // a cold tank draws heat from its surroundings.
        float nextHeat = step.NextHeat;
        heat.ApplyTankExchange(position, radius, contact, step.EnergyToTerrain, step.EnergyToAir);

        // Carried emberite and tank actions are external heat sources.
        heat.Ledger.RecordInjected((cargoHeat + step.ActionHeat) * ThermalCapacity);

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
        if (IsOffNetwork && (contact.SolidTemperature < 0f || contact.AirTemperature < 0f))
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
}
