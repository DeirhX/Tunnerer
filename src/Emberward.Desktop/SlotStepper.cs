namespace Emberward.Desktop;

using Emberward.Core;
using Emberward.Core.Input;
using Emberward.Core.Bots;
using Emberward.Core.Entities.Tanks;

/// <summary>
/// Advances the world one step with each tank driven by its slot's source: a slot with a bot asks the bot, every
/// other slot takes the input given for it. A single-player step and a network step both go through here, so a bot
/// behaves the same in either.
/// </summary>
public static class SlotStepper
{
    /// <param name="bots">Indexed by slot; null (or past the end) where no bot drives the tank.</param>
    /// <param name="inputForSlot">The input of a slot without a bot.</param>
    public static void Advance(World world, IReadOnlyList<BotTankAI?> bots, Func<int, ControllerOutput> inputForSlot)
    {
        IReadOnlyList<Tank> tanks = world.TankList.Tanks;
        world.Advance(slot => slot < bots.Count && bots[slot] is { } bot
            ? bot.GetInput(tanks[slot], BotTankAI.PickEnemy(tanks, slot), world)
            : inputForSlot(slot));
    }
}
