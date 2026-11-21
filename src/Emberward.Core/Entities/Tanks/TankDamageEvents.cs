namespace Emberward.Core.Entities.Tanks;

using Emberward.Core.Types;

/// <summary>Records what a hit that has just landed did to a tank, as step events.</summary>
public static class TankDamageEvents
{
    /// <summary>
    /// Records the shield's part of <paramref name="tank"/>'s last hit (<see cref="StepEventKind.TankShieldHit"/>, and
    /// <see cref="StepEventKind.TankShieldBroken"/> when it emptied the shield) at <paramref name="at"/>, where the hit
    /// landed. With <paramref name="hullEvent"/> also <see cref="StepEventKind.TankHit"/> (at the tank) when the hull took some.
    /// A hit the shield stopped has its own events, sound and pulse.
    /// </summary>
    public static void Record(StepEventLog? log, Tank tank, Position at, bool hullEvent)
    {
        if (log == null)
            return;
        var taken = tank.LastDamageTaken;
        // A hit that empties the shield is recorded as the break alone (its sound is the zap and the shatter in one).
        if (taken.ShieldBroken)
            log.Record(new StepEvent(StepEventKind.TankShieldBroken, at, tank.Color, Count: taken.Shield));
        else if (taken.Shield > 0)
            log.Record(new StepEvent(StepEventKind.TankShieldHit, at, tank.Color, Count: taken.Shield));
        if (hullEvent && taken.Hull > 0)
            log.Record(new StepEvent(StepEventKind.TankHit, tank.Position, tank.Color));
    }
}
