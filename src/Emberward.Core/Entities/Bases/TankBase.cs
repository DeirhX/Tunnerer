namespace Emberward.Core.Entities.Bases;

using Emberward.Core.Types;
using Emberward.Core.Config;
using Emberward.Core.Resources;
using Emberward.Core.Terrain;
using Emberward.Core.Network;

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
    public bool IsBotControlled { get; private set; }

    private double _botOreCredit;

    private static int Half => Tweaks.Base.BaseSize / 2;
    private static int DoorHalf => Tweaks.Base.DoorSize / 2;

    private readonly float _ambientTemperature;

    public TankBase(Position position, int color, SimulationSettings? settings = null)
    {
        Position = position;
        Color = color;
        _ambientTemperature = (settings ?? SimulationSettings.FromTweaks()).ThermalAmbientTemperature;
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

    /// <summary>The reactor's temperature, which rises as its finite heat store fills.</summary>
    public double ReactorTemperature => _ambientTemperature + HeatStoreFill * Tweaks.Base.EngineColdSideRise;

    /// <summary>
    /// Energy the radiator vents out of the reactor per tick while it holds heat (see <see cref="Advance"/>), with its
    /// radiator upgrades.
    /// </summary>
    public double VentCapacityPerTick => Upgrades.RadiatorUnits * (double)Tweaks.Base.ReactorEnergyPerHeatUnit;

    /// <summary>Link-network node of this base; relays are powered through it.</summary>
    public LinkPoint? LinkPoint { get; set; }

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

    /// <summary><see cref="EngineOutputPerTick"/> as a rate per second.</summary>
    public double EngineOutputPerSecond => EngineOutputPerTick * Tweaks.Perf.TargetFps;

    /// <summary>The engine made a noticeable amount of charge during the last tick.</summary>
    public bool IsEngineRunning => EngineOutputPerTick > 1.0;

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
        double dT = sourceTemperature - ReactorTemperature;
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
        if (maxEnergy <= 0.0 || ReactorRoomEnergy <= 0.0)
        {
            limitedByBase = maxEnergy > 0.0;
            engineOutput = 0.0;
            groundWasCold = false;
            return 0.0;
        }
        double absorbed = PumpField(terrain, center, radius, TerrainGrid.ThermalField.Terrain, maxEnergy,
            out limitedByBase, out engineOutput);
        if (absorbed < maxEnergy)
        {
            absorbed += PumpField(terrain, center, radius, TerrainGrid.ThermalField.Air, maxEnergy - absorbed,
                out bool airLimited, out double airOutput);
            limitedByBase |= airLimited;
            engineOutput += airOutput;
        }
        groundWasCold = absorbed == 0.0;
        _engineInputThisTick += absorbed;
        return absorbed;
    }

    private double PumpField(ITerrainHeatSource terrain, Position center, int radius, TerrainGrid.ThermalField field,
        double requested, out bool limited, out double output)
    {
        double eta = UpgradedEngineEfficiency(terrain.ExtractableHeatTemperature(center, radius, field));
        double budget = PumpBudget(eta, requested);
        limited = budget < requested;
        double taken = terrain.ExtractEnergyInRadiusArea(center, radius, budget, field);
        // The core tap measures potential output even when the charge store is full.
        output = eta * taken;
        double stored = StoreCharge(output);
        terrain.Ledger.RecordToCharge(stored);
        _engineOutputThisTick += stored;
        CreditReactor(taken - stored);
        return taken;
    }

    /// <summary>
    /// What <see cref="AbsorbHeatFrom"/> reports as limited by the base when the ground has no heat to give (heat it
    /// would take at ambient, where the engine makes nothing of it), without looking at the ground.
    /// </summary>
    public bool LimitsPumpFromColdGround(double maxEnergy) => PumpBudget(0.0, maxEnergy) < maxEnergy;

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

    /// <summary>Stores up to the available charge room and returns the amount stored.</summary>
    internal double StoreCharge(double amount)
    {
        double stored = Math.Min(amount, Math.Max(0.0, ChargeCapacity - Charge));
        Charge += stored;
        return stored;
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
        AbsorbNearbyHeat(terrain, Position, outerRadius, Tweaks.Base.AreaCoolingOuterHeatTotal, TerrainGrid.ThermalField.Terrain);
        AbsorbNearbyHeat(terrain, Position, midRadius, Tweaks.Base.AreaCoolingMidHeatTotal, TerrainGrid.ThermalField.Terrain);
        AbsorbNearbyHeat(terrain, Position, innerRadius, Tweaks.Base.AreaCoolingInnerHeatTotal, TerrainGrid.ThermalField.Terrain);
        AbsorbNearbyHeat(terrain, Position, outerRadius, Tweaks.Base.AreaCoolingOuterAirTotal, TerrainGrid.ThermalField.Air);
        AbsorbNearbyHeat(terrain, Position, midRadius, Tweaks.Base.AreaCoolingMidAirTotal, TerrainGrid.ThermalField.Air);
        AbsorbNearbyHeat(terrain, Position, innerRadius, Tweaks.Base.AreaCoolingInnerAirTotal, TerrainGrid.ThermalField.Air);

        // The radiator vents at its finite rate. When it can vent more than the whole units it holds, it can also
        // vent the remainder, so a reservoir with less than one unit of energy still cools.
        int beforeHeat = Reactor.Heat;
        int ventedUnits = Math.Min(beforeHeat, Upgrades.RadiatorUnits);
        Reactor.Exhaust(new ReactorState(ventedUnits, 0));
        double vented = ventedUnits * energyPerUnit;
        if (ventedUnits < Upgrades.RadiatorUnits)
        {
            vented += _reactorHeatRemainder;
            _reactorHeatRemainder = 0.0;
        }
        terrain.Ledger.RecordSunkBase(vented);

        Reactor.Add(new ReactorState(0, Tweaks.Base.HealthRegen));
    }

    public void RechargeTank(Reactor tankReactor, int tankColor)
    {
        if (tankColor == Color)
            GiveReactorResources(tankReactor, Tweaks.Base.HomeRechargeHealth);
        else
            GiveReactorResources(tankReactor, Tweaks.Base.ForeignRechargeHealth);
    }

    internal void AbsorbNearbyHeat(ITerrainHeatSource terrain, Position center, int radius, double limit, TerrainGrid.ThermalField field)
    {
        double taken = terrain.ExtractEnergyInRadiusArea(center, radius, Math.Min(limit, ReactorRoomEnergy), field);
        CreditReactor(taken);
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

    /// <summary>
    /// Hands the side to the computer or back to a player. Handing it back drops the ore credit the bot's income had
    /// built up, so a side that changes hands twice starts its income from nothing.
    /// </summary>
    internal void SetBotControlled(bool botControlled)
    {
        IsBotControlled = botControlled;
        if (!botControlled)
            _botOreCredit = 0.0;
    }

    /// <summary>Mixes who plays the side and the bot income's credit, for the sync digest's economy part.</summary>
    internal ulong MixEconomy(ulong hash) => Fnv1a.Mix(Fnv1a.Mix(hash, IsBotControlled), _botOreCredit);

    /// <summary>Mixes the reactor's exact stored heat, whole units and remainder, for the sync digest's heat part.</summary>
    internal ulong MixReactorHeat(ulong hash) => Fnv1a.Mix(Fnv1a.Mix(hash, (int)Reactor.Heat), _reactorHeatRemainder);
}
