// ============================================================================
// EntityPs.hlsl — Entity layer pixel shader (tanks, projectiles, machines, beams and ground overlays)
// ============================================================================
//
// Draws entities procedurally at screen resolution, over the finished terrain
// frame. Each entity is a quad whose `local` coordinate is in world cells with
// +x along the entity's facing (tank hull, turret) or travel (projectile)
// direction, so shapes are signed-distance functions of `local` and stay sharp
// at any pixel scale.
//
// Output is premultiplied alpha (blend: One, InvSrcAlpha), so opaque bodies and
// additive glows share one pass: a glow adds colour with zero alpha.
//
// The shapes live in Entity/*.hlsli; Entity/Layout.hlsli lists the shape kinds
// (color.a) and their params. main() picks the shape for the quad's kind.
// ============================================================================

#include "Entity/Layout.hlsli"
#include "Entity/Common.hlsli"
#include "Entity/Tank.hlsli"
#include "Entity/Projectiles.hlsli"
#include "Entity/Machines.hlsli"
#include "Entity/Beams.hlsli"
#include "Entity/Overlays.hlsli"

float4 main(PSIn input) : SV_Target
{
    float2 p = input.local;
    float aa = max(length(fwidth(p)) * 0.7071, 1e-4);
    float kind = input.color.a;

    if (kind < kKindTankHull + 0.5)
        return TankHull(p, input.color.rgb, input.params, aa);
    if (kind < kKindTankTurret + 0.5)
        return TankTurret(p, input.color.rgb, input.params, aa);
    if (kind < kKindHotShot + 0.5)
        return HotShot(p, input.color.rgb, input.params, aa);
    if (kind < kKindOreBeam + 0.5)
        return OreBeam(p, input.params, aa);
    if (kind < kKindRelay + 0.5)
        return Relay(p, input.params, aa);
    if (kind < kKindHarvester + 0.5)
        return Harvester(p, input.params, aa);
    if (kind < kKindSenseRing + 0.5)
        return SenseRing(p, input.params, aa);
    if (kind < kKindLink + 0.5)
        return LinkBeam(p, input.params, aa);
    if (kind < kKindChargeBeam + 0.5)
        return ChargeBeam(p, input.params, aa);
    if (kind < kKindPylon + 0.5)
        return Pylon(p, input.params, aa);
    if (kind < kKindBattery + 0.5)
        return Battery(p, input.params, aa);
    if (kind < kKindPreviewBeam + 0.5)
        return PreviewBeam(p, input.params, aa);
    if (kind < kKindReachArea + 0.5)
        return ReachArea(p, input.color.rgb, input.params, aa);
    if (kind < kKindCoverage + 0.5)
        return NetworkCoverage(p, input.color.rgb, input.params, aa);
    if (kind < kKindTether + 0.5)
        return Tether(p, input.params, aa);
    if (kind < kKindRadarBlip + 0.5)
        return RadarBlip(p, input.color.rgb, input.params, aa);
    return RadarPulse(p, input.params, aa); // kKindRadarPulse
}
