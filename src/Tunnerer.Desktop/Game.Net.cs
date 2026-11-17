namespace Tunnerer.Desktop;

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Input;
using Tunnerer.Core.Rendering;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Gui;
using Tunnerer.Desktop.Net;

/// <summary>
/// A network match: the world steps only from the relay's bundles, never from the local clock or keyboard directly
/// (docs/multiplayer-design.md).
/// </summary>
public partial class Game
{
    /// <summary>How often a network match logs its tick and world hash, for comparing clients' logs.</summary>
    private const int NetLogIntervalTicks = 240;

    /// <summary>The connection and session of a network match; both null in a local one.</summary>
    private readonly RelayConnection? _relay;
    private NetSession? _netSession;

    private bool IsNetMatch => _relay != null;

    /// <summary>
    /// A network match cannot stop for one player: the others' ticks keep coming. The pause menu still opens, over
    /// a running game.
    /// </summary>
    private bool SimulationPaused => _paused && !IsNetMatch;

    /// <summary>
    /// Nothing in the world is moving: a local match is paused, or the relay is holding a network match for
    /// everyone. Sound loops and animation that runs on frame time stop with it. The frame loop itself keeps
    /// running in a held network match, because the connection still has to be served.
    /// </summary>
    private bool WorldStandsStill => SimulationPaused || _relay is { Started: true, IsPaused: true };

    /// <summary>Tells the relay this client's world is built, with its hash, and gets ready to step from bundles.</summary>
    private void StartNetSession(RelayConnection relay)
    {
        _netSession = new NetSession(relay, _world);
        _hud.CanRestartMatch = false;
        ulong hash = WorldStateHash.Compute(_world);
        relay.SendReady(hash);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[Net] Slot {relay.Slot}, seed {relay.Config.Seed}, world hash {hash:X16}. Waiting for the match to start."));
    }

