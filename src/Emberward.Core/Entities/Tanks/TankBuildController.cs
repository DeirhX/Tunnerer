namespace Emberward.Core.Entities.Tanks;

using Emberward.Core.Entities.Machines;
using Emberward.Core.Input;
using Emberward.Core.Types;

/// <summary>
/// The build keys: building a machine with ore from the base store (in the own network) or the tank's hold, carrying
/// it, and planting it outside a base, refusing a link machine that would not link.
/// </summary>
internal sealed class TankBuildController
{
    /// <summary>Ticks a refusal ("will not link", "not enough ore") stays visible after the press.</summary>
    private const int RefusedDuration = 48;

    private readonly Tank _tank;
    private readonly MachineMaterializer _materializer;
    private bool _primaryHeld;
    private bool _secondaryHeld;
    private bool _tertiaryHeld;
    private bool _quaternaryHeld;
    private bool _quinaryHeld;

    public TankBuildController(Tank tank, int color)
    {
        _tank = tank;
        _materializer = new MachineMaterializer(color);
    }

    public bool IsCarrying => _materializer.IsCarrying;
    public MachineType? CarriedType => _materializer.CarriedType;
    public int RefusedTicks { get; private set; }
    public BuildRefusal LastRefusal { get; private set; }

    public void BeginTick()
    {
        if (RefusedTicks > 0)
            RefusedTicks--;
    }

    private void Refuse(BuildRefusal refusal)
    {
        LastRefusal = refusal;
        RefusedTicks = RefusedDuration;
    }

    public void DiscardCarried() => _materializer.DiscardCarried();

    /// <summary>
    /// Build keys (edge triggered): with no machine in hand the primary key builds a relay, the secondary key a
    /// harvester, the tertiary key a local generator, the fourth a turret and the fifth a gatling (the last three only
    /// where the match runs the energy network), paid as <see cref="Quote"/> says, and carries it; any build key pressed again outside a base
    /// plants it where the tank stands. While <paramref name="canBuild"/> is false (a stranded tank,
    /// <see cref="TankReserveEffects.CanBuild"/>) a press plants what the tank carries but builds nothing new; the keys
    /// are still tracked, so one held through the end of that stage is not taken for a new press.
    /// </summary>
    public void Advance(in SimulationContext ctx, ControllerOutput input, bool canBuild = true)
    {
        bool primaryPressed = input.BuildPrimary && !_primaryHeld;
        bool secondaryPressed = input.BuildSecondary && !_secondaryHeld;
        bool thirdAvailable = MachineBehaviors.ForKey(BuildKey.Tertiary).IsAvailable(ctx.Rules);
        bool tertiaryPressed = thirdAvailable && input.BuildTertiary && !_tertiaryHeld;
        _tertiaryHeld = thirdAvailable && input.BuildTertiary;
        bool fourthAvailable = MachineBehaviors.ForKey(BuildKey.Quaternary).IsAvailable(ctx.Rules);
        bool quaternaryPressed = fourthAvailable && input.BuildQuaternary && !_quaternaryHeld;
        _quaternaryHeld = fourthAvailable && input.BuildQuaternary;
        bool fifthAvailable = MachineBehaviors.ForKey(BuildKey.Quinary).IsAvailable(ctx.Rules);
        bool quinaryPressed = fifthAvailable && input.BuildQuinary && !_quinaryHeld;
        _quinaryHeld = fifthAvailable && input.BuildQuinary;
        _primaryHeld = input.BuildPrimary;
        _secondaryHeld = input.BuildSecondary;
        bool pressed = primaryPressed || secondaryPressed || tertiaryPressed || quaternaryPressed || quinaryPressed;
        var buildType = MachineBehaviors.ForKey(
            primaryPressed ? BuildKey.Primary : secondaryPressed ? BuildKey.Secondary :
            tertiaryPressed ? BuildKey.Tertiary : quaternaryPressed ? BuildKey.Quaternary : BuildKey.Quinary).Type;

        var position = _tank.Position;
        if (pressed && ctx.Machines != null && ctx.PlantMachine != null)
        {
            if (_materializer.IsCarrying)
            {
                // Machines that work through the link network need a live link to be worth planting; others
                // (the harvester) work anywhere outside a base.
                if (ctx.CheckBaseCollision(position) == null &&
                    _materializer.CarriedType is MachineType carried && MachineBehaviors.Get(carried).RequiresLinkToPlant &&
                    ctx.CanPlantRelayAt?.Invoke(position, _tank.Base) == false)
                {
                    Refuse(BuildRefusal.WillNotLink);
                    ctx.Events?.Record(new StepEvent(StepEventKind.PlantRefused, position, _tank.Color, carried));
                }
                else if (ctx.CheckBaseCollision(position) == null)
                {
                    var machine = _materializer.ReleaseCarried();
                    if (machine != null)
                        ctx.PlantMachine(machine, _tank.Base);
                }
            }
            else if (canBuild)
            {
                var quote = Quote(buildType);
                if (_tank.PayerStore(quote) is { } payer)
                    _materializer.TryBuild(buildType, position, payer, ctx.Machines);
                if (_materializer.IsCarrying)
                {
                    LastRefusal = BuildRefusal.None;
                    RefusedTicks = 0;
                    ctx.Events?.Record(new StepEvent(StepEventKind.MachineBuilt, position, _tank.Color, buildType));
                }
                else
                {
                    Refuse(quote.Refusal);
                    ctx.Events?.Record(new StepEvent(StepEventKind.BuildRefused, position, _tank.Color, buildType));
                }
            }
        }

        _materializer.UpdatePosition(_tank.Position);
    }

    /// <summary>
    /// The payer rule (DESIGN.md §6.4): in the own network (docked, or in the coverage of a linked node) the base store
    /// pays; outside it, or when the store is short, the hold does.
    /// </summary>
    public BuildQuote Quote(MachineType type)
    {
        int cost = MachineBehaviors.Get(type).OreCost;
        var hold = _tank.Hold;
        bool inNetwork = _tank.IsInOwnNetwork && _tank.Base != null;
        var store = inNetwork ? _tank.Base!.Store : null;
        if (store != null && store.CanPay(cost))
            return new BuildQuote(type, cost, OrePayer.BaseStore, store.Ore, inNetwork);
        if (hold.CanPay(cost))
            return new BuildQuote(type, cost, OrePayer.Hold, hold.Ore, inNetwork);
        return new BuildQuote(type, cost, OrePayer.None, Math.Max(hold.Ore, store?.Ore ?? 0), inNetwork);
    }

    /// <summary>
    /// Mixes the held build keys (a press is a change from these) and what the tank carries, for the sync digest.
    /// </summary>
    internal ulong MixLatent(ulong hash)
    {
        hash = Fnv1a.Mix(hash, _primaryHeld);
        hash = Fnv1a.Mix(hash, _secondaryHeld);
        hash = Fnv1a.Mix(hash, _tertiaryHeld);
        if (_quaternaryHeld)
            hash = Fnv1a.Mix(hash, 0xB4u);
        if (_quinaryHeld)
            hash = Fnv1a.Mix(hash, 0xB5u);
        return _materializer.MixLatent(hash);
    }
}
