namespace Tunnerer.Desktop.Gui;

using Tunnerer.Core.Entities.Links;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Desktop.Presentation;

/// <summary>How a hint line is coloured.</summary>
public enum HintTone { Dim, Info, Good, Warn, Bad }

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
/// <param name="BuildableDirt">Dirt the tank can build with here (<c>Tank.BuildableDirt</c>).</param>
/// <param name="RelayFromBaseStore">A relay built now is paid from the base store (<c>Tank.BuildsFromBaseStore</c>).</param>
/// <param name="BaseStoreDirt">Dirt in the base store.</param>
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
    int BuildableDirt,
    bool RelayFromBaseStore,
    int BaseStoreDirt,
    bool CoolingDown);

/// <summary>
/// Picks the one context hint the network panel shows: what the build keys will do now, or why a carried machine
/// will not plant. First match wins, from the most immediate situation to the general advice to dig.
/// </summary>
public static class HintSelector
{
    public static (string Text, HintTone Tone) Select(in HintInputs s)
    {
        var relay = MachinePresentations.Relay;
        var harvester = MachinePresentations.Harvester;
        var pylon = MachinePresentations.Pylon;
        int relayCost = relay.DirtCost;
        int dirt = s.BuildableDirt;

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
        if (dirt >= relayCost)
        {
            string hint = s.RelayFromBaseStore
                ? $"{relay.KeyLabel}: build relay from the base store ({relayCost} dirt)"
                : $"{relay.KeyLabel}: build relay ({relayCost} dirt)";
            if (dirt >= harvester.DirtCost)
                hint += $"   {harvester.KeyLabel}: build harvester ({harvester.DirtCost} dirt)";
            // A pylon only helps once there is a harvester to charge.
            if (dirt >= pylon.DirtCost && s.Harvesters > 0)
                hint += $"   {pylon.KeyLabel}: pylon ({pylon.DirtCost})";
            return (hint, HintTone.Good);
        }
        if (s.Docked && s.HasLeftBase && s.CoolingDown)
            return ("Docked: cooling down and storing your dirt", HintTone.Info);
        return (s.BaseStoreDirt >= relayCost
            ? $"Dig {relayCost - dirt} more dirt, or build a relay at base from the store"
            : $"Dig {relayCost - dirt} more dirt to build a relay", HintTone.Dim);
    }
}
