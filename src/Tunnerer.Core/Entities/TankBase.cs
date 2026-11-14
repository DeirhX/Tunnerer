namespace Tunnerer.Core.Entities;

using Tunnerer.Core.Types;
using Tunnerer.Core.Config;
using Tunnerer.Core.Resources;
using Tunnerer.Core.Terrain;

public class TankBase
{
    public Position Position { get; }
    public int Color { get; }
    public BoundingBox BoundingBox { get; } = new(Tweaks.Base.BaseSize / 2, Tweaks.Base.BaseSize / 2);
    public Reactor Reactor { get; } = new(
        initial: new ReactorState(Tweaks.Base.InitialHeat, Tweaks.Base.InitialHealth),
        capacity: new ReactorState(Tweaks.Base.HeatCapacity, Tweaks.Base.HealthCapacity));
    public MaterialContainer Materials { get; } = new(
        initial: new MaterialAmount(0, 0),
        capacity: new MaterialAmount(Tweaks.Base.MaterialDirtCapacity, Tweaks.Base.MaterialMineralsCapacity));

    private static int Half => Tweaks.Base.BaseSize / 2;
    private static int DoorHalf => Tweaks.Base.DoorSize / 2;

    public TankBase(Position position, int color)
    {
        Position = position;
        Color = color;
    }

    public bool IsInside(Position tested) => BoundingBox.IsInside(tested, Position);

    // Energy absorbed from the grid that does not yet add up to a whole reactor heat unit.
    private double _reactorHeatRemainder;

    /// <summary>
    /// Thermal energy currently stored in the reactor, in grid units. Reactor heat is a whole number of
    /// units of <see cref="Tweaks.Base.ReactorEnergyPerHeatUnit"/>; the sub-unit remainder is tracked here
    /// so absorption is exact.
    /// </summary>
    public double ReactorHeatEnergy => (int)Reactor.Heat * (double)Tweaks.Base.ReactorEnergyPerHeatUnit + _reactorHeatRemainder;

    public void Advance(TerrainGrid terrain)
    {
        int outerRadius = Half;
        int midRadius = Math.Max(1, (outerRadius * 2) / 3);
        int innerRadius = Math.Max(1, outerRadius / 3);
        double energyPerUnit = Tweaks.Base.ReactorEnergyPerHeatUnit;

        // The reactor is a finite sink: it can only absorb as much energy as it still has room for,
        // and only heat that is actually present in the surroundings.
        double room = Math.Max(0, (int)Reactor.HeatCapacity - (int)Reactor.Heat) * energyPerUnit - _reactorHeatRemainder;
        double absorbed = 0.0;
        double Pull(int radius, double cap, TerrainGrid.ThermalField field)
        {
            double taken = terrain.ExtractEnergyInRadiusArea(Position, radius, Math.Min(cap, room - absorbed), field);
            absorbed += taken;
            return taken;
        }

        Pull(outerRadius, Tweaks.Base.AreaCoolingOuterHeatTotal, TerrainGrid.ThermalField.Terrain);
        Pull(midRadius, Tweaks.Base.AreaCoolingMidHeatTotal, TerrainGrid.ThermalField.Terrain);
        Pull(innerRadius, Tweaks.Base.AreaCoolingInnerHeatTotal, TerrainGrid.ThermalField.Terrain);
        Pull(outerRadius, Tweaks.Base.AreaCoolingOuterAirTotal, TerrainGrid.ThermalField.Air);
        Pull(midRadius, Tweaks.Base.AreaCoolingMidAirTotal, TerrainGrid.ThermalField.Air);
        Pull(innerRadius, Tweaks.Base.AreaCoolingInnerAirTotal, TerrainGrid.ThermalField.Air);

        if (absorbed > 0.0)
        {
            _reactorHeatRemainder += absorbed;
            int wholeUnits = (int)Math.Floor(_reactorHeatRemainder / energyPerUnit);
            if (wholeUnits > 0)
            {
                Reactor.Add(new ReactorState(wholeUnits, 0));
                _reactorHeatRemainder -= wholeUnits * energyPerUnit;
            }
        }

        int beforeHeat = Reactor.Heat;
        Reactor.Exhaust(new ReactorState(Tweaks.Base.HeatCooldown, 0));
        int releasedUnits = Math.Max(0, beforeHeat - (int)Reactor.Heat);
        if (releasedUnits > 0)
        {
            double releasedEnergy = releasedUnits * energyPerUnit;
            double toAir = releasedEnergy * 0.8;
            terrain.AddEnergyInRadiusArea(Position, Half, toAir, TerrainGrid.ThermalField.Air);
            terrain.AddEnergyInRadiusArea(Position, Half, releasedEnergy - toAir, TerrainGrid.ThermalField.Terrain);
        }

        // Keep base center as a hard thermal sink for gameplay/readability:
        // its cell should not retain residual terrain/air energy. The removed energy is metered.
        terrain.SinkCellToAmbient(Position, TerrainGrid.ThermalField.Terrain);
        terrain.SinkCellToAmbient(Position, TerrainGrid.ThermalField.Air);

        Reactor.Add(new ReactorState(0, Tweaks.Base.HealthRegen));
    }

