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
    public const float KindBattery = 8f;
    public const float KindPreviewBeam = 9f;
    public const float KindReachArea = 10f;
    public const float KindCoverage = 11f;
    public const float KindTether = 12f;
    public const float KindRadarBlip = 13f;
    public const float KindRadarPulse = 14f;
    public const float KindGenerator = 15f;
    public const float KindPowerLabel = 16f;
    public const float KindTurret = 17f;
    public const float KindEmberite = 18f;
    public const float KindGatling = 19f;
    public const float KindBaseShield = 20f;
    public const float KindShieldHit = 21f;
    public const float KindTankShield = 22f;

    public const float MachineFlagStalled = 1f;
    public const float MachineFlagCarried = 2f;
    public const float MachineFlagLimited = 4f;
}
