namespace Tunnerer.Core.Entities;

using Tunnerer.Core.Types;
using Tunnerer.Core.Config;
using Tunnerer.Core.Resources;
using Tunnerer.Core.Terrain;

public class TankBase
{
    public Position Position { get; }
    public int Color { get; }
    public BoundingBox BoundingBox { get; } = new(Tweaks.Base.BaseSize / 2, Tweaks.Base.BaseSize / 2);
    public Reactor Reactor { get; } = new(
        initial: new ReactorState(Tweaks.Base.InitialHeat, Tweaks.Base.InitialHealth),
        capacity: new ReactorState(Tweaks.Base.HeatCapacity, Tweaks.Base.HealthCapacity));

    /// <summary>
    /// The base's ore store (DESIGN.md §6.4): what machines and upgrades are paid from. Docked and networked tanks
    /// unload into it, linked harvesters mine into it.
    /// </summary>
    public OreStore Store { get; } = new(Tweaks.Ore.BaseStartingStore, Tweaks.Ore.BaseStoreCapacity);

    /// <summary>
    /// True when the computer plays this base's side (<see cref="World.SetBotControlled"/>): the bot does not prospect,
    /// so the store gets <see cref="Tweaks.Ore.BotOrePerSecond"/> instead.
    /// </summary>
    public bool IsBotControlled { get; internal set; }

    private double _botOreCredit;

    private static int Half => Tweaks.Base.BaseSize / 2;
    private static int DoorHalf => Tweaks.Base.DoorSize / 2;

    // The base's own temperature, which the engine's efficiency is measured from.
    private readonly float _baseTemperature;

    public TankBase(Position position, int color, SimulationSettings? settings = null)
    {
        Position = position;
        Color = color;
        _baseTemperature = (settings ?? SimulationSettings.FromTweaks()).ThermalMaterials[ThermalMaterial.Base].FixedTemperature.GetValueOrDefault();
    }

    public bool IsInside(Position tested) => BoundingBox.IsInside(tested, Position);

    // Energy absorbed from the grid that does not yet add up to a whole reactor heat unit.
    private double _reactorHeatRemainder;

    /// <summary>
    /// Thermal energy currently stored in the reactor, in grid units. Reactor heat is a whole number of
    /// units of <see cref="Tweaks.Base.ReactorEnergyPerHeatUnit"/>; the sub-unit remainder is tracked here
    /// so absorption is exact.
    /// </summary>
    public double ReactorHeatEnergy => (int)Reactor.Heat * (double)Tweaks.Base.ReactorEnergyPerHeatUnit + _reactorHeatRemainder;

    /// <summary>How full the reactor (the base's heat store) is, 0 to 1, from its exact stored energy.</summary>
    public double HeatStoreFill
    {
        get
        {
            double capacityEnergy = Math.Max(1, (int)Reactor.HeatCapacity) * (double)Tweaks.Base.ReactorEnergyPerHeatUnit;
            return Math.Clamp(ReactorHeatEnergy / capacityEnergy, 0.0, 1.0);
        }
    }

    /// <summary>
    /// Energy the radiator vents out of the reactor per tick while it holds heat (see <see cref="Advance"/>), with its
    /// radiator upgrades.
    /// </summary>
    public double VentCapacityPerTick => Upgrades.RadiatorUnits * (double)Tweaks.Base.ReactorEnergyPerHeatUnit;

    /// <summary>Link-network node of this base; relays are powered through it.</summary>
    public Links.LinkPoint? LinkPoint { get; set; }

    /// <summary>Energy the reactor can still take before it is full.</summary>
    public double ReactorRoomEnergy =>
        Math.Max(0, (int)Reactor.HeatCapacity - (int)Reactor.Heat) * (double)Tweaks.Base.ReactorEnergyPerHeatUnit - _reactorHeatRemainder;

    /// <summary>Charge stored at the base: the power machines and pylons draw on (DESIGN.md §4.5).</summary>
    public double Charge { get; private set; } = Tweaks.Base.InitialCharge;

    public double ChargeCapacity => Tweaks.Base.ChargeCapacity;

    /// <summary>What this base has bought with its ore store (DESIGN.md §4.8), for itself and for its tank.</summary>
    public BaseUpgrades Upgrades { get; } = new();

