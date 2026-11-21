namespace Emberward.Core;

using Emberward.Core.Entities.Emberites;
using Emberward.Core.Rendering;
using Emberward.Core.Types;

public partial class World
{
    /// <summary>The emberites (<see cref="Match.MatchRules.Emberites"/>); null when the rules leave them out.</summary>
    public EmberiteSwarm? Emberites => _emberites;

    /// <summary>
    /// Whether ore at <paramref name="cell"/> belongs to a nest that still has creatures. Harvesters pass over guarded ore
    /// (they work it once the nest is dead); a tank may still dig it, which wakes the nest.
    /// </summary>
    public bool IsOreGuarded(Position cell) => _emberites?.IsGuarded(cell) ?? false;

    /// <summary>Writes the creatures out of their nests for drawing; returns how many (<see cref="EmberiteRenderState"/>).</summary>
    public int CopyEmberiteRenderStates(Span<EmberiteRenderState> destination) => _emberites?.CopyRenderStates(destination) ?? 0;

    /// <summary>Writes the nests for drawing; returns how many (<see cref="NestRenderState"/>).</summary>
    public int CopyNestRenderStates(Span<NestRenderState> destination) => _emberites?.CopyNestRenderStates(destination) ?? 0;
}
