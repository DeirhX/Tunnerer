namespace Tunnerer.Desktop.Gui.Guidance;

using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Match;
using Tunnerer.Core.Entities.Tanks;

/// <summary>
/// Watches the player's side of the world from frame to frame and posts a toast when something changes that the
/// player should know about: a relay linking or losing its link, a harvester starting, stalling or running dry, the
/// base stores filling up, charge running low, leaving the network. It only reads Core state. What happens inside a
/// step (harvesters banking ore) it counts from <see cref="World.StepEvents"/> in <see cref="AfterStep"/>.
/// </summary>
public sealed class GameEventWatcher
{
    private bool _primed;
    private int _poweredRelays;
    private int _harvesters;
    private int _stalledHarvesters;
    private int _dryHarvesters;
    private int _returningHarvesters;
    private bool _storeFull;
    private bool _heatStoreFull;
    private bool _chargeLow;
    private bool _offNetwork;
    private bool _engineRunning;
    private bool _radarScattered;
    private bool _holdingOre;
    private bool _oreHoldFull;
    private readonly int[] _upgradeLevels = new int[BaseUpgrades.Count];
    private int _upgradeRefused;
    private bool _tappingCore;
    private bool _meetingGoal;
    private bool _tappedCore;
    private int _harvestedSinceReport;
    private float _lastHarvestReport;

    /// <summary>The player is told the radar is being scattered once heat takes this share of a pulse's rays.</summary>
    public const float RadarScatteredShare = 0.08f;

    public void Reset()
    {
        _primed = false;
        _harvestedSinceReport = 0;
    }

    /// <summary>
    /// Call after each simulation step, while <see cref="World.StepEvents"/> still holds that step's events: counts
    /// the ore the player's harvesters banked, however many steps a displayed frame runs.
    /// </summary>
    public void AfterStep(World world, Tank player)
    {
        foreach (ref readonly StepEvent e in world.StepEvents)
            if (e.Kind == StepEventKind.OreHarvested && e.Color == player.Color)
                _harvestedSinceReport += e.Count;
    }

