namespace Tunnerer.Core.Config;

/// <summary>
/// Injectable configuration for non-thermal world simulation parameters.
/// Tests can construct custom instances; production code uses <see cref="FromTweaks"/>.
/// </summary>
public readonly record struct WorldConfig
{
    public TimeSpan AdvanceStep { get; init; }
    public TimeSpan DirtRecoverInterval { get; init; }
    public int DirtRecoverSpeed { get; init; }
    public int DirtRegrowSpeed { get; init; }
    public int DirtRegrowBlankModifier { get; init; }
    public int DirtRegrowScorchedModifier { get; init; }
    public int DecalDecaySpeed { get; init; }
    public int DigThroughRockChance { get; init; }
    public int MaxPlayers { get; init; }
    public TimeSpan RefreshLinkMapInterval { get; init; }
    public float MaximumLiveLinkDistance { get; init; }
    public float MaximumTheoreticalLinkDistance { get; init; }

    public static WorldConfig FromTweaks() => new()
    {
        AdvanceStep = Tweaks.World.AdvanceStep,
        DirtRecoverInterval = Tweaks.World.DirtRecoverInterval,
        DirtRecoverSpeed = Tweaks.World.DirtRecoverSpeed,
        DirtRegrowSpeed = Tweaks.World.DirtRegrowSpeed,
        DirtRegrowBlankModifier = Tweaks.World.DirtRegrowBlankModifier,
        DirtRegrowScorchedModifier = Tweaks.World.DirtRegrowScorchedModifier,
        DecalDecaySpeed = Tweaks.World.DecalDecaySpeed,
        DigThroughRockChance = Tweaks.World.DigThroughRockChance,
        MaxPlayers = Tweaks.World.MaxPlayers,
        RefreshLinkMapInterval = Tweaks.World.RefreshLinkMapInterval,
        MaximumLiveLinkDistance = Tweaks.World.MaximumLiveLinkDistance,
        MaximumTheoreticalLinkDistance = Tweaks.World.MaximumTheoreticalLinkDistance,
    };
}
