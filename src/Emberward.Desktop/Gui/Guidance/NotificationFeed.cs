namespace Emberward.Desktop.Gui.Guidance;

public enum NotificationKind { Info, Good, Warning, Danger }

/// <summary>A short message about something that just happened in the game.</summary>
public sealed record Notification(string Key, string Title, string Detail, NotificationKind Kind, float PostedAt, float Duration)
{
    public float ExpiresAt => PostedAt + Duration;
}

/// <summary>
/// The stack of game-event toasts the HUD shows. Messages with the same key are not repeated within a cooldown, so a
/// state that flickers on and off does not spam the player; a repeated key replaces the older toast.
/// </summary>
public sealed class NotificationFeed
{
    public const int MaxVisible = 4;
    public const float DefaultDuration = 6f;
    public const float DefaultCooldown = 30f;

    private readonly List<Notification> _active = new();
    private readonly Dictionary<string, float> _lastPosted = new();

    public IReadOnlyList<Notification> Active => _active;

    /// <summary>Posts a toast unless the same key was posted less than <paramref name="cooldown"/> seconds ago.</summary>
    public bool Post(float now, string key, string title, string detail, NotificationKind kind,
        float duration = DefaultDuration, float cooldown = DefaultCooldown)
    {
        if (_lastPosted.TryGetValue(key, out float last) && now - last < cooldown)
            return false;
        _lastPosted[key] = now;
        _active.RemoveAll(n => n.Key == key);
        _active.Add(new Notification(key, title, detail, kind, now, duration));
        while (_active.Count > MaxVisible)
            _active.RemoveAt(0);
        return true;
    }

    /// <summary>Drops expired toasts.</summary>
    public void Update(float now) => _active.RemoveAll(n => now >= n.ExpiresAt);

    public void Clear()
    {
        _active.Clear();
        _lastPosted.Clear();
    }
}