    /// <summary>
    /// Buys the next level of <paramref name="upgrade"/> from the ore store. False when it is maxed out or the store
    /// cannot pay.
    /// </summary>
    public bool TryBuyUpgrade(BaseUpgrade upgrade)
    {
        if (Upgrades.IsMaxed(upgrade) || !Store.Pay(Upgrades.NextCost(upgrade)))
            return false;
        Upgrades.Raise(upgrade);
        return true;
    }

    /// <summary>The bot's stand-in income: <see cref="Tweaks.Ore.BotOrePerSecond"/>, in whole units, while bot-controlled.</summary>
    internal void AdvanceBotTrickle(double dtSeconds)
    {
        if (!IsBotControlled)
            return;
        _botOreCredit += Tweaks.Ore.BotOrePerSecond * dtSeconds;
        int whole = (int)_botOreCredit;
        if (whole <= 0)
            return;
        _botOreCredit -= whole;
        Store.Add(whole);
    }

    /// <summary>Charge the relays tapping the map's hot core made for this base during the last tick.</summary>
    public double CoreTapPerTick { get; internal set; }

    /// <summary>Seconds the core tap has held the goal rate (DESIGN.md §4.8); the sector is won at the goal hold.</summary>
    public double CoreHoldSeconds { get; internal set; }

    /// <summary>True once this base has tapped the core at the goal rate for long enough.</summary>
    public bool HasTappedCore { get; internal set; }
    /// <summary>How full the charge store is, 0 to 1.</summary>
    public double ChargeFill => Math.Clamp(Charge / ChargeCapacity, 0.0, 1.0);

    /// <summary>The charge gauge: Warn when running low, Bad when nearly empty.</summary>
    public StatusLevel ChargeLevel
    {
        get
        {
            float fill = (float)ChargeFill;
            return fill < Tweaks.Base.ChargeLowFill ? StatusLevel.Bad
                : fill < Tweaks.Base.ChargeWarnFill ? StatusLevel.Warn
                : StatusLevel.Ok;
        }
    }

    /// <summary>
    /// Radiator load for a heat inflow (energy per tick reaching the radiator): the inflow against what the radiator
    /// vents (with its upgrades). Past 1 the heat store fills, and once it is full the relays are throttled.
    /// </summary>
    public float RadiatorLoad(double inflowPerTick) => (float)(inflowPerTick / VentCapacityPerTick);

    /// <summary>The radiator gauge for a <see cref="RadiatorLoad"/>: Warn near capacity, Bad past it.</summary>
    public static StatusLevel RadiatorLoadLevel(float load) =>
        load < Tweaks.Base.RadiatorWarnLoad ? StatusLevel.Ok
        : load <= 1.0f ? StatusLevel.Warn
        : StatusLevel.Bad;

    /// <summary>The heat-store gauge for a <see cref="HeatStoreFill"/>: Warn when filling, Bad (full) near the top.</summary>
    public static StatusLevel HeatStoreLevelOf(double heatStoreFill)
    {
        float fill = (float)heatStoreFill;
        return fill < Tweaks.Base.HeatStoreWarnFill ? StatusLevel.Ok
            : fill < Tweaks.Base.HeatStoreFullFill ? StatusLevel.Warn
            : StatusLevel.Bad;
    }

    /// <summary>Charge the engine made from relay heat during the last tick.</summary>
    public double EngineOutputPerTick { get; private set; }

    /// <summary>Heat relays delivered during the last tick (the engine's input).</summary>
    public double EngineInputPerTick { get; private set; }

    private double _engineOutputThisTick;
    private double _engineInputThisTick;

    /// <summary>
    /// Fraction of delivered heat the base engine turns into charge, from how far the source is above the base's
    /// own temperature: hot sources are worth far more than warm ones.
    /// </summary>
    public double EngineEfficiency(double sourceTemperature) =>
        EngineEfficiency(sourceTemperature, Tweaks.Base.EngineMaxEfficiency, Tweaks.Base.EngineTemperatureScale);

    /// <summary>This base's engine efficiency, with its engine upgrades.</summary>
    public double UpgradedEngineEfficiency(double sourceTemperature) =>
        EngineEfficiency(sourceTemperature, Upgrades.EngineMaxEfficiency, Upgrades.EngineTemperatureScale);

    private double EngineEfficiency(double sourceTemperature, double maxEfficiency, double temperatureScale)
    {
        double dT = sourceTemperature - _baseTemperature;
        if (dT <= 0.0)
            return 0.0;
        return Math.Min(maxEfficiency, dT / (dT + temperatureScale));
    }

