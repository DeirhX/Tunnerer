namespace Tunnerer.Desktop;

using System.Globalization;
using System.Text;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Input;

public partial class Game
{
    /// <summary>
    /// Logs which scripted input, commands, screenshots and recording the environment asked for, starts an
    /// autostarted recording, and returns whether the enemy tank is driven by its AI.
    /// </summary>
    private bool StartMatchInput(bool enableAiTanks)
    {
        if (_scriptedInput.HasScriptedController())
            Console.WriteLine("[Input] Scripted controller enabled via TUNNERER_SCRIPTED_INPUT.");
        if (_scriptedInput.ScreenshotFrameCount > 0)
            Console.WriteLine($"[Input] Scripted screenshot frames enabled ({_scriptedInput.ScreenshotFrameCount} frame(s)).");
        if (_scriptedInput.CommandFrameCount > 0)
            Console.WriteLine($"[Input] Scripted commands enabled via TUNNERER_COMMAND_SCRIPT ({_scriptedInput.CommandFrameCount} frame slot(s)).");
        if (_scriptedInput.RecordPath is not null)
            Console.WriteLine($"[Input] Scripted input recording path: {_scriptedInput.RecordPath}");
        if (_scriptedInput.RecordAutoStart)
        {
            Console.WriteLine("[Input] Scripted input recording autostart enabled.");
            _inputRecorder.Start();
        }

        bool aiTanks = enableAiTanks && (!_scriptedInput.HasScriptedController() || _scriptedInput.KeepAi);
        if (enableAiTanks && !aiTanks)
            Console.WriteLine("[Input] AI tanks forced OFF during scripted replay.");
        Console.WriteLine($"[Input] AI tanks: {(aiTanks ? "enabled" : "disabled")}");
        return aiTanks;
    }

    private void ToggleInputRecording(string source)
    {
        if (_inputRecorder.IsRecording)
        {
            StopAndPersistInputRecording(
                source,
                stoppedPrefix: "[Input] Scripted input recording stopped",
                emptyMessage: $"[Input] Scripted input recording stopped [source={source}] (no input/commands captured).");
            return;
        }

        _inputRecorder.Start();
        Console.WriteLine($"[Input] Scripted input recording started [source={source}] at frame {_simFrameCounter}.");
    }

    private void FlushInputRecordingOnExit()
    {
        if (!_inputRecorder.IsRecording)
            return;

        StopAndPersistInputRecording(
            source: null,
            stoppedPrefix: "[Input] Scripted input recording auto-saved on exit",
            emptyMessage: null);
    }

    private string ResolveRecordOutputPath()
    {
        if (!string.IsNullOrWhiteSpace(_scriptedInput.RecordPath))
            return _scriptedInput.RecordPath;

        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "debug", $"scripted_input_{ts}.txt"));
    }

    private static void SaveRecordedScript(string path, RecordingCapture capture)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var text = new StringBuilder();
        text.AppendLine("# Scripted input recording");
        text.AppendLine(CultureInfo.InvariantCulture, $"# Generated: {DateTime.Now:O}");
        if (!string.IsNullOrWhiteSpace(capture.InputScript))
            text.AppendLine(CultureInfo.InvariantCulture, $"TUNNERER_SCRIPTED_INPUT={capture.InputScript}");
        if (!string.IsNullOrWhiteSpace(capture.CommandScript))
            text.AppendLine(CultureInfo.InvariantCulture, $"TUNNERER_COMMAND_SCRIPT={capture.CommandScript}");
        File.WriteAllText(path, text.ToString());
    }

    private void StopAndPersistInputRecording(string? source, string stoppedPrefix, string? emptyMessage)
    {
        string path = ResolveRecordOutputPath();
        RecordingCapture capture = _inputRecorder.StopAndSerialize();
        if (string.IsNullOrWhiteSpace(capture.InputScript) && string.IsNullOrWhiteSpace(capture.CommandScript))
        {
            if (!string.IsNullOrWhiteSpace(emptyMessage))
                Console.WriteLine(emptyMessage);
            return;
        }

        SaveRecordedScript(path, capture);
        if (!string.IsNullOrWhiteSpace(source))
            Console.WriteLine($"{stoppedPrefix} [source={source}] -> {path}");
        else
            Console.WriteLine($"{stoppedPrefix} -> {path}");

        if (!string.IsNullOrWhiteSpace(capture.InputScript))
            Console.WriteLine($"[Input] Replay with TUNNERER_SCRIPTED_INPUT=\"{capture.InputScript}\"");
        if (!string.IsNullOrWhiteSpace(capture.CommandScript))
            Console.WriteLine($"[Input] Replay with TUNNERER_COMMAND_SCRIPT=\"{capture.CommandScript}\"");
    }

    private void RecordInputCommandForReplay(GameCommand command, string source)
    {
        _inputRecorder.RecordCommand(_simFrameCounter, command, source);
    }
}
