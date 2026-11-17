namespace Tunnerer.Browser.Client;

using System.Text.Json.Serialization;
using Tunnerer.Core.Types;

public sealed record BrowserFrame(
    int Seed, int Tick, int TickRate, PlayerStatus Player,
    SoundFrame Sounds, BuildOption[] Builds,
    UpgradeOption[] Upgrades, PlantPreview? Preview, GoalStatus? Goal, GuidanceStatus Guidance);

/// <param name="HeatGauge">Where the heat lies on the desktop's heat scale, 0 (deep frost) to 1 (well past overheating).</param>
/// <param name="HeatColor">The desktop's colour for the heat, as #rrggbb.</param>
/// <param name="HitFlash">1 just after a hit by enemy fire, fading to 0.</param>
/// <param name="HitFromX">Which way the last hit came from (a unit vector, with <paramref name="HitFromY"/>; 0 when unknown).</param>
public sealed record PlayerStatus(
    Position Position, Position Home, float Heat, float HeatLimit, float HeatGauge, string HeatColor,
    float HitFlash, float HitFromX, float HitFromY, float Shield, int Lives,
    int Ore, int HoldCapacity, int Store, double Charge, double BaseHeat,
    bool Docked, bool OffNetwork, float SafeSeconds, bool Dead, double RespawnSeconds,
    string Outcome, string? Carrying);

public sealed record BuildOption(int Key, string Name, int Cost, bool Affordable, string Payer);
/// <param name="Track">Whose upgrade it is: <c>Base</c> (keys 1-4) or <c>Tank</c> (5-6).</param>
public sealed record UpgradeOption(
    int Key, string Name, string Effect, string Track, int Level, int MaxLevel, int Cost, bool Maxed);
public sealed record PlantPreview(string Status, Position Target, float Heat);
/// <summary>What the HUD tells the player (<c>PlayerGuidance</c>, shared with the desktop). Tones are <c>HintTone</c> names.</summary>
public sealed record GuidanceStatus(TutorialCard? Tutorial, ToastLine[] Toasts, HintLine Hint, WarningLine? Warning);

/// <summary>The tutorial's step; <c>Target</c> is the cell it points at, if any.</summary>
public sealed record TutorialCard(
    int Number, int Count, string Title, string Body, string? Progress, float Fraction, Position? Target);

/// <summary>A game-event toast. <c>Key</c> and <c>PostedAt</c> name it; a toast posted again comes back with a new time.</summary>
public sealed record ToastLine(string Key, string Title, string Detail, string Tone, float PostedAt);

public sealed record HintLine(string Text, string Tone);
public sealed record WarningLine(string Kind, string Title, string? Detail, string Tone, bool Blinks);

public sealed record GoalStatus(Position Position, double Output, double Required, double Hold, double RequiredHold, bool Complete);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BrowserFrame))]
[JsonSerializable(typeof(SoundBankLayout))]
public partial class BrowserJsonContext : JsonSerializerContext;
