namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Links;
using Tunnerer.Core.Entities.Machines;
using static Tunnerer.Desktop.Gui.HudDraw;

/// <summary>
/// Top-right readout: lives, the relay network (how many relays are linked, how full the base reservoir is)
/// and a context hint for the build key that explains what E will do, or why a carried relay will not link.
/// </summary>
public sealed class NetworkPanel
{
    private bool _hasLeftBase;
    private int _lastPoweredRelays;
    private float _relayLinkedUntil = -1f;
    private double _radiatorInflow;
    private double _engineOutput;

    private static int DirtCost(MachineType type) => MachineBehaviors.Get(type).BuildCost.Dirt;

    public void Draw((float x, float y, float w, float h) vp, Tank player, World world, float time)
    {
        var tankBase = player.Base;
        if (vp.w <= 0 || vp.h <= 0 || tankBase == null)
            return;

        var status = world.GetNetworkStatus(tankBase);
        int relays = status.Relays, powered = status.PoweredRelays, pumping = status.PumpingRelays;
        int harvesters = status.Harvesters, stalled = status.HarvestersTooHot;
        int recharging = status.HarvestersReturning, flat = status.HarvestersOutOfCharge;
        // Smooth the readout: relays act every tick but the number is read by eye.
        // The engine turns part of the relay heat into charge; only the rest reaches the radiator.
        _engineOutput += (tankBase.EngineOutputPerTick - _engineOutput) * 0.1;
        _radiatorInflow += (status.RadiatorInflow - _radiatorInflow) * 0.1;
        double ventCapacity = status.VentCapacity;

        float fill = (float)status.HeatStoreFill;
        var fillColor = fill < 0.6f ? TextGood : fill < 0.9f ? TextWarn : TextBad;

        if (!player.IsDead && world.TankBases.CheckBaseCollision(player.Position) == null)
            _hasLeftBase = true;
        if (powered > _lastPoweredRelays)
            _relayLinkedUntil = time + 4f;
        _lastPoweredRelays = powered;

        bool docked = player.IsDockedAtOwnBase;
        int dirt = player.BuildableDirt(docked);
        int relayCost = DirtCost(MachineType.Relay);
        int harvesterCost = DirtCost(MachineType.Harvester);
        int pylonCost = DirtCost(MachineType.Pylon);
        bool fromBaseStore = docked && player.Resources.Dirt < relayCost && dirt >= relayCost;
        string hint;
        Vector4 hintColor;
        if (player.IsDead)
        {
            hint = string.Empty;
            hintColor = TextDim;
        }
        else if (!_hasLeftBase && docked)
        {
            hint = "WASD to drive - the base exit is at the bottom";
            hintColor = TextInfo;
        }
        else if (time < _relayLinkedUntil && !player.IsCarryingMachine)
        {
            hint = pumping > 0
                ? "Relay linked - pumping heat into your base"
                : "Relay linked - idle until the ground near it heats up";
            hintColor = TextGood;
        }
        else if (player.PlantRefusedTicks > 0)
        {
            hint = "Not planted: it would not link - find a clear line";
            hintColor = TextBad;
        }
        else if (player.IsCarryingMachine)
        {
            bool carryingHarvester = player.CarriedMachineType == MachineType.Harvester;
            bool carryingPylon = player.CarriedMachineType == MachineType.Pylon;
            string carriedName = carryingHarvester ? "harvester" : carryingPylon ? "pylon" : "relay";
            if (world.TankBases.CheckBaseCollision(player.Position) != null)
            {
                hint = $"Carrying {carriedName} - leave the base to plant it";
                hintColor = TextInfo;
            }
            else if (carryingHarvester)
            {
                hint = "R: plant harvester here - it mines nearby dirt into your base store";
                hintColor = TextGood;
            }
            else
            {
                var preview = world.LinkMap.PreviewLink(player.Position, world.Terrain);
                (hint, hintColor) = preview.Status switch
                {
                    LinkPreviewStatus.Live => (carryingPylon
                        ? "T: plant pylon here - it charges harvesters it can see"
                        : "E: plant relay here - it will link (green line)", TextGood),
                    LinkPreviewStatus.Blocked => ("Line to network blocked (red X) - dig it clear", TextBad),
                    LinkPreviewStatus.TooFar => ("Too far from the network - move closer", TextWarn),
                    _ => ("No powered network to link to", TextWarn),
                };
            }
        }
        else if (fill >= 0.9f && powered > 0)
        {
            hint = "Base heat store full - relays and docking cool less";
            hintColor = TextWarn;
        }
        else if (dirt >= relayCost)
        {
            hint = fromBaseStore
                ? $"E: build relay from the base store ({relayCost} dirt)"
                : $"E: build relay ({relayCost} dirt)";
            if (dirt >= harvesterCost)
                hint += $"   R: build harvester ({harvesterCost} dirt)";
            if (dirt >= pylonCost && harvesters > 0)
                hint += $"   T: pylon ({pylonCost})";
            hintColor = TextGood;
        }
        else if (docked && _hasLeftBase && player.HeatBudget < Tank.HeatBudgetCapacity - 1f)
        {
            hint = "Docked: cooling down and storing your dirt";
            hintColor = TextInfo;
        }
        else
        {
            int stored = tankBase.Materials.Current.Dirt;
            hint = stored >= relayCost
                ? $"Dig {relayCost - dirt} more dirt, or build a relay at base from the store"
                : $"Dig {relayCost - dirt} more dirt to build a relay";
            hintColor = TextDim;
        }

        ImGui.SetNextWindowPos(new Vector2(vp.x + vp.w - 10f, vp.y + 10f), ImGuiCond.Always, new Vector2(1f, 0f));
        ImGui.SetNextWindowBgAlpha(0.55f);
        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoInputs |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("##HeatNetworkOverlay", flags))
        {
            ImGui.SetWindowFontScale(1.25f);
            ColoredText(player.LivesLeft > 1 ? TextDim : TextBad, $"Lives {player.LivesLeft}");
            ColoredText(TextDim, $"Base dirt store {tankBase.Materials.Dirt}/{tankBase.Materials.DirtCapacity}");

            // Power: the charge store and what the engine is making from relay heat right now.
            float chargeFill = (float)Math.Clamp(tankBase.Charge / tankBase.ChargeCapacity, 0.0, 1.0);
            double perSecond = _engineOutput * Tweaks.Perf.TargetFps;
            ColoredText(chargeFill < 0.1f ? TextBad : chargeFill < 0.3f ? TextWarn : TextInfo,
                $"Charge {tankBase.Charge / 1000.0:0}k / {tankBase.ChargeCapacity / 1000.0:0}k" +
                (perSecond >= 1.0 ? $"   engine +{perSecond / 1000.0:0.0}k/s" : "   engine idle"));
            ImGui.ProgressBar(chargeFill, new Vector2(180f, 8f), string.Empty);

            if (harvesters > 0)
            {
                ColoredText(stalled > 0 ? TextWarn : TextDim,
                    $"Harvesters working {harvesters - stalled - recharging - flat}/{harvesters}" +
                    (stalled > 0 ? $", {stalled} too hot" : ""));
                if (recharging > 0 || flat > 0)
                    ColoredText(flat > 0 ? TextBad : TextWarn,
                        (recharging > 0 ? $"{recharging} heading to a pylon" : "") +
                        (recharging > 0 && flat > 0 ? ", " : "") +
                        (flat > 0 ? $"{flat} out of charge" : ""));
            }
            if (relays > 0)
            {
                ColoredText(powered == relays ? TextDim : TextWarn,
                    $"Relays linked {powered}/{relays}" + (pumping > 0 ? $", {pumping} pumping" : ""));
                ColoredText(fillColor, fill > 0f && fill < 0.01f
                    ? "Base heat store <1% full"
                    : $"Base heat store {(int)MathF.Round(fill * 100f)}% full");

                // Radiator load: heat arriving through relays against the rate the base can vent. Past 100% the
                // store fills, and once it is full the relays are throttled.
                float load = (float)(_radiatorInflow / ventCapacity);
                var loadColor = load < 0.8f ? TextGood : load <= 1.0f ? TextWarn : TextBad;
                ColoredText(loadColor,
                    $"Radiator {(int)Math.Round(_radiatorInflow)} in / {(int)ventCapacity} out per tick");
                ImGui.ProgressBar(Math.Clamp(load / 1.5f, 0f, 1f), new Vector2(180f, 8f), string.Empty);
            }
            if (hint.Length > 0)
            {
                ImGui.Separator();
                ColoredText(hintColor, hint);
            }
            ColoredText(TextDim, "H: heat view");
            ImGui.SetWindowFontScale(1.0f);
        }
        ImGui.End();
    }
}
