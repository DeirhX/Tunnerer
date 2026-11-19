namespace Emberward.Core;

using Emberward.Core.Bots;
using Emberward.Core.Rendering;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Entities.Bases;
using Emberward.Core.Types;

/// <summary>
/// One number for the state of a world: terrain pixels, tanks, machines, links and projectiles. Two worlds
/// built from the same seed and stepped with the same inputs have the same hash, which is how the determinism and
/// fingerprint tests compare runs and how machines in a network match will check they still agree.
/// </summary>
/// <remarks>
/// Of heat <see cref="Compute"/> reads only what tanks and machines hold, not the terrain's heat field;
/// <see cref="ComputeSync"/> adds that, and the other state <see cref="Compute"/> leaves out (docs/late-join-design.md
/// §10 lists it).
/// </remarks>
public static class WorldStateHash
{
    public static ulong Compute(World world)
    {
        ulong hash = Fnv1a.Offset;
        static ulong Mix(ulong h, uint value) => Fnv1a.Mix(h, value);

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
            hash = Mix(hash, (uint)(int)t.Reactor.Heat);
            hash = Mix(hash, (uint)(int)t.Reactor.Health);
            hash = Mix(hash, (uint)t.OreHeld);
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(t.Heat));
            hash = Mix(hash, t.IsDead ? 1u : 0u);
        }

        foreach (var tankBase in world.TankBases.Bases)
            hash = Mix(hash, (uint)(int)tankBase.Reactor.Health);

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

        for (int i = 0; i < tanks.Count; i++)
        {
            var tip = tanks[i].Turret.GetBarrelTip(tanks[i].Position);
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(tip.X));
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(tip.Y));
        }

        // Machine upgrades; a world without them hashes as it always did.
        hash = world.UpgradeSystem?.MixState(hash) ?? hash;
        // The energy network's islands and capacitors; a world without one hashes as it always did.
        return world.MixBasePower(world.Energy?.MixState(hash) ?? hash);
    }

    /// <summary>
    /// What a client reports when its world is built, before the first tick: <see cref="Compute"/> plus the world's
    /// <see cref="Match.MatchRules"/>, which a freshly built world's state may not show (whether the core ends the
    /// match, for one). The relay refuses to start when two clients report different numbers.
    /// </summary>
    public static ulong ComputeReady(World world) =>
        (Compute(world) ^ world.Rules.Fingerprint()) * Fnv1a.Prime;

    /// <summary>
    /// What machines in a network match compare: <see cref="Compute"/> plus what it does not read. The parts are kept
    /// apart so that, when two machines differ, the first thing known is where. Takes one walk over the grid (heat,
    /// air and scorch together, as the energy total it replaced did); everything else costs what the entities cost.
    /// </summary>
    public static SyncDigest ComputeSync(World world) => ComputeSync(world, []);

    /// <summary>
    /// <see cref="ComputeSync(World)"/> with the bots that drive some of the world's tanks. They are not part of the
    /// world (the session that steps it makes them), but every client runs them, so their state is compared too.
    /// </summary>
    /// <param name="bots">Indexed by slot; null where no bot drives the tank.</param>
    public static SyncDigest ComputeSync(World world, IReadOnlyList<BotTankAI?> bots)
    {
        ulong heat = world.Terrain.MixCellFields(Fnv1a.Offset, out ulong scorch);
        foreach (var tankBase in world.TankBases.Bases)
            heat = tankBase.MixReactorHeat(heat);
        return new(Compute(world), heat, ComputeCharge(world), ComputeEconomy(world), ComputeOutcome(world),
            ComputeLatent(world, scorch), ComputeBots(bots));
    }

    /// <summary>Every bot's state, slot by slot, with the empty slots marked so that the slots line up.</summary>
    private static ulong ComputeBots(IReadOnlyList<BotTankAI?> bots)
    {
        ulong hash = Fnv1a.Offset;
        foreach (BotTankAI? bot in bots)
            hash = bot?.MixState(Fnv1a.Mix(hash, 1)) ?? Fnv1a.Mix(hash, 0);
        return hash;
    }

    /// <summary>
    /// Every holder's stored charge, base by base and battery by battery: a total would not see charge that moved
    /// from one to another.
    /// </summary>
    private static ulong ComputeCharge(World world)
    {
        ulong hash = Fnv1a.Offset;
        foreach (var tankBase in world.TankBases.Bases)
            hash = Fnv1a.Mix(hash, tankBase.Charge);
        foreach (var machine in world.Machines.Machines)
            hash = Fnv1a.Mix(hash, machine.Charge);
        return world.Energy is { } energy ? Fnv1a.Mix(hash, energy.StoredCharge()) : hash;
    }

    /// <summary>
    /// Every base's ore store, upgrade levels and who plays it, which <see cref="Compute"/> does not read (its value
    /// is pinned by the fingerprint tests). Without it two machines could disagree about what a base can pay for
    /// until the difference reached a tank or the terrain.
    /// </summary>
    private static ulong ComputeEconomy(World world)
    {
        ulong hash = Fnv1a.Offset;
        foreach (var tankBase in world.TankBases.Bases)
        {
            hash = Fnv1a.Mix(hash, (uint)tankBase.Store.Ore);
            hash = Fnv1a.Mix(hash, (uint)tankBase.Store.Capacity);
            for (int upgrade = 0; upgrade < BaseUpgrades.Count; upgrade++)
                hash = Fnv1a.Mix(hash, (uint)tankBase.Upgrades.Level((BaseUpgrade)upgrade));
            hash = tankBase.MixEconomy(hash);
        }
        return hash;
    }

    /// <summary>
    /// The race to the core and the match result. They decide who won without passing through anything the other
    /// parts read, so without this two machines could show different winners and never be told.
    /// </summary>
    private static ulong ComputeOutcome(World world)
    {
        ulong hash = world.Goal?.MixOutcome(Fnv1a.Offset) ?? Fnv1a.Offset;
        return world.Result.MixOutcome(hash);
    }

    /// <summary>
    /// State that changes later results but shows in no other part until it does, which can be much later or only
    /// on a rare event: random generators, timers, held keys, harvester memory, pending projectiles, which heat
    /// tiles are awake, the regrowth clock and scorch.
    /// </summary>
    private static ulong ComputeLatent(World world, ulong scorch)
    {
        ulong hash = Fnv1a.Mix(Fnv1a.Offset, scorch);
        foreach (var tank in world.TankList.Tanks)
            hash = tank.MixLatent(hash);
        foreach (var machine in world.Machines.Machines)
            hash = machine.MixLatent(hash);
        hash = world.Projectiles.MixLatent(hash);
        hash = world.LinkMap.MixLatent(hash);
        hash = world.Terrain.MixHeatActivity(hash);
        hash = world.UpgradeSystem?.MixLatent(hash) ?? hash;
        return world.RegrowthSystem?.MixLatent(hash) ?? hash;
    }
}

