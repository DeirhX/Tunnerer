namespace Tunnerer.Core;

/// <summary>
/// How a gauge stands against its thresholds (shield, charge, heat store, radiator load). Core decides the level; the
/// HUD picks the colour.
/// </summary>
public enum StatusLevel
{
    Ok,
    Warn,
    Bad,
}