    /// <summary>
    /// Pulls up to <paramref name="maxEnergy"/> out of the square area around <paramref name="center"/> (terrain and
    /// air, only heat above ambient) on behalf of a relay. The heat runs through the base engine: a fraction
    /// <see cref="EngineEfficiency"/> of it (for the temperature it was taken at) becomes charge, while the store has
    /// room, and the rest goes into the reactor, limited by the reactor's free room. The reactor then vents it at
    /// the radiator rate in <see cref="Advance"/>. Returns the energy taken from the ground.
    /// <paramref name="limitedByBase"/> is true when the base could not take all of <paramref name="maxEnergy"/>
    /// (reactor room after the engine's share), whatever the ground had to give.
    /// <paramref name="groundWasCold"/> is true when the base had room and the ground had no heat at all to give.
    /// </summary>
    public double AbsorbHeatFrom(ITerrainHeatSource terrain, Position center, int radius, double maxEnergy,
        out bool limitedByBase, out double engineOutput, out bool groundWasCold)
    {
        engineOutput = 0.0;
        groundWasCold = false;
        double eta = UpgradedEngineEfficiency(terrain.ExtractableHeatTemperature(center, radius, TerrainGrid.ThermalField.Terrain));
        double chargeRoom = Math.Max(0.0, ChargeCapacity - Charge);
        double budget = PumpBudget(eta, maxEnergy);
        limitedByBase = budget < maxEnergy;
        if (budget <= 0.0)
            return 0.0;

        double absorbed = terrain.ExtractEnergyInRadiusArea(center, radius, budget, TerrainGrid.ThermalField.Terrain);
        absorbed += terrain.ExtractEnergyInRadiusArea(center, radius, budget - absorbed, TerrainGrid.ThermalField.Air);
        groundWasCold = absorbed == 0.0;

        double toCharge = Math.Min(eta * absorbed, chargeRoom);
        if (toCharge > 0.0)
        {
            Charge += toCharge;
            terrain.Ledger.RecordToCharge(toCharge);
        }
        CreditReactor(absorbed - toCharge);
        // What the engine made from this heat, whether or not the store had room to keep all of it (the core goal
        // measures the tap, not the store).
        engineOutput = eta * absorbed;
        _engineInputThisTick += absorbed;
        _engineOutputThisTick += toCharge;
        return absorbed;
    }

    /// <summary>
    /// What <see cref="AbsorbHeatFrom"/> reports as limited by the base when the ground has no heat to give (heat it
    /// would take at ambient, where the engine makes nothing of it), without looking at the ground.
    /// </summary>
    public bool LimitsPumpFromColdGround(double maxEnergy) => PumpBudget(UpgradedEngineEfficiency(0.0), maxEnergy) < maxEnergy;

    /// <summary>
    /// The most heat the base can take in one pump. Only what the reactor can hold after the engine's share:
    /// (1 - eta) E fits while the charge store has room, and E - chargeRoom fits once it is full.
    /// </summary>
    private double PumpBudget(double eta, double maxEnergy)
    {
        double chargeRoom = Math.Max(0.0, ChargeCapacity - Charge);
        double reactorRoom = ReactorRoomEnergy;
        double budget = Math.Min(maxEnergy, reactorRoom + chargeRoom);
        if (eta < 1.0)
            budget = Math.Min(budget, reactorRoom / (1.0 - eta));
        return budget;
    }

    /// <summary>Takes up to <paramref name="amount"/> out of the charge store and returns what was taken.</summary>
    public double DrawCharge(double amount)
    {
        double taken = Math.Clamp(amount, 0.0, Charge);
        Charge -= taken;
        return taken;
    }

    /// <summary>
    /// Takes up to <paramref name="energy"/> from a docked tank into the reactor, limited by the reactor's free room.
    /// Returns the energy accepted; the caller removes exactly that from the tank.
    /// </summary>
    public double AcceptTankHeat(double energy)
    {
        double accepted = Math.Min(energy, ReactorRoomEnergy);
        if (accepted <= 0.0)
            return 0.0;
        CreditReactor(accepted);
        return accepted;
    }

    private void CreditReactor(double energy)
    {
        if (energy <= 0.0)
            return;

        double energyPerUnit = Tweaks.Base.ReactorEnergyPerHeatUnit;
        _reactorHeatRemainder += energy;
        int wholeUnits = (int)Math.Floor(_reactorHeatRemainder / energyPerUnit);
        if (wholeUnits > 0)
        {
            Reactor.Add(new ReactorState(wholeUnits, 0));
            _reactorHeatRemainder -= wholeUnits * energyPerUnit;
        }
    }

