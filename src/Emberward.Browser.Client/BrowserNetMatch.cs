namespace Emberward.Browser.Client;

using System.Diagnostics;
using System.Globalization;
using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Input;
using Emberward.Core.LevelGen;
using Emberward.Core.Rendering;
using Emberward.Core.Types;
using Emberward.Desktop.Config;
using Emberward.Desktop.Net;
using Emberward.Protocol;

/// <summary>
/// The browser's part in a network match (docs/multiplayer-design.md), the same as the desktop's: the world steps
/// only from the relay's bundles, through the desktop's <see cref="NetSession"/>, so a browser and a desktop of one
/// build play in one room. The page owns the WebSocket: it hands every frame it receives to <see cref="Receive"/>
/// and sends what <see cref="TakeOutgoing"/> gives it.
/// </summary>
public sealed class BrowserNetMatch
{
    /// <summary>The most ticks one frame simulates at the normal rate, as on the desktop (<c>Game</c>).</summary>
    public const int MaxStepsPerFrame = 4;

    /// <summary>The most build and upgrade presses held for the next steps.</summary>
    private const int MaxQueuedActions = 16;

    private readonly Hello _hello;
    private readonly int _heatWorkers;
    private readonly List<byte[]> _outbox = [];
    private readonly Queue<ControllerOutput> _actions = new();
    private bool _lastSentAction;
    // The recall key as last seen: only its press is queued, while holding it carries in the held controls.
    private bool _recallDown;
    private string? _failure;
    private bool _begun;

    /// <param name="proposed">The match this client opens if the room does not exist yet.</param>
    /// <param name="heatWorkers">Threads for the heat solver (<see cref="BrowserSession"/>).</param>
    public BrowserNetMatch(string room, string name, MatchConfig proposed, int heatWorkers = 1)
    {
        // The public relay is open: the browser sends no secret (Hello's default).
        _hello = new Hello(MessageCodec.ProtocolVersion, NetMatch.BuildId, name, room, proposed);
        _heatWorkers = heatWorkers;
        _outbox.Add(MessageCodec.Encode(_hello));
    }

    /// <summary>
    /// The match the browser proposes when it opens a room, by the desktop's rule: its map for this many players
    /// unless one is asked for, and a new seed each match unless one is given.
    /// </summary>
    public static MatchConfig Propose(int seed, int players, Size? mapSize = null)
    {
        players = Math.Clamp(players, 2, Tweaks.World.MaxPlayers);
        Size size = mapSize ?? (players > 2 ? NetworkTweaks.LargeMatchMapSize : DesktopScreenTweaks.RenderSurfaceSize);
        return NetMatch.ProposeConfig(seed, size, LevelGenMode.Deterministic, players);
    }

    /// <summary>The room and what the relay has said; null until it has let this client in.</summary>
    public RoomClient? Room { get; private set; }

    /// <summary>This player's world, built from the room's settings when the relay lets the client in.</summary>
    public BrowserSession? Session { get; private set; }

    public NetSession? Net { get; private set; }

    /// <summary>Why the connection is over (refused, or lost), or null while it is alive.</summary>
    public string? Failure => Room?.Failure ?? _failure;

    /// <summary>Something the page shows has changed since the last <see cref="Status"/>: the lobby, the pause, a failure.</summary>
    public bool Changed { get; private set; } = true;

    /// <summary>Takes in one message from the relay, which arrived at <paramref name="arrived"/> by the page's clock.</summary>
    public void Receive(ReadOnlySpan<byte> frame, TimeSpan arrived)
    {
        Message message;
        try
        {
            message = MessageCodec.Decode(frame);
        }
        catch (InvalidDataException e)
        {
            Lose($"the relay sent something this game does not understand ({e.Message}).");
            return;
        }
        if (message is not Tick)
            Changed = true;
        if (Room is { } room)
        {
            room.Accept(message, arrived);
            return;
        }
        switch (message)
        {
            case Welcome { Slot: -1 }:
                Lose("This browser cannot load a running match yet.");
                break;
            case Welcome welcome when NetMatch.CannotBuild(welcome.Config) is { } reason:
                _failure ??= reason;
                break;
            case Welcome welcome:
                Session = BrowserSession.ForNetwork(welcome.Config, welcome.Slot, _heatWorkers);
                Room = new RoomClient(_hello, welcome, Send);
                Net = new NetSession(Room, Session.World);
                Room.SendReady(WorldStateHash.ComputeReady(Session.World));
                break;
            case Refusal refusal:
                _failure ??= refusal.Reason;
                break;
        }
    }

    /// <summary>The page's socket closed, for <paramref name="reason"/> (for the player).</summary>
    public void Lose(string reason)
    {
        Changed = true;
        if (Room is { } room)
            room.Lose(reason);
        else
            _failure ??= reason;
    }

    /// <summary>What to send to the relay, in order, since the last call.</summary>
    public IReadOnlyList<byte[]> TakeOutgoing()
    {
        if (_outbox.Count == 0)
            return [];
        byte[][] taken = [.. _outbox];
        _outbox.Clear();
        return taken;
    }

