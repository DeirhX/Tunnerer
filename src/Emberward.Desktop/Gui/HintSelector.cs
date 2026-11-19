namespace Emberward.Desktop.Gui;

using Emberward.Core;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Tanks;
using Emberward.Desktop.Presentation;
using Emberward.Core.Network;

/// <summary>How a hint, toast or warning line is coloured. <see cref="Accent"/> is for tips, not for state.</summary>
public enum HintTone { Dim, Info, Good, Warn, Bad, Accent }

/// <summary>What the network panel's hint line is chosen from: plain values read from Core this frame.</summary>
/// <param name="HasLeftBase">The tank has driven out of its base at least once (<c>Tank.HasLeftBase</c>).</param>
/// <param name="Docked">Docked at its own base (<c>Tank.IsDockedAtOwnBase</c>).</param>
/// <param name="InsideBase">Standing inside any base (<c>World.IsInsideAnyBase</c>).</param>
/// <param name="RelayJustLinked">A relay came online in the last few seconds.</param>
/// <param name="Carried">The machine in hand, or null.</param>
/// <param name="CarriedLink">
/// Where the carried machine would link if planted here (<c>World.CarriedPlantLink</c>); only read for a machine that
/// needs a link to plant.
/// </param>
/// <param name="HeatStoreFull">The base heat store is full with relays linked (<c>NetworkStatus.IsHeatStoreFullWithRelays</c>).</param>
/// <param name="SpendableOre">Ore the tank can build with here (the paying stock of <c>Tank.QuoteBuild</c>).</param>
/// <param name="RelayFromBaseStore">A relay built now is paid from the base store (<c>BuildQuote.Payer</c>).</param>
/// <param name="NotEnoughOre">A build key press was just refused for want of ore (<c>Tank.LastBuildRefusal</c>).</param>
/// <param name="CoolingDown">The tank's heat budget is not yet full (<c>Tank.IsCoolingDown</c>).</param>
public readonly record struct HintInputs(
    bool IsDead,
    bool HasLeftBase,
    bool Docked,
    bool InsideBase,
    bool RelayJustLinked,
    int PumpingRelays,
    bool PlantRefused,
    MachineType? Carried,
    LinkPreviewStatus CarriedLink,
    bool HeatStoreFull,
    int Harvesters,
    int SpendableOre,
    bool RelayFromBaseStore,
    bool NotEnoughOre,
    bool CoolingDown);

/// <summary>
/// Picks the one context hint the network panel shows: what the build keys will do now, or why a carried machine
/// will not plant. First match wins, from the most immediate situation to the general advice to mine.
/// </summary>
public static class HintSelector
{
    /// <summary>The hint for <paramref name="player"/> now, read from the world.</summary>
    /// <param name="status">The player's base's network (<c>World.GetNetworkStatus</c>).</param>
    /// <param name="relayJustLinked">A relay came online in the last few seconds.</param>
    public static (string Text, HintTone Tone) Select(World world, Tank player, in NetworkStatus status, bool relayJustLinked)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(player);
        var relayQuote = player.QuoteBuild(MachineType.Relay);
        return Select(new HintInputs(
            IsDead: player.IsDead,
            HasLeftBase: player.HasLeftBase,
            Docked: player.IsDockedAtOwnBase,
            InsideBase: world.IsInsideAnyBase(player.Position),
            RelayJustLinked: relayJustLinked,
            PumpingRelays: status.PumpingRelays,
            PlantRefused: player.PlantRefusedTicks > 0,
            Carried: player.CarriedMachineType,
            CarriedLink: world.CarriedPlantLink(player),
            HeatStoreFull: status.IsHeatStoreFullWithRelays,
            Harvesters: status.Harvesters,
            SpendableOre: relayQuote.Stock,
            RelayFromBaseStore: relayQuote.Payer == OrePayer.BaseStore,
            NotEnoughOre: player.BuildRefusedTicks > 0 && player.LastBuildRefusal == BuildRefusal.NotEnoughOre,
            CoolingDown: player.IsCoolingDown));
    }

    public static (string Text, HintTone Tone) Select(in HintInputs s)
    {
        var relay = MachinePresentations.Relay;
        var harvester = MachinePresentations.Harvester;
        var pylon = MachinePresentations.Pylon;
        int relayCost = relay.OreCost;
        int ore = s.SpendableOre;

        if (s.IsDead)
            return (string.Empty, HintTone.Dim);
        if (!s.HasLeftBase && s.Docked)
            return ("WASD to drive - the base exit is at the bottom", HintTone.Info);
        if (s.RelayJustLinked && s.Carried == null)
            return (s.PumpingRelays > 0
                ? "Relay linked - pumping heat into your base"
                : "Relay linked - idle until the ground near it heats up", HintTone.Good);
        if (s.PlantRefused)
            return ("Not planted: it would not link - find a clear line", HintTone.Bad);
        if (s.Carried is MachineType carried)
        {
            var machine = MachinePresentations.Get(carried);
            string plant = $"{machine.KeyLabel}: {machine.PlantHint}";
            if (s.InsideBase)
                return ($"Carrying {machine.Name} - leave the base to plant it", HintTone.Info);
            if (!machine.Behavior.RequiresLinkToPlant)
                return (plant, HintTone.Good);
            return s.CarriedLink switch
            {
                LinkPreviewStatus.Live => (plant, HintTone.Good),
                LinkPreviewStatus.Blocked => ("Line to network blocked (red X) - dig it clear", HintTone.Bad),
                LinkPreviewStatus.TooFar => ("Too far from the network - move closer", HintTone.Warn),
                _ => ("No powered network to link to", HintTone.Warn),
            };
        }
        if (s.HeatStoreFull)
            return ("Base heat store full - relays and docking cool less", HintTone.Warn);
        if (s.NotEnoughOre)
            return ("Not enough ore - mine emberite, or bring your hold into your network", HintTone.Bad);
        if (ore >= relayCost)
        {
            string hint = s.RelayFromBaseStore
                ? $"{relay.KeyLabel}: build relay from the base store ({relayCost} ore)"
                : $"{relay.KeyLabel}: build relay ({relayCost} ore)";
            if (ore >= harvester.OreCost)
                hint += $"   {harvester.KeyLabel}: build harvester ({harvester.OreCost} ore)";
            // A pylon only helps once there is a harvester to charge.
            if (ore >= pylon.OreCost && s.Harvesters > 0)
                hint += $"   {pylon.KeyLabel}: pylon ({pylon.OreCost})";
            return (hint, HintTone.Good);
        }
        if (s.Docked && s.HasLeftBase && s.CoolingDown)
            return ("Docked: cooling down and banking your ore", HintTone.Info);
        return ($"Mine {relayCost - ore} more ore (gold crystal) to build a relay", HintTone.Dim);
    }
}
