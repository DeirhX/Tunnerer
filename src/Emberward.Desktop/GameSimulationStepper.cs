namespace Emberward.Desktop;

using System.Diagnostics;
using System.Globalization;
using Emberward.Core;
using Emberward.Core.Input;
using Emberward.Core.Types;
using Emberward.Desktop.Input;
using Emberward.Desktop.Net;
using Emberward.Core.Bots;

/// <summary>
/// Runs one simulation step: each tank takes its input from the source its slot has. The local slot is this machine's
/// keyboard, mouse and scripted input; a slot with a bot asks the bot; any other slot idles.
/// </summary>
public sealed class GameSimulationStepper
{
    private readonly World _world;
    private readonly int _localSlot;

    /// <summary>Times the local input's trip through the relay. Null unless `EMBERWARD_NET_LATENCY_LOG=1`.</summary>
    private readonly InputLatencyMeter? _latencyMeter =
        Environment.GetEnvironmentVariable("EMBERWARD_NET_LATENCY_LOG") == "1" ? new InputLatencyMeter() : null;
    private readonly KeyboardController _localController;
    private readonly IReadOnlyList<BotTankAI?> _bots;
    private readonly ScriptedInputConfig _scriptedInput;
    private readonly InputRecorder _inputRecorder;
    private readonly Action<GameCommand, string> _executeCommand;
    private readonly Action<string> _requestScreenshot;

    /// <param name="bots">Indexed by slot; null where no bot drives the tank.</param>
    public GameSimulationStepper(
        World world,
        int localSlot,
        KeyboardController localController,
        IReadOnlyList<BotTankAI?> bots,
        ScriptedInputConfig scriptedInput,
        InputRecorder inputRecorder,
        Action<GameCommand, string> executeCommand,
        Action<string> requestScreenshot)
    {
        _world = world;
        _localSlot = localSlot;
        _localController = localController;
        _bots = bots;
        _scriptedInput = scriptedInput;
        _inputRecorder = inputRecorder;
        _executeCommand = executeCommand;
        _requestScreenshot = requestScreenshot;
    }

    public void AdvanceOneStep(int simFrame, DirectionF? aimDirection, bool mouseShoot)
    {
        ApplyScriptCommandsForFrame(simFrame);

        // Quantised as it would be on the network, so a match plays the same alone, replayed or shared.
        ControllerOutput local = ControllerOutputCodec.Quantize(SampleLocalInput(simFrame, aimDirection, mouseShoot));
        SlotStepper.Advance(_world, _bots, slot => slot == _localSlot ? local : default);

        _inputRecorder.RecordFrame(local.MoveSpeed, local.ShootPrimary);
        if (_scriptedInput.ShouldCaptureScreenshot(simFrame))
            _requestScreenshot($"script_frame_{simFrame:D4}");
    }

    /// <summary>
    /// One step of a network match: the next tick the relay sent is simulated, and the local player's input goes to
    /// the relay to take effect in a later tick. False, with nothing done, when no tick is waiting.
    /// </summary>
    /// <param name="sendInput">
    /// False while this client is replaying ticks from the past to catch up: what the player presses now belongs
    /// to the present, and thousands of inputs at once would only pile up at the relay.
    /// </param>
    public bool AdvanceNetStep(
        int simFrame, DirectionF? aimDirection, bool mouseShoot, NetSession session, bool sendInput = true)
    {
        if (session.Buffered == 0)
            return false;
        ApplyScriptCommandsForFrame(simFrame);

        ControllerOutput local = SampleLocalInput(simFrame, aimDirection, mouseShoot);
        int tick = _world.AdvanceCount;
        session.TryStep();
        // One input per tick simulated keeps this client sending at exactly the relay's rate.
        if (sendInput)
        {
            session.SendInput(local);
            _latencyMeter?.Sent(ControllerOutputCodec.Encode(local), tick, Stopwatch.GetTimestamp());
        }

        // What was simulated for this tank, not what the keyboard says now: that is what a replay must reproduce.
        uint simulatedBits = session.Played[^1].Inputs[_localSlot];
        ControllerOutput simulated = ControllerOutputCodec.Decode(simulatedBits);
        if (sendInput && _latencyMeter?.Applied(simulatedBits, tick, Stopwatch.GetTimestamp()) is { } sample)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[Latency] input sent at tick {sample.SentTick}, applied at tick {sample.AppliedTick}: " +
                $"{sample.Ticks} ticks, {sample.Elapsed.TotalMilliseconds:F0} ms"));
        _inputRecorder.RecordFrame(simulated.MoveSpeed, simulated.ShootPrimary);
        if (_scriptedInput.ShouldCaptureScreenshot(simFrame))
            _requestScreenshot($"script_frame_{simFrame:D4}");
        return true;
    }

    private ControllerOutput SampleLocalInput(int simFrame, DirectionF? aimDirection, bool mouseShoot)
    {
        var kb = _localController.Poll();
        ControllerOutput scripted = _scriptedInput.Controller?.GetOutputAtFrame(simFrame) ?? default;
        return new ControllerOutput
        {
            MoveSpeed = _scriptedInput.Controller is null ? kb.MoveSpeed : scripted.MoveSpeed,
            ShootPrimary = kb.ShootPrimary || mouseShoot || scripted.ShootPrimary,
            BuildPrimary = kb.BuildPrimary || scripted.BuildPrimary,
            BuildSecondary = kb.BuildSecondary || scripted.BuildSecondary,
            BuildTertiary = kb.BuildTertiary || scripted.BuildTertiary,
            BuildQuaternary = kb.BuildQuaternary || scripted.BuildQuaternary,
            Recall = kb.Recall || scripted.Recall,
            Upgrade = kb.Upgrade != 0 ? kb.Upgrade : scripted.Upgrade,
            AimDirection = aimDirection ?? default,
        };
    }

    public void ProcessScriptedCommands(int frame)
    {
        ApplyScriptCommandsForFrame(frame);
        if (_scriptedInput.ShouldCaptureScreenshot(frame))
            _requestScreenshot($"script_frame_{frame:D4}");
    }

    private void ApplyScriptCommandsForFrame(int frame)
    {
        if (!_scriptedInput.TryGetCommandsForFrame(frame, out IReadOnlyList<GameCommand> commands))
            return;

        for (int i = 0; i < commands.Count; i++)
            _executeCommand(commands[i], InputCommandSources.Script);
    }
}
