namespace Tunnerer.Core.Sensing;

using Tunnerer.Core.Config;
using Tunnerer.Core.Types;

/// <summary>What reflected a radar ray.</summary>
public enum RadarEcho : byte
{
    /// <summary>Rock, concrete, hot or frost rock, another side's base wall. Stops the ray.</summary>
    Solid,

    /// <summary>Gold or a machine. Stops the ray.</summary>
    Metal,

    /// <summary>Open ground behind dirt: a tunnel or a cave. A weak return; the ray goes on.</summary>
    Cavity,

    /// <summary>
    /// Hot ground the ray went through, or a reflector too hot to tell what it is. The radar learned only that it
    /// is hot there, and how hot (<see cref="RadarContact.Strength"/>). The ray goes on, weaker.
    /// </summary>
    Heat,

    /// <summary>Something alive: another tank. Stops the ray.</summary>
    Life,

    /// <summary>
    /// Emberite ore cool enough to read. Stops the ray. A speck returns only from near, a deep deposit from further
    /// (<see cref="RadarReach.OreRangeMin"/>, <see cref="RadarReach.OreRangeMax"/>); a lump's warm core and the ground
    /// it heats return <see cref="Heat"/> instead.
    /// </summary>
    Ore,
}

/// <summary>One radar return: the cell that reflected, what kind of thing it was and when (<see cref="Radar.Tick"/>).</summary>
/// <param name="Strength">
/// What was left of the ray when it reflected, 1 for a ray that crossed only cool ground down toward 0 for one
/// that warm ground nearly absorbed. For a <see cref="RadarEcho.Heat"/> contact it is how hot the cell was instead:
/// just above 0 at <see cref="Tweaks.Radar.HeatThreshold"/>, 1 at <see cref="Tweaks.Radar.HeatFullTemperature"/>.
/// </param>
public readonly record struct RadarContact(Position Position, RadarEcho Echo, int BornTick, float Strength = 1f);

/// <summary>Something that is not terrain and reflects radar: a machine or a tank, as a square of cells.</summary>
/// <param name="Alive">A tank or other living thing, which returns <see cref="RadarEcho.Life"/> instead of metal.</param>
public readonly record struct RadarTarget(Position Position, int HalfSize, bool Alive = false);
