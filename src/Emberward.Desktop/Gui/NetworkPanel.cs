namespace Emberward.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Entities.Bases;
using Emberward.Core.Network;
using static Emberward.Desktop.Gui.HudDraw;

/// <summary>
/// Top-right readout: base armor, the relay network (how many relays are linked, how full the base reservoir is)
/// and a context hint for the build key that explains what E will do, or why a carried relay will not link.
/// </summary>
public sealed class NetworkPanel
{
    private double _radiatorInflow;
    private double _engineOutput;

    private static uint ToneColor(HintTone tone) => tone switch
    {
        HintTone.Info => HudTheme.Teal,
        HintTone.Good => HudTheme.Good,
        HintTone.Warn => HudTheme.Warn,
        HintTone.Bad => HudTheme.Danger,
        HintTone.Accent => HudTheme.Amber,
        _ => HudTheme.Text,
    };

    /// <param name="hint">The context hint (<see cref="Guidance.PlayerGuidance.Hint"/>).</param>
    public void Draw((float x, float y, float w, float h) vp, Tank player, World world, (string Text, HintTone Tone) hint)
    {
        var tankBase = player.Base;
        if (vp.w <= 0 || vp.h <= 0 || tankBase == null)
            return;

        var status = world.GetNetworkStatus(tankBase);
        int relays = status.Relays, powered = status.PoweredRelays, pumping = status.PumpingRelays;
        int harvesters = status.Harvesters, stalled = status.HarvestersTooHot, working = status.HarvestersWorking;
        int flat = status.HarvestersOutOfCharge;
        // Smooth the readout: relays act every tick but the number is read by eye.
        // The engine turns part of the relay heat into charge; only the rest reaches the radiator.
        _engineOutput += (tankBase.EngineOutputPerTick - _engineOutput) * 0.1;
        _radiatorInflow += (status.RadiatorInflow - _radiatorInflow) * 0.1;
        double ventCapacity = status.VentCapacity;

        float fill = (float)status.HeatStoreFill;
        var fillColor = HudTheme.LevelColor(status.HeatStoreLevel, HudTheme.Good);

        var hintColor = ToneColor(hint.Tone);

        if (HudTheme.BeginWindow("##HeatNetworkOverlay", new Vector2(vp.x + vp.w - 14f, vp.y + 14f), new Vector2(1f, 0f), 400f))
        {
            HudTheme.TitleRow($"BASE        armor {tankBase.ArmorFill:P0}", 18f, tankBase.IsDestroyed ? HudTheme.Danger : HudTheme.Amber);
            GoalLines.Draw(world, tankBase);
            ColoredText(HudTheme.Teal, $"Base ore store {tankBase.Store.Ore}" +
                (player.IsUnloadingOre ? "   unloading" : player.IsInOwnNetwork ? "   in network" : ""));

            // Power: the charge store and what the engine is making from relay heat right now.
            float chargeFill = (float)tankBase.ChargeFill;
            double perSecond = _engineOutput * Tweaks.Perf.TargetFps;
            ColoredText(HudTheme.LevelColor(tankBase.ChargeLevel, HudTheme.Teal),
                $"Charge {tankBase.Charge / 1000.0:0}k / {tankBase.ChargeCapacity / 1000.0:0}k" +
                (perSecond >= 1.0 ? $"   engine +{perSecond / 1000.0:0.0}k/s" : "   engine idle"));
            HudTheme.BarRow(chargeFill, HudTheme.Teal, null, 10f);
            // With the energy network (DESIGN.md §4.9): what the engine and generators make against what the island draws.
            if (world.BasePowerOf(tankBase) is BasePower basePower)
            {
                var power = PowerLabels.Of(basePower);
                ColoredText(power.Color, $"Network {power.Flow}   {power.Net}");
                ColoredText(power.Color, power.Shield);
            }

            if (harvesters > 0)
            {
                ColoredText(stalled > 0 ? HudTheme.Warn : HudTheme.Text,
                    $"Harvesters working {working}/{harvesters}" +
                    (stalled > 0 ? $", {stalled} too hot" : ""));
                if (status.HarvestersOffNetwork > 0 || status.HarvestersNoOre > 0)
                    ColoredText(HudTheme.Warn,
                        (status.HarvestersOffNetwork > 0 ? $"{status.HarvestersOffNetwork} off the network" : "") +
                        (status.HarvestersOffNetwork > 0 && status.HarvestersNoOre > 0 ? ", " : "") +
                        (status.HarvestersNoOre > 0 ? $"{status.HarvestersNoOre} out of ore" : ""));
                if (status.HarvestersCoreOnly > 0)
                    ColoredText(HudTheme.Warn, $"{status.HarvestersCoreOnly} at a warm core: torch it out with the tank");
                if (status.HarvestersStoreFull > 0)
                    ColoredText(HudTheme.Warn, $"{status.HarvestersStoreFull} waiting: the base ore store is full");
                if (flat > 0)
                    ColoredText(HudTheme.Danger, $"{flat} out of charge");
            }
            if (relays > 0)
            {
                ColoredText(powered == relays ? HudTheme.Text : HudTheme.Warn,
                    $"Relays linked {powered}/{relays}" + (pumping > 0 ? $", {pumping} pumping" : ""));
                ColoredText(fillColor, fill > 0f && fill < 0.01f
                    ? "Base heat store <1% full"
                    : $"Base heat store {(int)MathF.Round(fill * 100f)}% full");

                // Radiator load: heat arriving through relays against the rate the base can vent. Past 100% the
                // store fills, and once it is full the relays are throttled.
                float load = tankBase.RadiatorLoad(_radiatorInflow);
                var loadColor = HudTheme.LevelColor(TankBase.RadiatorLoadLevel(load), HudTheme.Good);
                ColoredText(loadColor,
                    $"Radiator {(int)Math.Round(_radiatorInflow)} in / {(int)ventCapacity} out per tick");
                HudTheme.BarRow(Math.Clamp(load / 1.5f, 0f, 1f), loadColor, null, 10f);
            }
            if (hint.Text.Length > 0)
            {
                ImGui.Separator();
                HudTheme.WrappedRow(hint.Text, hintColor, 16f);
            }
            HudTheme.KeyRow("H", "heat view", HudTheme.TextDim, 15f);
            if (world.Energy != null)
                HudTheme.KeyRow("G", "charge labels", HudTheme.TextDim, 15f);
        }
        HudTheme.EndWindow();
    }
}