    /// <summary>Asks the relay to start with the players who are here; only the host's request counts.</summary>
    public void RequestStart()
    {
        if (Room is { Started: false, Failure: null } room)
            room.RequestStart();
    }

    /// <summary>Asks the relay to hold the match for everyone, or to let it run again.</summary>
    public void TogglePause()
    {
        if (Room is { Started: true, Failure: null } room)
            room.SetPaused(!room.IsPaused);
    }

    /// <summary>Closes the running match to newcomers, or opens it again; only the host's request counts.</summary>
    public void SetClosedToNewcomers(bool closed)
    {
        if (Room is { Started: true, IsHost: true, Failure: null } room)
            room.SetClosedToNewcomers(closed);
    }

    /// <summary>
    /// The match's share of one display frame, as on the desktop (<c>Game.AdvanceNetSteps</c>): simulates the ticks
    /// that have arrived and sends one input for each. Far behind (just rejoined, or the page was hidden) it replays
    /// as many ticks as fit in <see cref="NetworkTweaks.CatchUpBudgetMs"/>, silently and without sending input.
    /// </summary>
    /// <param name="input">The player's controls now. A build or upgrade press waits for a step to carry it.</param>
    /// <param name="dueByClock">How many steps the page's clock says are due.</param>
    /// <param name="now">The page's clock, as <see cref="Receive"/> is given it.</param>
    /// <returns>How many ticks were simulated.</returns>
    public int Advance(ControllerOutput input, int dueByClock, TimeSpan now)
    {
        if (Room is not { } room || Net is not { } net || Session is not { } session)
            return 0;
        room.KeepTime(now);
        if (room.Failure != null || room.DesyncTick != null)
            return 0;
        if (room.Started && !_begun)
        {
            net.StartSeats(room.Config.Seed, room.HumanMask);
            _begun = true;
            Changed = true;
        }
        int previousMask = net.HumanMask;
        Hold(input);

        net.Receive();
        bool catchingUp = net.Buffered > NetworkTweaks.CatchUpTicks;
        int steps = catchingUp ? int.MaxValue : net.StepsToTake(dueByClock, MaxStepsPerFrame);
        long started = Stopwatch.GetTimestamp();
        int taken = 0;
        while (taken < steps)
        {
            if (catchingUp && (net.Buffered <= BundleBuffer.TargetDepth ||
                Stopwatch.GetElapsedTime(started).TotalMilliseconds > NetworkTweaks.CatchUpBudgetMs))
                break;
            if (!net.TryStep())
                break;
            taken++;
            // One input per tick simulated keeps this client sending at exactly the relay's rate.
            if (!catchingUp)
                net.SendInput(NextInput(input));
            session.AfterStep(silent: catchingUp);
        }
        Changed |= previousMask != net.HumanMask;
        return taken;
    }

    /// <summary>
    /// Keeps a build, upgrade or recall press until a step carries it: a frame may simulate no tick at all. Recall is
    /// also a held control (held off the network it keeps burning emberite, TankReserveSystem), so only the frame it
    /// goes down is a press; the frames it stays down add nothing to the queue.
    /// </summary>
    private void Hold(ControllerOutput input)
    {
        bool recallPressed = input.Recall && !_recallDown;
        _recallDown = input.Recall;
        if ((input.BuildPrimary || input.BuildSecondary || input.BuildTertiary || input.BuildQuaternary || input.BuildQuinary || recallPressed || input.Upgrade != 0) &&
            _actions.Count < MaxQueuedActions)
            _actions.Enqueue(input with { Recall = recallPressed });
    }

    /// <summary>
    /// The input for the next tick: the controls held now, with the next held press. A tick without one follows
    /// every press, because Core reads a press as the change from one tick's input to the next. A held recall key
    /// stays down through those ticks too.
    /// </summary>
    private ControllerOutput NextInput(ControllerOutput held)
    {
        var next = held;
        next.BuildPrimary = next.BuildSecondary = next.BuildTertiary = next.BuildQuaternary = next.BuildQuinary = false;
        next.Upgrade = 0;
        next.UpgradeMachine = false;
        if (_lastSentAction)
        {
            _lastSentAction = false;
            return next;
        }
        if (_actions.TryDequeue(out var press))
        {
            _lastSentAction = true;
            next.BuildPrimary = press.BuildPrimary;
            next.BuildSecondary = press.BuildSecondary;
            next.BuildTertiary = press.BuildTertiary;
            next.BuildQuaternary = press.BuildQuaternary;
            next.BuildQuinary = press.BuildQuinary;
            next.Upgrade = press.Upgrade;
            next.UpgradeMachine = press.UpgradeMachine;
            next.Recall = press.Recall || held.Recall;
        }
        return next;
    }

    private void Send(Message message) => _outbox.Add(MessageCodec.Encode(message));