    public void Update(World world, Tank player, NotificationFeed feed, float now)
    {
        var home = player.Base;
        if (home == null)
            return;

        int poweredRelays = 0, harvesters = 0, stalled = 0, dry = 0, returning = 0;
        bool hasPylon = false;
        foreach (var m in world.Machines.Machines)
        {
            if (!m.IsAlive || m.State != MachineState.Planted || m.OwnerBase != home)
                continue;
            switch (m.Type)
            {
                case MachineType.Relay when m.LinkPoint?.IsPowered == true:
                    poweredRelays++;
                    break;
                case MachineType.Pylon when m.LinkPoint?.IsPowered == true:
                    hasPylon = true;
                    break;
                case MachineType.Harvester:
                    harvesters++;
                    if (m.IsReturning) returning++;
                    else if (m.Charge < Tweaks.Machine.HarvesterHeatPerHarvest) dry++;
                    else if (m.IsStalled) stalled++;
                    break;
            }
        }

        int store = home.Store.Ore;
        bool storeFull = home.Store.IsFull;
        double heatCapacity = Math.Max(1, (int)home.Reactor.HeatCapacity) * (double)Tweaks.Base.ReactorEnergyPerHeatUnit;
        bool heatStoreFull = home.ReactorHeatEnergy / heatCapacity >= 0.9;
        bool chargeLow = home.Charge < home.ChargeCapacity * 0.1 && harvesters > 0;
        bool offNetwork = player.IsOffNetwork;
        bool engineRunning = home.EngineOutputPerTick > 1.0;
        bool radarScattered = !player.IsDead && world.RadarOf(player) is { HeatLoss: >= RadarScatteredShare };
        bool holdingOre = player.OreHeld > 0;
        bool oreHoldFull = player.IsOreHoldFull;

        CoreTap? core = world.CoreTapOf(home);
        double coreRate = (core?.TapPerTick ?? 0.0) * Tweaks.Perf.TargetFps;
        bool tappingCore = coreRate > 1.0;
        bool meetingGoal = coreRate >= Tweaks.Goal.GoalChargePerSecond;

        if (!_primed)
        {
            _primed = true;
            _lastHarvestReport = now;
            for (int i = 0; i < BaseUpgrades.Count; i++)
                _upgradeLevels[i] = home.Upgrades.Level((BaseUpgrade)i);
            _tappedCore = core?.HasTapped == true;
        }
        else
        {
            for (int i = 0; i < BaseUpgrades.Count; i++)
            {
                int level = home.Upgrades.Level((BaseUpgrade)i);
                if (level > _upgradeLevels[i])
                {
                    var (name, effect) = BaseUpgrades.Describe((BaseUpgrade)i);
                    feed.Post(now, $"upgrade-{i}-{level}", $"{name} upgraded to level {level}", effect, NotificationKind.Good, cooldown: 0f);
                }
                _upgradeLevels[i] = level;
            }
            if (player.UpgradeRefusedTicks > _upgradeRefused)
                feed.Post(now, "upgrade-refused", "Can't buy that upgrade", player.LastUpgradeRefusal switch
                {
                    UpgradeRefusal.NotDocked => "Upgrades are bought while docked at your base.",
                    UpgradeRefusal.Maxed => "That upgrade is already at its top level.",
                    _ => "Not enough ore in the base store. Bring ore home, or let harvesters mine it.",
                }, NotificationKind.Warning, cooldown: 2f);

            if (tappingCore && !_tappingCore)
                feed.Post(now, "core-tap", "Tapping the core",
                    $"Your relays by the core make charge. Reach {Tweaks.Goal.GoalChargePerSecond / 1000:0}k/s and hold it for {Tweaks.Goal.GoalHoldSeconds:0} s.",
                    NotificationKind.Good, cooldown: 60f);
            if (meetingGoal && !_meetingGoal)
                feed.Post(now, "core-goal", "Core tap at the goal rate",
                    "Keep it there: the hold timer is running. Falling short drains it.", NotificationKind.Good, cooldown: 30f);
            if (core?.HasTapped == true && !_tappedCore)
                feed.Post(now, "sector-complete", "Sector complete", "The core is tapped.", NotificationKind.Good,
                    duration: 12f, cooldown: 1e9f);

            if (poweredRelays > _poweredRelays)
                feed.Post(now, "relay-linked", "Relay linked",
                    "It pumps heat home whenever the ground around it is warm, and widens your network.", NotificationKind.Good, cooldown: 4f);
            else if (poweredRelays < _poweredRelays)
                feed.Post(now, "relay-lost", "Relay lost its link",
                    "Something blocks its line to the network. Dig the line clear or plant a relay closer.", NotificationKind.Warning);

            if (harvesters > _harvesters)
                feed.Post(now, "harvester-planted", "Harvester working",
                    "Inside your network it mines the ore around it into your base store. Keep it cool and charged.", NotificationKind.Good, cooldown: 4f);
            if (stalled > _stalledHarvesters)
                feed.Post(now, "harvester-stalled", "Harvester stalled: too hot",
                    "Mining heats the ground. A linked relay near it pumps that heat away.", NotificationKind.Warning);
            if (dry > _dryHarvesters)
                feed.Post(now, "harvester-dry", "Harvester out of charge",
                    hasPylon ? "Move a pylon where it can see the harvester." : "Build a pylon (T) near it so it can recharge.",
                    NotificationKind.Danger);
            if (returning > _returningHarvesters)
                feed.Post(now, "harvester-returning", "Harvester heading to a pylon",
                    "Its battery is low. It goes back to work once recharged.", NotificationKind.Info, cooldown: 60f);

            if (storeFull && !_storeFull)
                feed.Post(now, "store-full", "Base ore store full",
                    "Harvesters idle until you spend ore: dock and buy upgrades (1-6) or build machines.", NotificationKind.Warning);
            if (heatStoreFull && !_heatStoreFull)
                feed.Post(now, "heat-full", "Base heat store nearly full",
                    "Relays and docking cool less until the radiator catches up.", NotificationKind.Warning);
            if (chargeLow && !_chargeLow)
                feed.Post(now, "charge-low", "Base charge low",
                    "Machines stop when it runs out. Put a relay by hot rock to make more.", NotificationKind.Danger);
            if (offNetwork && !_offNetwork)
                feed.Post(now, "off-network", "Off network",
                    "Your tank heats up out here. Head back inside the pale circles, or extend the network with a relay.",
                    NotificationKind.Warning, cooldown: 90f);
            if (radarScattered && !_radarScattered)
                feed.Post(now, "radar-heat", "Radar scattered by heat",
                    "Hot ground swallows the pulse: red dots show how far it got. Cool that ground to see past it.",
                    NotificationKind.Warning, cooldown: 60f);
            // Once each per match: the first ore in the hold, and the first time the hold fills.
            if (holdingOre && !_holdingOre)
                feed.Post(now, "ore-collected", "Emberite collected",
                    "Ore rides in your hold (MATERIALS). Dock, or stay inside your network, to bank it; if you die, it spills where you fall.",
                    NotificationKind.Good, cooldown: 1e9f);
            if (oreHoldFull && !_oreHoldFull)
                feed.Post(now, "ore-hold-full", "Ore hold full",
                    $"The hold takes {player.OreHoldCapacity}. Bring it into your network or dock to bank it before you dig more ore.",
                    NotificationKind.Warning, cooldown: 1e9f);
            if (engineRunning && !_engineRunning)
                feed.Post(now, "engine-running", "Base engine making charge",
                    "Relay heat is being turned into charge. Hotter sources make more.", NotificationKind.Good, cooldown: 120f);

            if (_harvestedSinceReport > 0 && now - _lastHarvestReport >= 20f)
            {
                feed.Post(now, "harvested", $"Harvesters delivered +{_harvestedSinceReport} ore",
                    $"Base ore store {store}.", NotificationKind.Good, cooldown: 0f);
                _harvestedSinceReport = 0;
                _lastHarvestReport = now;
            }
        }

        _poweredRelays = poweredRelays;
        _harvesters = harvesters;
        _stalledHarvesters = stalled;
        _dryHarvesters = dry;
        _returningHarvesters = returning;
        _storeFull = storeFull;
        _heatStoreFull = heatStoreFull;
        _chargeLow = chargeLow;
        _offNetwork = offNetwork;
        _engineRunning = engineRunning;
        _radarScattered = radarScattered;
        _holdingOre = holdingOre;
        _oreHoldFull = oreHoldFull;
        _upgradeRefused = player.UpgradeRefusedTicks;
        _tappingCore = tappingCore;
        _meetingGoal = meetingGoal;
        _tappedCore = core?.HasTapped == true;
    }
}
