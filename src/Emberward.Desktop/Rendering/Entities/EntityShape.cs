namespace Emberward.Desktop.Rendering.Entities;

/// <summary>
/// The shape kinds (vertex <c>Kind</c>) and machine flags (<c>P3</c>) the entity shader understands. Each must match
/// the <c>kKind*</c> / <c>kMachineFlag*</c> constant of the same name in <c>Shaders/Entity/Layout.hlsli</c>
/// (EntityLayoutGuardTests).
/// </summary>
public static class EntityShape
{
    public const float KindTankHull = 0f;
    public const float KindTankTurret = 1f;
    public const float KindHotShot = 2f;
    public const float KindOreBeam = 3f;
    public const float KindRelay = 4f;
    public const float KindHarvester = 5f;
    public const float KindSenseRing = 6f;
    public const float KindLink = 7f;
    public const float KindChargeBeam = 8f;
    public const float KindPylon = 9f;
    public const float KindBattery = 10f;
    public const float KindPreviewBeam = 11f;
    public const float KindReachArea = 12f;
    public const float KindCoverage = 13f;
    public const float KindTether = 14f;
    public const float KindRadarBlip = 15f;
    public const float KindRadarPulse = 16f;
    public const float KindGenerator = 17f;
    public const float KindPowerLabel = 18f;
    public const float KindTurret = 19f;

    public const float MachineFlagStalled = 1f;
    public const float MachineFlagCarried = 2f;
    public const float MachineFlagLimited = 4f;
}
