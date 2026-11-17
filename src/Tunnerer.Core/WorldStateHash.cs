namespace Tunnerer.Core;

using Tunnerer.Core.Rendering;

/// <summary>
/// One number for the state of a world: terrain pixels, tanks, machines, links, projectiles and sprites. Two worlds
/// built from the same seed and stepped with the same inputs have the same hash, which is how the determinism and
/// fingerprint tests compare runs and how machines in a network match will check they still agree.
/// </summary>
/// <remarks>
/// Of heat <see cref="Compute"/> reads only what tanks and machines hold, not the terrain's heat field;
/// <see cref="ComputeSync"/> adds that.
/// </remarks>
public static class WorldStateHash
{
    public static ulong Compute(World world)
    {
        const ulong offset = 1469598103934665603UL;
        ulong hash = offset;

        static ulong Mix(ulong h, uint value)
        {
            const ulong fnvPrime = 1099511628211UL;
            h ^= value;
            h *= fnvPrime;
            return h;
        }

        var terrain = world.Terrain.Data;
        for (int i = 0; i < terrain.Length; i++)
            hash = Mix(hash, (uint)terrain[i]);

        hash = Mix(hash, (uint)world.AdvanceCount);
        hash = Mix(hash, (uint)world.Projectiles.Count);
        hash = Mix(hash, (uint)world.Machines.Machines.Count);
        hash = Mix(hash, (uint)world.LinkMap.Points.Count);
        hash = Mix(hash, (uint)world.LinkMap.Links.Count);

        var tanks = world.TankList.Tanks;
        hash = Mix(hash, (uint)tanks.Count);
        for (int i = 0; i < tanks.Count; i++)
        {
            var t = tanks[i];
            hash = Mix(hash, (uint)t.Position.X);
            hash = Mix(hash, (uint)t.Position.Y);
            hash = Mix(hash, (uint)t.Direction);
            hash = Mix(hash, (uint)t.LivesLeft);
            hash = Mix(hash, (uint)(int)t.Reactor.Heat);
            hash = Mix(hash, (uint)(int)t.Reactor.Health);
            hash = Mix(hash, (uint)t.Resources.Dirt);
            hash = Mix(hash, (uint)t.Resources.Minerals);
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(t.Heat));
            hash = Mix(hash, t.IsDead ? 1u : 0u);
        }

        var machines = world.Machines.Machines;
        for (int i = 0; i < machines.Count; i++)
        {
            var m = machines[i];
            hash = Mix(hash, (uint)m.Position.X);
            hash = Mix(hash, (uint)m.Position.Y);
            hash = Mix(hash, (uint)m.Type);
            hash = Mix(hash, (uint)m.State);
            hash = Mix(hash, (uint)m.OwnerColor);
            hash = Mix(hash, m.IsAlive ? 1u : 0u);
            hash = Mix(hash, (uint)(int)m.Reactor.Heat);
            hash = Mix(hash, (uint)(int)m.Reactor.Health);
        }

        var points = world.LinkMap.Points;
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            hash = Mix(hash, (uint)p.Id);
            hash = Mix(hash, (uint)p.Position.X);
            hash = Mix(hash, (uint)p.Position.Y);
            hash = Mix(hash, (uint)p.Type);
            hash = Mix(hash, p.IsEnabled ? 1u : 0u);
            hash = Mix(hash, p.IsPowered ? 1u : 0u);
        }

        var links = world.LinkMap.Links;
        for (int i = 0; i < links.Count; i++)
        {
            var l = links[i];
            hash = Mix(hash, (uint)l.From.Id);
            hash = Mix(hash, (uint)l.To.Id);
            hash = Mix(hash, (uint)l.Type);
            hash = Mix(hash, l.IsAlive ? 1u : 0u);
        }

        // Entities the GPU draws, hashed from Core's own state rather than a raster of it.
        var projectiles = new ProjectileRenderState[world.Projectiles.Count];
        int projectileCount = world.Projectiles.CopyRenderStates(projectiles);
        for (int i = 0; i < projectileCount; i++)
        {
            var p = projectiles[i];
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(p.Position.X));
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(p.Position.Y));
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(p.Velocity.X));
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(p.Velocity.Y));
            hash = Mix(hash, (uint)p.Type);
            hash = Mix(hash, p.IsAlive ? 1u : 0u);
        }

        var sprites = new SpriteRenderState[world.Sprites.Count];
        int spriteCount = world.Sprites.CopyRenderStates(sprites);
        for (int i = 0; i < spriteCount; i++)
        {
            var s = sprites[i];
            hash = Mix(hash, (uint)s.Position.X);
            hash = Mix(hash, (uint)s.Position.Y);
            hash = Mix(hash, (uint)s.Type);
            hash = Mix(hash, s.IsAlive ? 1u : 0u);
        }

        for (int i = 0; i < tanks.Count; i++)
        {
            var tip = tanks[i].Turret.GetBarrelTip(tanks[i].Position);
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(tip.X));
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(tip.Y));
        }

        return hash;
    }

    /// <summary>
    /// What machines in a network match compare: <see cref="Compute"/> plus the two totals that cover what it does
    /// not read, the heat field and the stored charge. The parts are kept apart so that, when two machines differ,
    /// the first thing known is where.
    /// </summary>
    public static SyncDigest ComputeSync(World world) => new(
        Compute(world),
        BitConverter.DoubleToUInt64Bits(world.SumSystemThermalEnergy()),
        BitConverter.DoubleToUInt64Bits(world.SumSystemCharge()));
}

/// <summary>A world's state for comparison between machines (<see cref="WorldStateHash.ComputeSync"/>).</summary>
/// <param name="State">Terrain pixels, tanks, machines, links, projectiles and sprites.</param>
/// <param name="Heat">The bits of the total thermal energy: terrain, air, tanks and bases.</param>
/// <param name="Charge">The bits of the total stored charge.</param>
public readonly record struct SyncDigest(ulong State, ulong Heat, ulong Charge)
{
    /// <summary>The three parts as one number, which is what travels.</summary>
    public ulong Combined => (State * 1099511628211UL ^ Heat) * 1099511628211UL ^ Charge;
}
