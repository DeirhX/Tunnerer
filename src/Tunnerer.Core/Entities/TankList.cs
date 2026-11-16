namespace Tunnerer.Core.Entities;

using Tunnerer.Core.Config;
using Tunnerer.Core.Types;
using Tunnerer.Core.Input;
using Tunnerer.Core.Rendering;

public class TankList
{
    private readonly List<Tank> _tanks = new();

    public IReadOnlyList<Tank> Tanks => _tanks;
    public int Count => _tanks.Count;

    public Tank AddTank(int color, TankBase tankBase, int rngSeed = 0, SimulationSettings? settings = null)
    {
        var tank = new Tank(color, tankBase, rngSeed, settings);
        _tanks.Add(tank);
        return tank;
    }

    public void Advance(in SimulationContext ctx, Func<int, ControllerOutput> getInput)
    {
        for (int i = 0; i < _tanks.Count; i++)
            _tanks[i].Advance(in ctx, getInput(i));
    }

    public int CopyRenderStates(Span<TankRenderState> destination)
    {
        int count = Math.Min(destination.Length, _tanks.Count);
        for (int i = 0; i < count; i++)
        {
            var tank = _tanks[i];
            destination[i] = new TankRenderState(
                Position: tank.Position,
                Color: tank.Color,
                Direction: tank.Direction,
                IsDead: tank.IsDead,
                TurretDirection: tank.Turret.Direction,
                HitFlashTicks: tank.HitFlashTicks,
                IsProtected: tank.SpawnProtectionTicks > 0);
        }
        return count;
    }
}
