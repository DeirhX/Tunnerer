namespace Emberward.Core;

using Emberward.Core.Entities.Mobs;
using Emberward.Core.Rendering;
using Emberward.Core.Types;

public partial class World
{
    /// <summary>The emberlings (<see cref="Match.MatchRules.Mobs"/>); null when the rules leave them out.</summary>
    public MobSwarm? Mobs => _mobs;

    /// <summary>
    /// Whether ore at <paramref name="cell"/> belongs to a nest that still has creatures. Harvesters pass over guarded ore
    /// (they work it once the nest is dead); a tank may still dig it, which wakes the nest.
    /// </summary>
    public bool IsOreGuarded(Position cell) => _mobs?.IsGuarded(cell) ?? false;

    /// <summary>Writes the creatures out of their nests for drawing; returns how many (<see cref="MobRenderState"/>).</summary>
    public int CopyMobRenderStates(Span<MobRenderState> destination) => _mobs?.CopyRenderStates(destination) ?? 0;

    /// <summary>Writes the nests for drawing; returns how many (<see cref="NestRenderState"/>).</summary>
    public int CopyNestRenderStates(Span<NestRenderState> destination) => _mobs?.CopyNestRenderStates(destination) ?? 0;
}
