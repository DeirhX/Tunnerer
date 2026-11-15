namespace Tunnerer.Desktop.Gui;

using Tunnerer.Core.Entities.Links;
using Tunnerer.Core.Entities.Machines;

/// <summary>How a hint line is coloured.</summary>
public enum HintTone { Dim, Info, Good, Warn, Bad }

/// <summary>What the network panel's hint line is chosen from: plain values read from Core this frame.</summary>
/// <param name="HasLeftBase">The tank has driven out of its base at least once since the HUD started.</param>
/// <param name="Docked">Docked at its own base (<c>Tank.IsDockedAtOwnBase</c>).</param>
/// <param name="InsideBase">Standing inside any base.</param>
/// <param name="RelayJustLinked">A relay came online in the last few seconds.</param>
/// <param name="Carried">The machine in hand, or null.</param>
/// <param name="CarriedLink">
/// Where the carried machine would link if planted here; only read for a machine that needs a link to plant.
/// </param>
/// <param name="BuildableDirt">Dirt the tank can build with here (<c>Tank.BuildableDirt</c>).</param>
/// <param name="HoldDirt">Dirt in the tank's own hold.</param>
/// <param name="BaseStoreDirt">Dirt in the base store.</param>
/// <param name="CoolingDown">The tank's heat budget is not yet full.</param>
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
    float HeatStoreFill,
    int PoweredRelays,
    int Harvesters,
    int BuildableDirt,
    int HoldDirt,
    int BaseStoreDirt,
    bool CoolingDown);

/// <summary>
/// Picks the one context hint the network panel shows: what the build keys will do now, or why a carried machine
/// will not plant. First match wins, from the most immediate situation to the general advice to dig.
/// </summary>
public static class HintSelector
{
    private static int DirtCost(MachineType type) => MachineBehaviors.Get(type).BuildCost.Dirt;

    public static (string Text, HintTone Tone) Select(in HintInputs s)
    {
        int relayCost = DirtCost(MachineType.Relay);
        int harvesterCost = DirtCost(MachineType.Harvester);
        int pylonCost = DirtCost(MachineType.Pylon);
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
            bool carryingHarvester = carried == MachineType.Harvester;
            bool carryingPylon = carried == MachineType.Pylon;
            string carriedName = carryingHarvester ? "harvester" : carryingPylon ? "pylon" : "relay";
            if (s.InsideBase)
                return ($"Carrying {carriedName} - leave the base to plant it", HintTone.Info);
            if (carryingHarvester)
                return ("R: plant harvester here - it mines nearby dirt into your base store", HintTone.Good);
            return s.CarriedLink switch
            {
                LinkPreviewStatus.Live => (carryingPylon
                    ? "T: plant pylon here - it charges harvesters it can see"
                    : "E: plant relay here - it will link (green line)", HintTone.Good),
                LinkPreviewStatus.Blocked => ("Line to network blocked (red X) - dig it clear", HintTone.Bad),
                LinkPreviewStatus.TooFar => ("Too far from the network - move closer", HintTone.Warn),
                _ => ("No powered network to link to", HintTone.Warn),
            };
        }
        if (s.HeatStoreFill >= 0.9f && s.PoweredRelays > 0)
            return ("Base heat store full - relays and docking cool less", HintTone.Warn);
        if (dirt >= relayCost)
        {
            bool fromBaseStore = s.Docked && s.HoldDirt < relayCost;
            string hint = fromBaseStore
                ? $"E: build relay from the base store ({relayCost} dirt)"
                : $"E: build relay ({relayCost} dirt)";
            if (dirt >= harvesterCost)
                hint += $"   R: build harvester ({harvesterCost} dirt)";
            if (dirt >= pylonCost && s.Harvesters > 0)
                hint += $"   T: pylon ({pylonCost})";
            return (hint, HintTone.Good);
        }
        if (s.Docked && s.HasLeftBase && s.CoolingDown)
            return ("Docked: cooling down and storing your dirt", HintTone.Info);
        return (s.BaseStoreDirt >= relayCost
            ? $"Dig {relayCost - dirt} more dirt, or build a relay at base from the store"
            : $"Dig {relayCost - dirt} more dirt to build a relay", HintTone.Dim);
    }
}
