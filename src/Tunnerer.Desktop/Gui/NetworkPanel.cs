namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Tanks;
using static Tunnerer.Desktop.Gui.HudDraw;

/// <summary>
/// Top-right readout: lives, the relay network (how many relays are linked, how full the base reservoir is)
/// and a context hint for the build key that explains what E will do, or why a carried relay will not link.
/// </summary>
public sealed class NetworkPanel
{
    private int _lastPoweredRelays;
    private float _relayLinkedUntil = -1f;
    private double _radiatorInflow;
    private double _engineOutput;

    private static Vector4 LevelColor(StatusLevel level, Vector4 ok) => level switch
    {
        StatusLevel.Ok => ok,
        StatusLevel.Warn => TextWarn,
        _ => TextBad,
    };

    private static Vector4 ToneColor(HintTone tone) => tone switch
    {
        HintTone.Info => TextInfo,
        HintTone.Good => TextGood,
        HintTone.Warn => TextWarn,
        HintTone.Bad => TextBad,
        _ => TextDim,
    };

    public void Draw((float x, float y, float w, float h) vp, Tank player, World world, float time)
    {
        var tankBase = player.Base;
        if (vp.w <= 0 || vp.h <= 0 || tankBase == null)
            return;

        var status = world.GetNetworkStatus(tankBase);
        int relays = status.Relays, powered = status.PoweredRelays, pumping = status.PumpingRelays;
        int harvesters = status.Harvesters, stalled = status.HarvestersTooHot, working = status.HarvestersWorking;
        int recharging = status.HarvestersReturning, flat = status.HarvestersOutOfCharge;
        // Smooth the readout: relays act every tick but the number is read by eye.
        // The engine turns part of the relay heat into charge; only the rest reaches the radiator.
        _engineOutput += (tankBase.EngineOutputPerTick - _engineOutput) * 0.1;
        _radiatorInflow += (status.RadiatorInflow - _radiatorInflow) * 0.1;
        double ventCapacity = status.VentCapacity;

        float fill = (float)status.HeatStoreFill;
        var fillColor = LevelColor(status.HeatStoreLevel, TextGood);

        bool insideBase = world.IsInsideAnyBase(player.Position);
        if (powered > _lastPoweredRelays)
            _relayLinkedUntil = time + 4f;
        _lastPoweredRelays = powered;

        bool docked = player.IsDockedAtOwnBase;
        var carried = player.CarriedMachineType;
        var relayQuote = player.QuoteBuild(MachineType.Relay);
        var (hint, tone) = HintSelector.Select(new HintInputs(
            IsDead: player.IsDead,
            HasLeftBase: player.HasLeftBase,
            Docked: docked,
            InsideBase: insideBase,
            RelayJustLinked: time < _relayLinkedUntil,
            PumpingRelays: pumping,
            PlantRefused: player.PlantRefusedTicks > 0,
            Carried: carried,
            CarriedLink: world.CarriedPlantLink(player),
            HeatStoreFull: status.IsHeatStoreFullWithRelays,
            Harvesters: harvesters,
            SpendableOre: relayQuote.Stock,
            RelayFromBaseStore: relayQuote.Payer == OrePayer.BaseStore,
            NotEnoughOre: player.BuildRefusedTicks > 0 && player.LastBuildRefusal == BuildRefusal.NotEnoughOre,
            CoolingDown: player.IsCoolingDown));
        var hintColor = ToneColor(tone);

        if (HudTheme.BeginWindow("##HeatNetworkOverlay", new Vector2(vp.x + vp.w - 14f, vp.y + 14f), new Vector2(1f, 0f), 400f))
        {
            HudTheme.TitleRow($"BASE        lives {player.LivesLeft}", 18f, player.LivesLeft > 1 ? HudTheme.Amber : HudTheme.Danger);
            GoalLines.Draw(world, tankBase);
            ColoredText(TextInfo, $"Base ore store {tankBase.Store.Ore}" +
                (player.IsUnloadingOre ? "   unloading" : player.IsInOwnNetwork ? "   in network" : ""));

            // Power: the charge store and what the engine is making from relay heat right now.
            float chargeFill = (float)tankBase.ChargeFill;
            double perSecond = _engineOutput * Tweaks.Perf.TargetFps;
            ColoredText(LevelColor(tankBase.ChargeLevel, TextInfo),
                $"Charge {tankBase.Charge / 1000.0:0}k / {tankBase.ChargeCapacity / 1000.0:0}k" +
                (perSecond >= 1.0 ? $"   engine +{perSecond / 1000.0:0.0}k/s" : "   engine idle"));
            HudTheme.BarRow(chargeFill, HudTheme.Teal, null, 10f);

            if (harvesters > 0)
            {
                ColoredText(stalled > 0 ? TextWarn : TextDim,
                    $"Harvesters working {working}/{harvesters}" +
                    (stalled > 0 ? $", {stalled} too hot" : ""));
                if (status.HarvestersOffNetwork > 0 || status.HarvestersNoOre > 0)
                    ColoredText(TextWarn,
                        (status.HarvestersOffNetwork > 0 ? $"{status.HarvestersOffNetwork} off the network" : "") +
                        (status.HarvestersOffNetwork > 0 && status.HarvestersNoOre > 0 ? ", " : "") +
                        (status.HarvestersNoOre > 0 ? $"{status.HarvestersNoOre} out of ore" : ""));
                if (status.HarvestersCoreOnly > 0)
                    ColoredText(TextWarn, $"{status.HarvestersCoreOnly} at a warm core: torch it out with the tank");
                if (status.HarvestersStoreFull > 0)
                    ColoredText(TextWarn, $"{status.HarvestersStoreFull} waiting: the base ore store is full");
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
                float load = tankBase.RadiatorLoad(_radiatorInflow);
                var loadColor = LevelColor(TankBase.RadiatorLoadLevel(load), TextGood);
                ColoredText(loadColor,
                    $"Radiator {(int)Math.Round(_radiatorInflow)} in / {(int)ventCapacity} out per tick");
                HudTheme.BarRow(Math.Clamp(load / 1.5f, 0f, 1f),
                    load < 0.8f ? HudTheme.Good : load <= 1.0f ? HudTheme.Warn : HudTheme.Danger, null, 10f);
            }
            if (hint.Length > 0)
            {
                ImGui.Separator();
                HudTheme.WrappedRow(hint, ImGui.ColorConvertFloat4ToU32(hintColor), 16f);
            }
            HudTheme.KeyRow("H", "heat view", HudTheme.TextDim, 15f);
        }
        HudTheme.EndWindow();
    }
}
