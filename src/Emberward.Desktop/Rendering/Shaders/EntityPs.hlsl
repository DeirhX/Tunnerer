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
#include "Entity/PowerLabel.hlsli"
#include "Entity/BaseShield.hlsli"

float4 main(PSIn input) : SV_Target
{
    float2 p = input.local;
    float aa = max(length(fwidth(p)) * 0.7071, 1e-4);
    float kind = input.color.a;

    // One result returned at the end rather than a return per kind. DXC turns a function with several returns into
    // a loop that runs once, left by a break at each return; ANGLE's Direct3D 11 backend (Windows browsers) hands
    // that to FXC, which took over half a minute on this shader in that form and about a second in this one. The
    // GPU still runs only the chosen kind's branch.
    float4 result;
    if (kind < kKindTankHull + 0.5)
        result = TankHull(p, input.color.rgb, input.params, aa);
    else if (kind < kKindTankTurret + 0.5)
        result = TankTurret(p, input.color.rgb, input.params, aa);
    else if (kind < kKindHotShot + 0.5)
        result = HotShot(p, input.color.rgb, input.params, aa);
    else if (kind < kKindOreBeam + 0.5)
        result = OreBeam(p, input.params, aa);
    else if (kind < kKindRelay + 0.5)
        result = Relay(p, input.params, aa);
    else if (kind < kKindHarvester + 0.5)
        result = Harvester(p, input.params, aa);
    else if (kind < kKindSenseRing + 0.5)
        result = SenseRing(p, input.params, aa);
    else if (kind < kKindLink + 0.5)
        result = LinkBeam(p, input.params, aa);
    else if (kind < kKindBattery + 0.5)
        result = Battery(p, input.params, aa);
    else if (kind < kKindPreviewBeam + 0.5)
        result = PreviewBeam(p, input.params, aa);
    else if (kind < kKindReachArea + 0.5)
        result = ReachArea(p, input.color.rgb, input.params, aa);
    else if (kind < kKindCoverage + 0.5)
        result = NetworkCoverage(p, input.color.rgb, input.params, aa);
    else if (kind < kKindTether + 0.5)
        result = Tether(p, input.params, aa);
    else if (kind < kKindRadarBlip + 0.5)
        result = RadarBlip(p, input.color.rgb, input.params, aa);
    else if (kind < kKindRadarPulse + 0.5)
        result = RadarPulse(p, input.params, aa);
    else if (kind < kKindGenerator + 0.5)
        result = Generator(p, input.params, aa);
    else if (kind < kKindPowerLabel + 0.5)
        result = PowerLabel(p, input.color.rgb, input.params);
    else if (kind < kKindTurret + 0.5)
        result = Turret(p, input.params, aa);
    else if (kind < kKindBaseShield + 0.5)
        result = BaseShield(p, input.color.rgb, input.params, aa);
    else // kKindShieldHit
        result = ShieldHit(p, input.color.rgb, input.params, aa);
    return result;
}
