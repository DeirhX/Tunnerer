namespace Tunnerer.Desktop.Rendering.Entities;

using Tunnerer.Core.Rendering;
using Tunnerer.Core.Types;

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
