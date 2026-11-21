namespace Emberward.Core.Entities.Tanks;

using Emberward.Core.Config;
using Emberward.Core.Types;
using Emberward.Core.Input;
using Emberward.Core.Rendering;
using Emberward.Core.Entities.Bases;

public class TankList
{
    private readonly List<Tank> _tanks = new();

    public IReadOnlyList<Tank> Tanks => _tanks;
    public int Count => _tanks.Count;

    public Tank AddTank(int color, TankBase tankBase, int rngSeed = 0, SimulationSettings? settings = null,
        TankShieldSystem? shield = null)
    {
        var tank = new Tank(color, tankBase, rngSeed, settings, shield);
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