    public void RechargeTank(Reactor tankReactor, int tankColor)
    {
        if (tankColor == Color)
            GiveReactorResources(tankReactor, Tweaks.Base.HomeRechargeHealth);
        else
            GiveReactorResources(tankReactor, Tweaks.Base.ForeignRechargeHealth);
    }

    private static void GiveReactorResources(Reactor target, int healthRegen)
    {
        target.Add(new ReactorState(0, healthRegen));
    }

    public void AbsorbResources(MaterialContainer other)
    {
        Materials.Absorb(other);
    }

    public void AbsorbResources(MaterialContainer other, MaterialAmount rate)
    {
        var absorber = new MaterialContainer(initial: default, capacity: rate);
        absorber.Absorb(other);
        Materials.Absorb(absorber);
        other.Absorb(absorber);
    }

    public void Draw(Surface surface)
    {
        var outlineColor = Tweaks.Colors.BaseOutline.ToArgb();

        ForEachEdgePixel(Position, (px, py, isDoor) =>
        {
            if (!surface.IsInside(px, py)) return;
            if (!isDoor)
                surface.Pixels[px + py * surface.Width] = outlineColor;
        });
    }

    public void CreateInTerrain(TerrainGrid terrain, int color)
    {
        int half = Half;

        for (int y = -half; y <= half; y++)
            for (int x = -half; x <= half; x++)
            {
                var pix = Position + new Offset(x, y);
                if (!terrain.IsInside(pix)) continue;

                if (IsEdge(x, y))
                {
                    if (IsDoor(x))
                        terrain.SetPixel(pix, TerrainPixel.BaseBarrier);
                    else
                        terrain.SetPixel(pix, (TerrainPixel)((byte)TerrainPixel.BaseMin + color));
                }
                else
                {
                    terrain.SetPixel(pix, TerrainPixel.BaseCore);
                }
            }
    }

    private static bool IsEdge(int dx, int dy) => Math.Abs(dx) == Half || Math.Abs(dy) == Half;
    private static bool IsDoor(int dx) => dx >= -DoorHalf && dx <= DoorHalf;

    /// <summary>
    /// Iterates edge pixels of the base outline, calling visitor(px, py, isDoor).
    /// </summary>
    private static void ForEachEdgePixel(Position center, Action<int, int, bool> visitor)
    {
        int half = Half;
        for (int dy = -half; dy <= half; dy++)
            for (int dx = -half; dx <= half; dx++)
            {
                if (!IsEdge(dx, dy)) continue;
                visitor(center.X + dx, center.Y + dy, IsDoor(dx));
            }
    }
}