    public void Advance(ITerrainHeatSource terrain)
    {
        // Bases advance after machines, so this tick's relay deliveries are complete.
        EngineInputPerTick = _engineInputThisTick;
        EngineOutputPerTick = _engineOutputThisTick;
        _engineInputThisTick = _engineOutputThisTick = 0.0;

        int outerRadius = Half;
        int midRadius = Math.Max(1, (outerRadius * 2) / 3);
        int innerRadius = Math.Max(1, outerRadius / 3);
        double energyPerUnit = Tweaks.Base.ReactorEnergyPerHeatUnit;

        // The reactor is a finite sink: it can only absorb as much energy as it still has room for,
        // and only heat that is actually present in the surroundings.
        double room = Math.Max(0, (int)Reactor.HeatCapacity - (int)Reactor.Heat) * energyPerUnit - _reactorHeatRemainder;
        double absorbed = 0.0;
        double Pull(int radius, double cap, TerrainGrid.ThermalField field)
        {
            double taken = terrain.ExtractEnergyInRadiusArea(Position, radius, Math.Min(cap, room - absorbed), field);
            absorbed += taken;
            return taken;
        }

        Pull(outerRadius, Tweaks.Base.AreaCoolingOuterHeatTotal, TerrainGrid.ThermalField.Terrain);
        Pull(midRadius, Tweaks.Base.AreaCoolingMidHeatTotal, TerrainGrid.ThermalField.Terrain);
        Pull(innerRadius, Tweaks.Base.AreaCoolingInnerHeatTotal, TerrainGrid.ThermalField.Terrain);
        Pull(outerRadius, Tweaks.Base.AreaCoolingOuterAirTotal, TerrainGrid.ThermalField.Air);
        Pull(midRadius, Tweaks.Base.AreaCoolingMidAirTotal, TerrainGrid.ThermalField.Air);
        Pull(innerRadius, Tweaks.Base.AreaCoolingInnerAirTotal, TerrainGrid.ThermalField.Air);

        CreditReactor(absorbed);

        // Radiator: the reactor vents HeatCooldown units per tick out of the system. This is the base's finite
        // sink rate; heat pumped in faster than this fills the reactor and the pumping stops.
        int beforeHeat = Reactor.Heat;
        Reactor.Exhaust(new ReactorState(Upgrades.RadiatorUnits, 0));
        int ventedUnits = Math.Max(0, beforeHeat - (int)Reactor.Heat);
        if (ventedUnits > 0)
            terrain.Ledger.RecordSunkBase(ventedUnits * energyPerUnit);

        // Keep base center as a hard thermal sink for gameplay/readability:
        // its cell should not retain residual terrain/air energy. The removed energy is metered.
        terrain.SinkCellToAmbient(Position, TerrainGrid.ThermalField.Terrain);
        terrain.SinkCellToAmbient(Position, TerrainGrid.ThermalField.Air);

        Reactor.Add(new ReactorState(0, Tweaks.Base.HealthRegen));
    }

    public void RechargeTank(Reactor tankReactor, int tankColor)
    {
        if (tankColor == Color)
            GiveReactorResources(tankReactor, Tweaks.Base.HomeRechargeHealth);
        else
            GiveReactorResources(tankReactor, Tweaks.Base.ForeignRechargeHealth);
    }

    private static void GiveReactorResources(Reactor target, int healthRegen)
    {
        target.Add(new ReactorState(0, healthRegen));
    }

    public void CreateInTerrain(ITerrainPixelAccessor terrain, int color)
    {
        int half = Half;

        for (int y = -half; y <= half; y++)
            for (int x = -half; x <= half; x++)
            {
                var pix = Position + new Offset(x, y);
                if (!terrain.IsInside(pix)) continue;

                if (IsEdge(x, y))
                {
                    if (IsDoor(x))
                        terrain.SetPixel(pix, TerrainPixel.BaseBarrier);
                    else
                        terrain.SetPixel(pix, (TerrainPixel)((byte)TerrainPixel.BaseMin + color));
                }
                else
                {
                    terrain.SetPixel(pix, TerrainPixel.BaseCore);
                }
            }
    }

    private static bool IsEdge(int dx, int dy) => Math.Abs(dx) == Half || Math.Abs(dy) == Half;
    private static bool IsDoor(int dx) => dx >= -DoorHalf && dx <= DoorHalf;
}
