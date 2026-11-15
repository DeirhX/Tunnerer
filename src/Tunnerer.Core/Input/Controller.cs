namespace Tunnerer.Core.Input;

using Tunnerer.Core.Types;

public struct ControllerOutput
{
    public Offset MoveSpeed;
    public bool ShootPrimary;
    public bool ShootSecondary;
    public DirectionF AimDirection;
    public bool BuildPrimary;
    /// <summary>Second build key: builds (and plants) a harvester instead of a relay.</summary>
    public bool BuildSecondary;
    /// <summary>Third build key: builds (and plants) a pylon.</summary>
    public bool BuildTertiary;
}
