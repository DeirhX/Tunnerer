namespace Tunnerer.Core.Input;

using Tunnerer.Core.Types;
using Tunnerer.Core.Entities.Bases;

public struct ControllerOutput
{
    public Offset MoveSpeed;
    public bool ShootPrimary;
    public DirectionF AimDirection;
    public bool BuildPrimary;
    /// <summary>Second build key: builds (and plants) a harvester instead of a relay.</summary>
    public bool BuildSecondary;
    /// <summary>Third build key: builds (and plants) a pylon.</summary>
    public bool BuildTertiary;
    /// <summary>Upgrade key held: 0 for none, 1-6 for <see cref="BaseUpgrade"/> + 1. Bought while docked.</summary>
    public int Upgrade;
}
