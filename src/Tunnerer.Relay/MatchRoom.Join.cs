namespace Tunnerer.Relay;

using Tunnerer.Protocol;

public sealed partial class MatchRoom
{
    public static readonly TimeSpan JoinHoldLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PrepareLimit = TimeSpan.FromMinutes(2);

    /// <summary>
    /// After a hold that ended with nobody seated, the match plays this long before the room admits another
    /// newcomer, and twice as long after each further failure in a row, up to <see cref="LongestFailedJoinWait"/>.
    /// A hold stops the match for every player, so without this a newcomer who never finishes loading (a link too
    /// slow for the copy, or someone who means to) could hold it again as soon as the last hold timed out.
    /// </summary>
    public static readonly TimeSpan FailedJoinWait = TimeSpan.FromSeconds(60);

    /// <summary>The longest the room makes newcomers wait after failed joins.</summary>
    public static readonly TimeSpan LongestFailedJoinWait = TimeSpan.FromMinutes(8);

    private readonly SnapshotBudget _snapshotBudget;
    private readonly bool[] _canUpload;
    private readonly int[] _lastHashTick;
    private IPeer? _watcher;
    private string _watchName = string.Empty;
    private bool _watcherCanUpload;
    private TimeSpan _joinElapsed;
    private SnapshotHold? _hold;
    private bool _offered;
    private bool _closedToNewcomers;
    // The wait the last failed hold set (zero after a join that worked) and the match time it ends at.
    private TimeSpan _failedJoinWait;
    private double _admitFromMatchSeconds;

