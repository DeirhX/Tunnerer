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
    /// <summary>Third build key: builds a local generator in energy matches.</summary>
    public bool BuildTertiary;
    /// <summary>Fourth build key: builds a turret in energy matches.</summary>
    public bool BuildQuaternary;
    /// <summary>
    /// Upgrade key held: 0 for none, 1-7 for <see cref="BaseUpgrade"/> + 1, bought while docked. With
    /// <see cref="UpgradeMachine"/> it is instead the path (1 for the first) to buy for the nearest own machine.
    /// </summary>
    public int Upgrade;
    /// <summary>
    /// The upgrade key is for the machine next to the tank (<see cref="Entities.Machines.MachineUpgradeSystem"/>), not
    /// the base: the player has the machine upgrade panel open. No base upgrade is bought while it is set.
    /// </summary>
    public bool UpgradeMachine;
}
