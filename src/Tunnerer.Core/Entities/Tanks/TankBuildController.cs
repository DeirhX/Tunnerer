namespace Tunnerer.Core.Entities.Tanks;

using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Input;
using Tunnerer.Core.Resources;

/// <summary>
/// The build keys: building a machine from the tank's (or its base's) store, carrying it, and planting it outside a
/// base, refusing a link machine that would not link.
/// </summary>
internal sealed class TankBuildController
{
    /// <summary>Ticks the "will not link" refusal stays visible after a refused plant.</summary>
    private const int PlantRefusedDuration = 48;

    private readonly Tank _tank;
    private readonly MachineMaterializer _materializer;
    private bool _primaryHeld;
    private bool _secondaryHeld;
    private bool _tertiaryHeld;

    public TankBuildController(Tank tank, int color)
    {
        _tank = tank;
        _materializer = new MachineMaterializer(color);
    }

    public bool IsCarrying => _materializer.IsCarrying;
    public MachineType? CarriedType => _materializer.CarriedType;
    public int PlantRefusedTicks { get; private set; }

    public void BeginTick()
    {
        if (PlantRefusedTicks > 0)
            PlantRefusedTicks--;
    }

    public void DiscardCarried() => _materializer.DiscardCarried();

    /// <summary>
    /// Build keys (edge triggered): with no machine in hand the primary key builds a relay, the secondary key a
    /// harvester and the tertiary key a pylon from the tank's resources and carries it; any build key pressed again
    /// outside a base plants it where the tank stands.
    /// </summary>
    public void Advance(in SimulationContext ctx, ControllerOutput input)
    {
        bool primaryPressed = input.BuildPrimary && !_primaryHeld;
        bool secondaryPressed = input.BuildSecondary && !_secondaryHeld;
        bool tertiaryPressed = input.BuildTertiary && !_tertiaryHeld;
        _primaryHeld = input.BuildPrimary;
        _secondaryHeld = input.BuildSecondary;
        _tertiaryHeld = input.BuildTertiary;
        bool pressed = primaryPressed || secondaryPressed || tertiaryPressed;
        var buildType = MachineBehaviors.ForKey(
            primaryPressed ? BuildKey.Primary : secondaryPressed ? BuildKey.Secondary : BuildKey.Tertiary).Type;

        var position = _tank.Position;
        if (pressed && ctx.Machines != null && ctx.PlantMachine != null)
        {
            if (_materializer.IsCarrying)
            {
                // Machines that work through the link network need a live link to be worth planting; others
                // (the harvester) work anywhere outside a base.
                if (ctx.CheckBaseCollision(position) == null &&
                    _materializer.CarriedType is MachineType carried && MachineBehaviors.Get(carried).RequiresLinkToPlant &&
                    ctx.CanPlantRelayAt?.Invoke(position) == false)
                {
                    PlantRefusedTicks = PlantRefusedDuration;
                    ctx.Events?.Record(new StepEvent(StepEventKind.PlantRefused, position, _tank.Color, carried));
                }
                else if (ctx.CheckBaseCollision(position) == null)
                {
                    var machine = _materializer.ReleaseCarried();
                    if (machine != null)
                        ctx.PlantMachine(machine, _tank.Base);
                }
            }
            else
            {
                var payer = BuildPayer(buildType, _tank.IsDockedAtOwnBase);
                if (payer != null)
                    _materializer.TryBuild(buildType, position, payer, ctx.Machines);
                if (_materializer.IsCarrying)
                    ctx.Events?.Record(new StepEvent(StepEventKind.MachineBuilt, position, _tank.Color, buildType));
            }
        }

        _materializer.UpdatePosition(_tank.Position);
    }

    /// <summary>
    /// Dirt available to build with: the tank's own, or the base store if that is larger while docked at the own
    /// base (docking moves the tank's dirt into the store). A machine is affordable when its cost fits.
    /// </summary>
    public int BuildableDirt(bool dockedAtOwnBase)
    {
        var tankBase = _tank.Base;
        return dockedAtOwnBase && tankBase != null
            ? Math.Max(_tank.Resources.Dirt, tankBase.Materials.Dirt)
            : _tank.Resources.Dirt;
    }

    /// <summary>The store that pays for <paramref name="type"/>, or null if neither can.</summary>
    public MaterialContainer? BuildPayer(MachineType type, bool dockedAtOwnBase)
    {
        var cost = MachineBehaviors.Get(type).BuildCost;
        var resources = _tank.Resources;
        if (resources.CanPay(cost))
            return resources;
        var tankBase = _tank.Base;
        if (dockedAtOwnBase && tankBase != null && tankBase.Materials.CanPay(cost))
            return tankBase.Materials;
        return null;
    }
}
