namespace Emberward.Desktop.Rendering.Entities;

using Emberward.Core.Rendering;
using Emberward.Core.Types;
using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;

/// <summary>Reads one value of a machine's render state for its shader.</summary>
public delegate float MachineReading(in MachineRenderState machine);

/// <summary>
/// How the entity layer draws one machine type, planted or carried, and its placement preview.
/// <see cref="MachineQuads"/> and <see cref="PreviewQuads"/> read it instead of switching on the type.
/// </summary>
/// <param name="Kind">The body's shader shape (an <see cref="EntityShape"/> <c>Kind*</c> constant).</param>
/// <param name="HalfLength">Half the body quad along its facing, in cells.</param>
/// <param name="HalfWidth">Half the body quad across its facing, in cells.</param>
/// <param name="FacesHeading">
/// The body turns to the machine's heading and its parameters are the heading (P0, P1) and a drum angle that turns
/// while it works (P2). Otherwise it stands upright with P0 = powered, P1 = <paramref name="Gauge"/>, P2 = phase.
/// </param>
/// <param name="Gauge">An upright body's P1; unused when <paramref name="FacesHeading"/>.</param>
/// <param name="SenseRadius">
/// Radius of the ground ring it senses, drawn round it while planted and as its preview while carried; null for none.
/// </param>
/// <param name="ShowsBattery">Planted, it shows a battery gauge above it and a charge beam while a pylon fills it.</param>
/// <param name="Reach">
/// The ground its preview tints while carried. A machine with a reach also previews the link it would make; one
/// without previews only its sense ring.
/// </param>
public sealed record MachineSprite(
    float Kind,
    float HalfLength,
    float HalfWidth,
    bool FacesHeading = false,
    MachineReading? Gauge = null,
    float? SenseRadius = null,
    bool ShowsBattery = false,
    MachineReach? Reach = null);

/// <summary>The ground a carried machine's preview tints.</summary>
/// <param name="Radius">Reach in cells.</param>
/// <param name="Color">Tint of the area.</param>
/// <param name="Circle">A circle (true) or a rounded square (false).</param>
public sealed record MachineReach(float Radius, Color Color, bool Circle);

/// <summary>Host-independent machine shapes, shared by desktop and WebGL.</summary>
public static class MachineSprites
{
    public static MachineSprite Relay { get; } = new(EntityShape.KindRelay, HalfLength: 3.6f, HalfWidth: 3.6f,
        Gauge: static (in MachineRenderState m) => m.PumpFraction,
        Reach: new MachineReach(Tweaks.Machine.RelayCoolingRadius, Tweaks.Colors.Relay, Circle: false));

    public static MachineSprite Harvester { get; } = new(EntityShape.KindHarvester, HalfLength: 3.4f, HalfWidth: 3.0f,
        FacesHeading: true, SenseRadius: Tweaks.Machine.HarvesterHeatSenseRadius + 1.5f, ShowsBattery: true);

    public static MachineSprite Pylon { get; } = new(EntityShape.KindPylon, HalfLength: 3.6f, HalfWidth: 3.6f,
        Gauge: static (in MachineRenderState m) => m.IsGivingCharge ? 1f : 0f,
        Reach: new MachineReach(Tweaks.Machine.PylonRange, Tweaks.Colors.Pylon, Circle: true));

    public static MachineSprite Generator { get; } = new(EntityShape.KindGenerator, HalfLength: 3.8f, HalfWidth: 3.8f,
        Gauge: static (in MachineRenderState m) => m.GenerationFraction,
        Reach: new MachineReach(Tweaks.Machine.GeneratorIntakeRadius, Tweaks.Colors.Generator, Circle: false));

    /// <summary>Every machine's sprite, indexed by <see cref="MachineType"/>.</summary>
    public static IReadOnlyList<MachineSprite> All { get; } = BuildTable(
        (MachineType.Relay, Relay), (MachineType.Harvester, Harvester), (MachineType.Pylon, Pylon), (MachineType.Generator, Generator));

    public static MachineSprite Get(MachineType type) =>
        (uint)type < (uint)All.Count ? All[(int)type] : throw new ArgumentOutOfRangeException(nameof(type));

    private static MachineSprite[] BuildTable(params (MachineType Type, MachineSprite Sprite)[] entries)
    {
        var table = new MachineSprite[Enum.GetValues<MachineType>().Length];
        foreach (var (type, sprite) in entries)
            table[(int)type] = sprite;
        if (Array.IndexOf(table, null) >= 0)
            throw new InvalidOperationException("Every MachineType needs a MachineSprite.");
        return table;
    }
}