    /// <summary>What the page shows of the network match; clears <see cref="Changed"/>.</summary>
    public NetStatus Status()
    {
        Changed = false;
        if (Room is not { } room)
            return new NetStatus(_hello.Room, -1, _hello.Config.Slots, [], false, false, false, null,
                Failure is { } failure ? $"Could not join: {failure}" : "Connecting to the relay...", Failure != null,
                null, null, null);
        int buffered = Net?.Buffered ?? 0;
        var roster = new NetSlot[room.Config.Slots];
        for (int slot = 0; slot < roster.Length; slot++)
        {
            string name = _begun ? room.DriverName(slot, Net!.HumanMask)
                : slot < room.Roster.Length ? room.Roster[slot] : string.Empty;
            Color c = TankSprites.GetPixelColor(1, slot);
            roster[slot] = new NetSlot(name, string.Create(CultureInfo.InvariantCulture, $"#{c.R:x2}{c.G:x2}{c.B:x2}"),
                slot == room.Slot, name.Length == 0);
        }
        var winner = Session?.World.MatchWinner?.Tank;
        string? winnerName = winner != null && _begun ? room.DriverName(winner.Color, Net!.HumanMask) : null;
        return new NetStatus(room.Room, room.Slot, room.Config.Slots, roster, room.Started, room.IsHost, room.IsPaused,
            room.LatencyMs, Notice(room, buffered, out bool bad), bad,
            LobbyText.AssignedNameLine(room.RequestedName, room.AssignedName), room.Started ? null : LobbyHint(room),
            winnerName, room.IsClosedToNewcomers, room.Started && room.IsHost);
    }

    /// <summary>
    /// What a running match has to tell the player, or null when all is well: it is over or out of sync
    /// (<paramref name="bad"/>), paused, or this machine is behind and catching up.
    /// </summary>
    public static string? Notice(RoomClient room, int buffered, out bool bad)
    {
        ArgumentNullException.ThrowIfNull(room);
        bad = room.Failure != null || room.DesyncTick != null;
        if (room.Failure is { } failure)
            return room.Started && room.DesyncTick == null
                ? $"Disconnected: {failure} Join again under the same name to come back to the match."
                : $"Disconnected: {failure}";
        if (room.DesyncTick is int tick)
            return string.Create(CultureInfo.InvariantCulture,
                $"The players' games went out of sync at tick {tick}. The match cannot continue.");
        if (!room.Started)
            return null;
        if (room.JoiningName.Length > 0)
            return $"Copying the match to {room.JoiningName}.";
        if (room.IsPaused)
            return $"Paused by {room.PausedByName}. P resumes the match for everyone.";
        if (buffered <= NetworkTweaks.BehindWarningTicks)
            return null;
        int seconds = buffered / Tweaks.Perf.TargetFps;
        if (room.Rejoined)
            return string.Create(CultureInfo.InvariantCulture, $"Rejoining: replaying the match, {seconds} s to go.");
        bad = true;
        return string.Create(CultureInfo.InvariantCulture,
            $"This device is not keeping up with the match: {seconds} s behind.");
    }

    /// <summary>What the player in the lobby should do, or is waiting for.</summary>
    public static string LobbyHint(RoomClient room)
    {
        ArgumentNullException.ThrowIfNull(room);
        int here = room.PlayersHere;
        if (here >= room.Config.Slots)
            return "Everyone is here. Starting...";
        if (room.IsHost)
            return here == 1
                ? "Start now to play against bots, or wait for players to join this room."
                : string.Create(CultureInfo.InvariantCulture,
                    $"Start now with {here} players; bots take the open slots.");
        int host = room.HostSlot;
        string hostName = host >= 0 && host < room.Roster.Length ? room.Roster[host] : "the host";
        return $"Waiting for {hostName} to start the match.";
    }
}

/// <summary>A network match as the page shows it: the lobby before the start, then the notices and the round trip.</summary>
/// <param name="Slot">The tank this player drives; -1 until the relay has let the client in.</param>
/// <param name="Roster">Every slot: who is in it (in the lobby) or drives it (once started: a name, or "Bot").</param>
/// <param name="Notice">What is wrong or holding the match, or null while it plays.</param>
/// <param name="Bad">The notice is a failure: disconnected, out of sync, or falling behind.</param>
/// <param name="NameLine">The name the relay gave this player, when it is not the one asked for.</param>
/// <param name="LobbyHint">In the lobby: how the match gets started.</param>
/// <param name="Winner">The name of the player whose base won, once the match is decided.</param>
/// <param name="ClosedToNewcomers">The host has closed the running match to newcomers.</param>
/// <param name="CanCloseToNewcomers">This player is the host of a started match, so may change that.</param>
public sealed record NetStatus(
    string Room, int Slot, int Slots, NetSlot[] Roster, bool Started, bool IsHost, bool Paused, int? LatencyMs,
    string? Notice, bool Bad, string? NameLine, string? LobbyHint, string? Winner,
    bool ClosedToNewcomers = false, bool CanCloseToNewcomers = false);

/// <param name="Color">The tank's colour, as #rrggbb.</param>
public sealed record NetSlot(string Name, string Color, bool You, bool Open);
