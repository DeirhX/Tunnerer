namespace Emberward.Core.Entities.Machines;

using Emberward.Core.Network;

/// <summary>
/// What a machine would do if planted at a spot (see <see cref="World.PreviewPlacement"/>): the link it would make
/// to the network, for machines that work through links, or the ground heat a harvester would sense there.
/// </summary>
/// <param name="Link">The link it would make; default for a harvester, which does not link.</param>
/// <param name="SenseHeat">
/// A harvester's ground temperature there as a fraction of its stall temperature (see <see cref="Machine.SenseHeat"/>);
/// zero for other machines.
/// </param>
public readonly record struct MachinePlacementPreview(LinkPreview Link, float SenseHeat);
