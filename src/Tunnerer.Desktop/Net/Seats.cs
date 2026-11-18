namespace Tunnerer.Desktop.Net;

using Tunnerer.Core;
using Tunnerer.Core.Bots;
using Tunnerer.Core.Config;
using Tunnerer.Core.Input;
using Tunnerer.Core.Match;
using Tunnerer.Protocol;

/// <summary>
/// Who drives each tank of a network match: a player, by the inputs in the bundle, or a bot that every client runs.
/// Only the human mask each bundle carries changes it, so every client, and every replay, hands a seat over at the
/// same tick (docs/late-join-design.md §4.2).
/// </summary>
public sealed class Seats
{
    private readonly BotTankAI?[] _bots;

    /// <param name="bots">
    /// Indexed by slot, one per tank: the bot driving it, or null where a player does. Taken over, not copied: from
    /// the start of the match (<see cref="ForStart"/>) or from a snapshot.
    /// </param>
    public Seats(int matchSeed, BotTankAI?[] bots)
    {
        ArgumentNullException.ThrowIfNull(bots);
        MatchSeed = matchSeed;
        _bots = bots;
        for (int slot = 0; slot < bots.Length; slot++)
            if (bots[slot] == null)
                HumanMask |= 1 << slot;
    }

    /// <summary>
    /// The seats when the match starts: a bot for every tank no player drives, each seeded from the match seed and
    /// its slot, so every client (and a replay) makes the same ones.
    /// </summary>
    public static Seats ForStart(World world, int matchSeed, int humanMask)
    {
        ArgumentNullException.ThrowIfNull(world);
        var bots = new BotTankAI?[world.TankList.Count];
        for (int slot = 0; slot < bots.Length; slot++)
            if ((humanMask & 1 << slot) == 0)
            {
                bots[slot] = new BotTankAI(seed: MatchSetup.BotSeed(matchSeed, slot));
                // Part of the simulation, so every client must mark the same slots: the bot's base gets its
                // stand-in ore income.
                world.SetBotControlled(slot, botControlled: true);
            }
        return new Seats(matchSeed, bots);
    }

    /// <summary>A player in every one of <paramref name="tanks"/> slots, and no bots.</summary>
    public static Seats AllPlayers(int tanks) => new(matchSeed: 0, new BotTankAI?[tanks]);

    public int MatchSeed { get; }

    /// <summary>Bit n is set while a player drives slot n: the mask of the last bundle stepped.</summary>
    public int HumanMask { get; private set; }

    /// <summary>The bots by slot; null where a player drives. What a snapshot saves beside the world.</summary>
    public IReadOnlyList<BotTankAI?> Bots => _bots;

    /// <summary>True when a bot drives the slot: a seat a player joining later may take.</summary>
    public bool IsBotSeat(int slot) => slot >= 0 && slot < _bots.Length && _bots[slot] != null;

    /// <summary>
    /// Steps the world with one bundle, after handing over any seat whose bit in the bundle's mask changed. While no
    /// seat changes, that costs one comparison.
    /// </summary>
    public void Step(World world, TickBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.HumanMask != HumanMask)
            Hand(world, bundle.HumanMask, bundle.Tick);
        SlotStepper.Advance(world, _bots, slot =>
            slot < bundle.Inputs.Length ? ControllerOutputCodec.Decode(bundle.Inputs[slot]) : default);
    }

    /// <summary>
    /// A set bit takes the slot from its bot: the bot goes, and so does its base's stand-in ore income, and the
    /// bundle drives the tank from this tick. A cleared bit gives the slot to a new bot, seeded from the match seed,
    /// the slot and the tick (today's relay never clears one; docs/late-join-design.md §14).
    /// </summary>
    private void Hand(World world, int humanMask, int tick)
    {
        for (int slot = 0; slot < _bots.Length; slot++)
        {
            bool was = (HumanMask & 1 << slot) != 0, now = (humanMask & 1 << slot) != 0;
            if (now && !was)
            {
                _bots[slot] = null;
                world.SetBotControlled(slot, botControlled: false);
            }
            else if (was && !now)
            {
                uint seed = FastRandom.Hash32((uint)MatchSetup.BotSeed(MatchSeed, slot) ^ FastRandom.Hash32((uint)tick));
                _bots[slot] = new BotTankAI(seed: unchecked((int)seed));
                world.SetBotControlled(slot, botControlled: true);
            }
        }
        HumanMask = humanMask;
    }
}
