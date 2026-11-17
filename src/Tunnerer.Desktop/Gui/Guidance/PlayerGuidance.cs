namespace Tunnerer.Desktop.Gui.Guidance;

using Tunnerer.Core;
using Tunnerer.Core.Entities.Tanks;

/// <summary>
/// What the HUD tells the player, whichever host draws it: the tutorial's next step, the game-event toasts, the
/// context hint for the build keys and the warning banner. The desktop draws it with ImGui and the browser sends it to
/// the page. It only reads Core state.
/// </summary>
public sealed class PlayerGuidance
{
    /// <summary>Seconds the hint says a relay just linked.</summary>
    private const float RelayLinkedSeconds = 4f;

    private readonly NotificationFeed _feed = new();
    private readonly GameEventWatcher _events = new();
    private readonly TutorialDirector _tutorial = new();
    private readonly PlayerWarnings _warnings = new();
    private World? _watchedWorld;
    private float _tutorialDoneAt = float.NaN;
    private int _lastPoweredRelays;
    private float _relayLinkedUntil = -1f;

    /// <summary>The tutorial step to show, or null while the tank is dead and once the tutorial is over.</summary>
    public TutorialPrompt? Tutorial { get; private set; }

    /// <summary>The toasts showing now, oldest first.</summary>
    public IReadOnlyList<Notification> Notifications => _feed.Active;

    /// <summary>What the build keys will do now, or why a carried machine will not plant; empty when dead.</summary>
    public (string Text, HintTone Tone) Hint { get; private set; } = (string.Empty, HintTone.Dim);

    /// <summary>The warning banner, or null for none.</summary>
    public PlayerWarning? Warning { get; private set; }

    /// <summary>Call after each simulation step, while <see cref="World.StepEvents"/> still holds that step's events.</summary>
    public void AfterStep(World world, Tank player) => _events.AfterStep(world, player);

    /// <param name="now">Seconds on the host's HUD clock; toasts and timed tutorial steps run on it.</param>
    /// <param name="offerThermalView">The host has a thermal view and it is off (<see cref="PlayerWarnings.Update"/>).</param>
    public void Update(World world, Tank player, float now, bool offerThermalView)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(player);
        if (!ReferenceEquals(world, _watchedWorld))
        {
            _watchedWorld = world;
            _events.Reset();
            _feed.Clear();
        }

        _events.Update(world, player, _feed, now);
        _feed.Update(now);
        Tutorial = player.IsDead ? null : _tutorial.Update(world, player, now);
        if (Tutorial == null && _tutorial.IsComplete && float.IsNaN(_tutorialDoneAt))
        {
            _tutorialDoneAt = now;
            _feed.Post(now, "tutorial-done", "Tutorial complete",
                "Push your network toward hot rock, keep harvesters cool and charged, and mind your temperature.",
                NotificationKind.Good, duration: 12f, cooldown: 1e9f);
        }

        if (player.Base is { } home)
        {
            var status = world.GetNetworkStatus(home);
            if (status.PoweredRelays > _lastPoweredRelays)
                _relayLinkedUntil = now + RelayLinkedSeconds;
            _lastPoweredRelays = status.PoweredRelays;
            Hint = HintSelector.Select(world, player, status, relayJustLinked: now < _relayLinkedUntil);
        }
        else
        {
            Hint = (string.Empty, HintTone.Dim);
        }

        Warning = _warnings.Update(player, offerThermalView, now);
    }
}
