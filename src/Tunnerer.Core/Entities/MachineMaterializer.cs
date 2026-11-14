namespace Tunnerer.Core.Entities;

using Tunnerer.Core.Types;
using Tunnerer.Core.Resources;
using Tunnerer.Core.Entities.Machines;

public class MachineMaterializer
{
    private readonly int _ownerColor;
    private Machine? _carrying;

    public MachineMaterializer(int ownerColor)
    {
        _ownerColor = ownerColor;
    }

    public bool IsCarrying => _carrying != null;

    public void TryBuild(MachineType type, Position position, MaterialContainer resources, MachineList machineList)
    {
        if (_carrying != null) return;

        var cost = MachineBehaviors.Get(type).BuildCost;
        if (!resources.CanPay(cost)) return;
        resources.Pay(cost);

        _carrying = new Machine(position, type, _ownerColor);
        _carrying.State = MachineState.Transporting;
        machineList.Add(_carrying);
    }

    public void UpdatePosition(Position position)
    {
        if (_carrying != null)
            _carrying.Position = position;
    }

    /// <summary>
    /// Hands over the machine being carried so the caller can plant it (the world registers it in the link
    /// network). Returns null if nothing is being carried.
    /// </summary>
    public Machine? ReleaseCarried()
    {
        var machine = _carrying;
        _carrying = null;
        return machine;
    }

    /// <summary>Drops the carried machine for good (the carrier died); its build cost is not refunded.</summary>
    public void DiscardCarried()
    {
        if (_carrying != null)
            _carrying.IsAlive = false;
        _carrying = null;
    }
}
