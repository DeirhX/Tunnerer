namespace Tunnerer.Desktop.Net;

using Tunnerer.Core;
using Tunnerer.Core.Match;
using Tunnerer.Core.Config;
using Tunnerer.Core.Input;
using Tunnerer.Protocol;
using Tunnerer.Core.Bots;

/// <summary>
/// One client's part in a network match. It sends the local player's input to the relay, collects the bundles the
/// relay sends back, steps the world only from those bundles, and reports a hash of the world now and then so the
/// relay can tell when clients have drifted apart.
/// </summary>
/// <remarks>
/// The local tank is driven by the bundle like every other, never by the keyboard directly: what the relay put in
/// the bundle is what every client simulates.
/// </remarks>
public sealed class NetSession
{
    /// <summary>A hash is reported every this many ticks: once a second.</summary>
    public const int HashIntervalTicks = Tweaks.Perf.TargetFps;

    private readonly ILockstepLink _link;
    private readonly World _world;
    private IReadOnlyList<BotTankAI?> _bots = [];
    private readonly BundleBuffer _buffer = new();
    private readonly List<TickBundle> _played = [];
    private readonly Queue<(int Tick, SyncDigest Digest)> _digests = new();
    private bool _matchOverSent;

    /// <param name="world">A world built from the match settings and not yet stepped.</param>
    public NetSession(ILockstepLink link, World world)
    {
        if (world.AdvanceCount != 0)
            throw new ArgumentException("A network session starts from a world that has not been stepped.", nameof(world));
        _link = link;
        _world = world;
    }

    /// <summary>
    /// The bots that drive the slots no player took, indexed by slot (null where a player drives). Which slots
    /// those are is known only when the match starts, so set this then, before the first step. Every client must
    /// make them from the same seeds.
    /// </summary>
    public IReadOnlyList<BotTankAI?> Bots
    {
        get => _bots;
        set
        {
            if (_world.AdvanceCount != 0)
                throw new InvalidOperationException("Bots are chosen before the first tick.");
            _bots = value;
        }
    }

    /// <summary>Every bundle simulated so far, in order: with the match settings, the whole match.</summary>
    public IReadOnlyList<TickBundle> Played => _played;

    /// <summary>The last few digests reported to the relay, oldest first, for a desync report.</summary>
    public IReadOnlyCollection<(int Tick, SyncDigest Digest)> RecentDigests => _digests;

    /// <summary>How many reported digests are kept.</summary>
    public const int DigestsKept = 12;

    /// <summary>Bundles received and not yet simulated.</summary>
    public int Buffered => _buffer.Count;

    /// <summary>Sends the local player's input for one step.</summary>
    public void SendInput(in ControllerOutput input) => _link.SendInput(ControllerOutputCodec.Encode(input));

    /// <summary>Moves every bundle that has arrived into the buffer.</summary>
    public void Receive()
    {
        while (_link.TryReceive(out TickBundle? bundle))
            _buffer.Add(bundle);
    }

    /// <summary>How many steps to simulate now; see <see cref="BundleBuffer.StepsToTake"/>.</summary>
    public int StepsToTake(int dueByClock, int maxSteps) => _buffer.StepsToTake(dueByClock, maxSteps);

    /// <summary>Simulates the next buffered tick. False when none is buffered.</summary>
    public bool TryStep()
    {
        if (!_buffer.TryTake(out TickBundle? bundle))
            return false;
        if (bundle.Tick != _world.AdvanceCount)
            throw new InvalidOperationException(
                $"The world is at tick {_world.AdvanceCount} but the next bundle is for tick {bundle.Tick}.");

        Step(_world, _bots, bundle);
        _played.Add(bundle);
        if (_world.AdvanceCount % HashIntervalTicks == 0)
        {
            SyncDigest digest = WorldStateHash.ComputeSync(_world, _bots);
            _digests.Enqueue((_world.AdvanceCount, digest));
            if (_digests.Count > DigestsKept)
                _digests.Dequeue();
            _link.SendHash(_world.AdvanceCount, digest.Combined);
        }
        // The relay does not simulate, so it has to be told: a finished match gives its room up for the next one.
        if (_world.IsMatchOver && !_matchOverSent)
        {
            _matchOverSent = true;
            _link.SendMatchOver(_world.AdvanceCount);
        }
        return true;
    }

    /// <summary>
    /// The bots of a match: one for every tank no player drives, each seeded from the match seed and its slot, so
    /// every client (and a replay) makes the same ones.
    /// </summary>
    public static BotTankAI?[] CreateBots(World world, int matchSeed, int humanMask)
    {
        var bots = new BotTankAI?[world.TankList.Count];
        for (int slot = 0; slot < bots.Length; slot++)
            if ((humanMask & 1 << slot) == 0)
            {
                bots[slot] = new BotTankAI(seed: MatchSetup.BotSeed(matchSeed, slot));
                // Part of the simulation, so every client must mark the same slots: the bot's base gets its
                // stand-in ore income.
                world.SetBotControlled(slot, botControlled: true);
            }
        return bots;
    }

    /// <summary>
    /// Steps a world with one bundle. Also how a recorded match is replayed, and how a returning client catches up
    /// from the relay's log.
    /// </summary>
    public static void Step(World world, IReadOnlyList<BotTankAI?> bots, TickBundle bundle) =>
        SlotStepper.Advance(world, bots, slot =>
            slot < bundle.Inputs.Length ? ControllerOutputCodec.Decode(bundle.Inputs[slot]) : default);
}
