namespace Emberward.Core.Entities.Projectiles;

using Emberward.Core.Types;
using Emberward.Core.Config;

public static class ExplosionFactory
{
    public static List<Projectile> CreateExplosion(
        Position center,
        ExplosionParams p,
        ref FastRandom rng,
        float shrapnelHeatScale = 1f)
    {
        var result = new List<Projectile>(p.ShrapnelCount);

        for (int i = 0; i < p.ShrapnelCount; i++)
        {
            float angle = rng.NextSingle() * MathF.Tau;
            float s = rng.NextSingle() * p.Speed * Tweaks.Explosion.SpeedVarianceRange
                    + p.Speed * Tweaks.Explosion.SpeedVarianceBase;
            var dir = new VectorF(DeterministicMath.Cos(angle) * s, DeterministicMath.Sin(angle) * s);
            int life = rng.NextInt(0, p.Frames);
            result.Add(Projectile.CreateShrapnel(center, dir, life, shrapnelHeatScale));
        }
        return result;
    }

}