/// <summary>A world's state for comparison between machines (<see cref="WorldStateHash.ComputeSync"/>).</summary>
/// <param name="State">Terrain pixels, tanks, machines, links and projectiles (<see cref="WorldStateHash.Compute"/>).</param>
/// <param name="Heat">Every cell's heat and air temperature, in cell order, and every base reactor's exact heat.</param>
/// <param name="Charge">Every base's and every battery's stored charge.</param>
/// <param name="Economy">Every base's ore store, upgrade levels and whether a bot plays it.</param>
/// <param name="Outcome">The race to the core and the match result.</param>
/// <param name="Latent">State that shows elsewhere only later: generators, timers, held keys, harvester memory, heat activity, regrowth, scorch.</param>
/// <param name="Bots">Every bot's state, its random stream included.</param>
public readonly record struct SyncDigest(
    ulong State, ulong Heat, ulong Charge, ulong Economy, ulong Outcome, ulong Latent, ulong Bots)
{
    /// <summary>The parts as one number, which is what travels.</summary>
    public ulong Combined
    {
        get
        {
            ulong hash = State;
            foreach (ulong part in (ReadOnlySpan<ulong>)[Heat, Charge, Economy, Outcome, Latent, Bots])
                hash = hash * Fnv1a.Prime ^ part;
            return hash;
        }
    }
}