    /// <summary>
    /// The network match's share of one clock step: takes in what the relay sent and simulates the ticks that are
    /// due, sending one input for each. With nothing from the relay the world waits.
    /// </summary>
    private void AdvanceNetSteps(RelayConnection relay, NetSession session, DirectionF? aimDirection, bool mouseShoot)
    {
        relay.Pump();
        if (relay.IsPaused != _netPauseLogged)
        {
            _netPauseLogged = relay.IsPaused;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[Net] Match {(relay.IsPaused ? "paused" : "resumed")} at tick {_world.AdvanceCount}."));
        }
        if (relay.DesyncTick is int desyncTick && !_desyncReported)
            ReportDesync(relay, session, desyncTick);
        if (relay.Failure != null || relay.DesyncTick != null)
            return;
        if (relay.Started && _slotNames.Length == 0)
            BeginNetMatch(relay, session);
        // In the lobby of an automated run, and while the match is paused, the script clock keeps counting, as it
        // does in a local pause, so a command script can start the match (StartNetMatch) or resume it.
        if ((!relay.Started || relay.IsPaused) && _automatedRun && session.Buffered == 0)
            AdvanceScriptClock();

        session.Receive();
        // Far behind (just rejoined, or stalled): replay as many ticks as fit in the frame's budget, silently and
        // without sending input, until the present is in reach again.
        bool catchingUp = session.Buffered > NetworkTweaks.CatchUpTicks;
        int steps = catchingUp ? int.MaxValue : session.StepsToTake(dueByClock: 1, MaxCatchUpSimulationSteps);
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < steps; i++)
        {
            if (catchingUp && (session.Buffered <= BundleBufferTarget ||
                Stopwatch.GetElapsedTime(started).TotalMilliseconds > NetworkTweaks.CatchUpBudgetMs))
                break;
            if (!_simulationStepper.AdvanceNetStep(
                    _simFrameCounter, aimDirection, mouseShoot, session, sendInput: !catchingUp))
                break;
            AfterSimulationStep(silent: catchingUp);
            if (_world.AdvanceCount == _faultTick)
            {
                // Development only (TUNNERER_NET_FAULT_TICK): warm a patch of ground on this machine alone, to see
                // a desync found and reported. The patch is outside the base, whose cells are pinned at ambient.
                var at = _world.TankList.Tanks[_localSlot].Position + new Offset(0, Tweaks.Base.BaseSize);
                _world.Terrain.AddHeatRadius(at, 20, 3);
                Console.WriteLine($"[Net] Fault injected at tick {_faultTick}.");
            }
            // The session digested the world at this tick already (the interval is a multiple of its own).
            if (_world.AdvanceCount % NetLogIntervalTicks == 0 && session.RecentDigests.LastOrDefault() is { } logged &&
                logged.Tick == _world.AdvanceCount)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[Net] tick {_world.AdvanceCount} hash {logged.Digest.State:X16} buffered {session.Buffered}"));
        }
    }

    private const int BundleBufferTarget = Tunnerer.Protocol.BundleBuffer.TargetDepth;
    private bool _desyncReported;
    private bool _netPauseLogged;
    private readonly int _faultTick =
        int.TryParse(Environment.GetEnvironmentVariable("TUNNERER_NET_FAULT_TICK"), out int tick) ? tick : -1;

    /// <summary>
    /// The relay found two clients' worlds different. Writes what this client knows to a file: the match as a
    /// recording that replays it exactly, the digests it reported around the tick, and where its tanks stand.
    /// Comparing two players' files shows which part of the world went apart, and when.
    /// </summary>
    private void ReportDesync(RelayConnection relay, NetSession session, int desyncTick)
    {
        _desyncReported = true;
        string path = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(), "artifacts", "debug",
            string.Create(CultureInfo.InvariantCulture, $"desync_{SafeFileName(relay.Room)}_tick{desyncTick}_slot{relay.Slot}.txt")));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var writer = new StreamWriter(path);
            DesyncReport.Write(writer, new MatchRecording(relay.Config, relay.HumanMask, session.Played),
                desyncTick, relay.Slot, NetLaunch.BuildId, session.RecentDigests, _world);
            Console.WriteLine($"[Net] Out of sync at tick {desyncTick}. Report written to {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Net] Out of sync at tick {desyncTick}. The report could not be written: {e.Message}");
        }
    }

    private static string SafeFileName(string text) =>
        string.Concat(text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    /// <summary>What each tank is called in this match: the player's name, or "Bot". Empty until the match starts.</summary>
    private string[] _slotNames = [];

    /// <summary>
    /// The relay has started the match: the slots nobody took get bots, made from the match seed so every client's
    /// bots are the same, and the names for the tags are fixed.
    /// </summary>
    private void BeginNetMatch(RelayConnection relay, NetSession session)
    {
        int tanks = _world.TankList.Count;
        _slotNames = new string[tanks];
        for (int slot = 0; slot < tanks; slot++)
        {
            bool human = (relay.HumanMask & 1 << slot) != 0;
            _slotNames[slot] = !human ? "Bot"
                : slot < relay.Roster.Length && relay.Roster[slot].Length > 0 ? relay.Roster[slot]
                : "Player " + (slot + 1).ToString(CultureInfo.InvariantCulture);
        }
        session.Bots = NetSession.CreateBots(_world, Seed, relay.HumanMask);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[Net] Match {(relay.Rejoined ? "rejoined" : "started")}: {string.Join(", ", _slotNames)}."));
    }

    private static uint SlotColor(int slot)
    {
        Color c = TankSprites.GetPixelColor(1, slot);
        return HudTheme.Rgba(c.R, c.G, c.B);
    }

    /// <summary>
    /// The network overlays of a frame: the lobby before the start, a warning plate when something is wrong, and a
    /// name over every other tank in sight.
    /// </summary>
    private void DrawNetOverlays(ReadOnlySpan<PositionF> drawnTankPositions)
    {
        if (_relay is not { } relay)
            return;

        if (!relay.Started && relay.Failure == null)
        {
            DrawLobby(relay);
            return;
        }

        var winner = _world.MatchWinner?.Tank;
        _hud.WinnerName = winner != null && winner.Color < _slotNames.Length ? _slotNames[winner.Color] : null;
        DrawNameTags(drawnTankPositions);

        var play = _hud.PlayRect;
        var anchor = new Vector2(play.x + play.w * 0.5f, play.y + 18f);
        string? notice = NetNotice(relay, _netSession?.Buffered ?? 0, out bool bad);
        if (notice == null)
        {
            if (relay.LatencyMs is int ms)
                HudTheme.DrawTag(ImGui.GetForegroundDrawList(), anchor + new Vector2(0f, 8f),
                    string.Create(CultureInfo.InvariantCulture, $"NET {ms} ms"),
                    ms < 80 ? HudTheme.Good : ms < 160 ? HudTheme.Warn : HudTheme.Danger, 13f);
            return;
        }

        uint? accent = bad ? HudTheme.Danger : null;
        if (HudTheme.BeginWindow("##NetStatus", anchor, new Vector2(0.5f, 0f), 520f, accent))
        {
            HudTheme.TitleRow("NETWORK MATCH", 19f, accent);
            HudTheme.WrappedRow(notice, HudTheme.Text);
        }
        HudTheme.EndWindow();
    }

    /// <summary>
    /// What a running match has to tell the player, or null when all is well: it is over or out of sync
    /// (<paramref name="bad"/>), paused, or this machine is behind and catching up.
    /// </summary>
    public static string? NetNotice(RelayConnection relay, int buffered, out bool bad)
    {
        bad = relay.Failure != null || relay.DesyncTick != null;
        if (relay.Status is { } status)
            return status;
        if (buffered <= NetworkTweaks.BehindWarningTicks)
            return null;
        int seconds = buffered / Tweaks.Perf.TargetFps;
        if (relay.Rejoined)
            return string.Create(CultureInfo.InvariantCulture, $"Rejoining: replaying the match, {seconds} s to go.");
        bad = true;
        return string.Create(CultureInfo.InvariantCulture,
            $"This computer is not keeping up with the match: {seconds} s behind.");
    }

    /// <summary>Who is in the room, slot by slot in tank colours, and how the match gets started.</summary>
    private static void DrawLobby(RelayConnection relay)
    {
        var view = ImGui.GetMainViewport();
        var anchor = new Vector2(view.Pos.X + view.Size.X * 0.5f, view.Pos.Y + view.Size.Y * 0.22f);
        if (HudTheme.BeginWindow("##NetLobby", anchor, new Vector2(0.5f, 0f), 520f))
        {
            HudTheme.TitleRow($"NETWORK MATCH    room {relay.Room}", 19f);
            var dl = ImGui.GetWindowDrawList();
            for (int slot = 0; slot < relay.Config.Slots; slot++)
            {
                string name = slot < relay.Roster.Length ? relay.Roster[slot] : string.Empty;
                bool taken = name.Length > 0;
                var pos = ImGui.GetCursorScreenPos();
                dl.AddRectFilled(pos + new Vector2(0f, 3f), pos + new Vector2(18f, 21f), SlotColor(slot));
                string label = !taken ? "open - a bot plays if nobody joins"
                    : slot == relay.Slot ? name + "  (you)"
                    : name;
                HudTheme.TextAt(dl, 18f, pos + new Vector2(28f, 2f), label, taken ? HudTheme.Text : HudTheme.TextDim);
                ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, 26f));
            }
            ImGui.Dummy(new Vector2(1f, 4f));
            HudTheme.WrappedRow(LobbyHint(relay), HudTheme.Amber);
        }
        HudTheme.EndWindow();
    }

    /// <summary>What the player in the lobby should do, or is waiting for.</summary>
    public static string LobbyHint(RelayConnection relay)
    {
        int here = relay.PlayersHere;
        if (here >= relay.Config.Slots)
            return "Everyone is here. Starting...";
        if (relay.IsHost)
            return here == 1
                ? "Press Enter to start alone against bots, or wait for players."
                : $"Press Enter to start with {here} players; bots take the open slots.";
        int host = relay.HostSlot;
        string hostName = host >= 0 && host < relay.Roster.Length ? relay.Roster[host] : "the host";
        return $"Waiting for {hostName} to start the match.";
    }

    /// <summary>The name of every other living tank the player can see, over the tank, in its colour.</summary>
    private void DrawNameTags(ReadOnlySpan<PositionF> drawnTankPositions)
    {
        var tanks = _world.TankList.Tanks;
        var dl = ImGui.GetForegroundDrawList();
        for (int slot = 0; slot < tanks.Count && slot < drawnTankPositions.Length && slot < _slotNames.Length; slot++)
        {
            // Names follow the same rule as the tanks themselves: nothing is shown of a tank out of sight.
            if (slot == _localSlot || tanks[slot].IsDead || !Rendering.Entities.Sight.Sees(_vision, tanks[slot].Position))
                continue;
            Vector2 at = _hud.CellToScreen(drawnTankPositions[slot].X + 0.5f, drawnTankPositions[slot].Y - 5f);
            if (_hud.IsInView(at))
                HudTheme.DrawTag(dl, at, _slotNames[slot], SlotColor(slot), 14f);
        }
    }
}
