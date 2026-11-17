namespace Tunnerer.Browser.Client;

using System.Text.Json.Serialization;
using Tunnerer.Core.Types;

public sealed record BrowserFrame(
    int Seed, int Tick, int TickRate, Rect View, PlayerStatus Player,
    SceneEvent[] Events, BuildOption[] Builds,
    UpgradeOption[] Upgrades, PlantPreview? Preview, GoalStatus? Goal);

public sealed record PlayerStatus(
    Position Position, Position Home, float Heat, float HeatLimit, float Shield, int Lives,
    int Ore, int HoldCapacity, int Store, double Charge, double BaseHeat,
    bool Docked, bool OffNetwork, float SafeSeconds, bool Dead, double RespawnSeconds,
    string Outcome, string? Carrying, string? Refusal);

public sealed record SceneEvent(string Kind, int X, int Y);
public sealed record BuildOption(int Key, string Name, int Cost, bool Affordable, string Payer);
public sealed record UpgradeOption(int Key, string Name, string Effect, int Level, int Cost, bool Maxed);
public sealed record PlantPreview(string Status, Position Target, float Heat);
public sealed record GoalStatus(Position Position, double Output, double Required, double Hold, double RequiredHold, bool Complete);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BrowserFrame))]
public partial class BrowserJsonContext : JsonSerializerContext;
