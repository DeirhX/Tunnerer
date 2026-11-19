namespace Emberward.Core.Input;

using Emberward.Core.Types;
using Emberward.Core.Entities.Bases;

public struct ControllerOutput
{
    public Offset MoveSpeed;
    public bool ShootPrimary;
    public bool Recall;
    public DirectionF AimDirection;
    public bool BuildPrimary;
    /// <summary>Second build key: builds (and plants) a harvester instead of a relay.</summary>
    public bool BuildSecondary;
    /// <summary>Third build key: builds (and plants) a pylon.</summary>
    public bool BuildTertiary;
    /// <summary>Fourth build key: builds a local generator in energy matches.</summary>
    public bool BuildQuaternary;
    /// <summary>Upgrade key held: 0 for none, 1-7 for <see cref="BaseUpgrade"/> + 1. Bought while docked.</summary>
    public int Upgrade;
}