    // One newcomer at a time. Building their world does not stop the clock.
    private void AdmitWatcher(IPeer peer, string name, SnapshotCapabilities capabilities, out string refusal)
    {
        if (_overAtTick != null || _lockstep.DesyncTick != null)
            refusal = "This match has ended or gone out of sync.";
        else if (_watcher != null)
            refusal = "Another player is joining this match.";
        else if (!Enumerable.Range(0, _peers.Length).Any(_lockstep.IsBotSeat))
            refusal = "Every tank in this match already has a player.";
        else if (!Providers().Any())
            refusal = "No player in this match can supply a snapshot.";
        else if (_closedToNewcomers)
            refusal = "This match is closed to newcomers.";
        else if (_matchSeconds < _admitFromMatchSeconds)
            refusal = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"The last attempt to join this match failed. Try again in {Math.Ceiling(_admitFromMatchSeconds - _matchSeconds)} seconds.");
        else
        {
            refusal = string.Empty;
            _watcher = peer;
            _watchName = UniqueName(string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim());
            _watcherCanUpload = capabilities.HasFlag(SnapshotCapabilities.Upload);
            _joinElapsed = TimeSpan.Zero;
            peer.Send(new Welcome(-1, Config));
            peer.Send(new Roster([.. _names]));
        }
    }

    // The host's switch. Closing refuses a newcomer who is already here, which releases any hold.
    private void SetClosed(bool closed)
    {
        _closedToNewcomers = closed;
        Broadcast(new NewcomersState(closed));
        if (closed && _watcher != null)
        {
            // The host's choice is not a failed join, so it adds nothing to the wait.
            (var wait, double admitFrom) = (_failedJoinWait, _admitFromMatchSeconds);
            RefuseWatcher("This match is closed to newcomers.");
            (_failedJoinWait, _admitFromMatchSeconds) = (wait, admitFrom);
        }
    }

    // A slot is returned exactly when the connection becomes a player. The hub subsequently routes its input
    // and its disconnect through that slot just like a connection that joined in the lobby.
    public int HandleWatcher(IPeer peer, Message message)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(peer, _watcher))
                return -1;
            switch (message)
            {
                case Ping ping:
                    peer.Send(new Pong(ping.Id));
                    break;
                case ReadyToLoad when _hold == null:
                    _joinElapsed = TimeSpan.Zero;
                    RequestUpload();
                    if (_hold != null)
                        Broadcast(new JoinHoldState(_watchName));
                    break;
                case JoinFailed:
                    RefuseWatcher("The client could not load the match.");
                    break;
                case TakeSeat take when _offered && _hold != null:
                    if ((uint)take.Slot >= (uint)_peers.Length || !_lockstep.Seat(take.Slot))
                    {
                        peer.Send(new SeatRefused("That tank already has a player."));
                        break;
                    }
                    _peers[take.Slot] = peer;
                    _names[take.Slot] = _startNames[take.Slot] = _watchName;
                    _canUpload[take.Slot] = _watcherCanUpload;
                    peer.Send(new Seated(take.Slot, _hold.Tick));
                    if (IsPaused)
                        peer.Send(new PauseState(true, _pausedBySlot));
                    Broadcast(new Roster([.. _names]));
                    EndJoin(seated: true);
                    return take.Slot;
            }
            return -1;
        }
    }

    public void LeaveWatcher(IPeer peer)
    {
        lock (_lock)
            if (ReferenceEquals(peer, _watcher))
                EndJoin();
    }

    private IEnumerable<int> Providers() => Enumerable.Range(0, _peers.Length)
        .Where(slot => _peers[slot] != null && _canUpload[slot])
        .OrderByDescending(slot => _lastHashTick[slot]);

    private void RequestUpload()
    {
        var providers = Providers().Take(2).ToArray();
        if (providers.Length == 0)
        {
            RefuseWatcher("No player is available to copy the match.");
            return;
        }
        ReleaseUpload();
        _hold = new SnapshotHold(_lockstep.NextTick, providers[0], providers.Length == 2 ? providers[1] : null,
            MessageCodec.MaxSnapshotBytes);
        foreach (int slot in providers)
            _peers[slot]!.Send(new SnapshotRequest(_hold.Tick, SendChunks: slot == _hold.Uploader));
    }

    private void ProviderLeft(int slot)
    {
        if (_watcher == null)
            return;
        if (!Providers().Any() && !_offered)
            RefuseWatcher("The last player able to copy the match left.");
        else if (_hold is { } hold && !_offered)
        {
            if (hold.Uploader == slot && hold.NeedsUpload)
                RequestUpload();
            else if (hold.Verifier == slot && hold.NeedsVerification)
            {
                int? next = Providers().Where(s => s != hold.Uploader).Cast<int?>().FirstOrDefault();
                hold.ReplaceVerifier(next);
                if (next is int verifier)
                    _peers[verifier]!.Send(new SnapshotRequest(hold.Tick, SendChunks: false));
                OfferIfReady();
            }
        }
    }

    internal bool ExpectsUpload(int slot)
    {
        lock (_lock)
            return _hold is { NeedsUpload: true } hold && hold.Uploader == slot;
    }

    private void ReceiveChunk(int slot, SnapshotChunk chunk)
    {
        if (_hold is not { } hold || slot != hold.Uploader || chunk.Tick != hold.Tick || !hold.NeedsUpload)
            return;
        if (!_snapshotBudget.TryReserve(chunk.Bytes.Length))
        {
            RefuseWatcher("The relay is busy copying other matches.");
            return;
        }
        if (!hold.AddChunk(slot, chunk))
        {
            _snapshotBudget.Release(chunk.Bytes.Length);
            RefuseWatcher("The snapshot chunks did not fit the requested upload.");
        }
    }

    private void FinishReport(int slot, SnapshotDone done)
    {
        if (_hold?.Finish(slot, done) != true)
            return;
        _lockstep.SubmitHash(done.Tick, done.SyncHash);
        _lockstep.SubmitSnapshotDigest(done.Tick, done.Digest);
        ReportDesync();
        OfferIfReady();
    }

    private void ReportDesync()
    {
        if (_lockstep.DesyncTick is not int tick || _desyncSent)
            return;
        _desyncSent = true;
        Broadcast(new Desync(tick));
        RefuseWatcher("The match copies disagree.");
    }

    private void OfferIfReady()
    {
        if (_offered || _hold?.Offer is not { } offer)
            return;
        _offered = true;
        _watcher!.Send(offer);
        foreach (var chunk in _hold.Chunks)
            _watcher.Send(chunk);
    }

    private bool AdvanceJoinClock(TimeSpan elapsed)
    {
        if (_watcher == null)
            return false;
        bool held = _hold != null;
        _joinElapsed += elapsed;
        if (_joinElapsed >= (held ? JoinHoldLimit : PrepareLimit))
            RefuseWatcher(held ? "Copying the match timed out." : "Preparing to join timed out.");
        return held;
    }

    private void RefuseWatcher(string reason)
    {
        if (_watcher == null)
            return;
        _watcher.Send(new Refusal(reason));
        _watcher.Close();
        EndJoin();
    }

    private void EndJoin(bool seated = false)
    {
        if (_hold != null)
        {
            Broadcast(new JoinHoldState(string.Empty));
            // Only a hold costs the players anything: a newcomer who leaves before asking for one is not counted.
            _failedJoinWait = seated ? TimeSpan.Zero
                : _failedJoinWait == TimeSpan.Zero ? FailedJoinWait
                : TimeSpan.FromTicks(Math.Min(LongestFailedJoinWait.Ticks, _failedJoinWait.Ticks * 2));
            _admitFromMatchSeconds = _matchSeconds + _failedJoinWait.TotalSeconds;
        }
        ReleaseUpload();
        _watcher = null;
        _watchName = string.Empty;
        _offered = false;
    }

    private void ReleaseUpload()
    {
        if (_hold != null)
            _snapshotBudget.Release(_hold.Length);
        _hold = null;
    }
}
