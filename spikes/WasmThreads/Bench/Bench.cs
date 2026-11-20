// Spike: how fast does Emberward.Core step in the browser, on one thread and on several, and does it give the
// same bits as the desktop build? Shared by the native console app and the browser app.
namespace Spike;

using System.Diagnostics;
using Emberward.Core;
using Emberward.Core.Bots;
using Emberward.Core.Match;
using Emberward.Core.Config;
using Emberward.Core.Input;
using Emberward.Core.LevelGen;
using Emberward.Core.Rendering;
using Emberward.Core.Types;

public static class Bench
{
    private const ulong ExpectedBotVsBot = 0xBE06ADA8362A015CUL; // SimulationFingerprintTests

    public static void RunAll(Action<string> log, int[] workerCounts, int ticks)
    {
        log($"runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} " +
            $"os={System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
            $"arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} " +
            $"cpus={Environment.ProcessorCount} simd={System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated}");

        // 1. The golden fingerprint from the test suite (320x200, two bots, 1200 ticks).
        var sw = Stopwatch.StartNew();
        ulong golden = GoldenBotVsBot();
        log($"golden BotVsBot: 0x{golden:X16} {(golden == ExpectedBotVsBot ? "MATCHES desktop" : "DIFFERS from desktop 0x" + ExpectedBotVsBot.ToString("X16"))} ({sw.Elapsed.TotalSeconds:F1} s)");

        // 2. Thread probe: does Parallel.For really use more than one thread here?
        log($"thread probe: Parallel.For over 64 items ran on {ProbeThreads()} distinct threads");

        // 3. The reference map with two bots, then the same with heat bursts that keep many tiles stepping.
        foreach (bool hot in new[] { false, true })
        {
            ulong? firstHash = null;
            foreach (int workers in workerCounts)
            {
                var r = RunMatch(workers, hot, ticks);
                string same = firstHash is null ? "" : r.Hash == firstHash ? " same-bits" : " HASH DIFFERS";
                firstHash ??= r.Hash;
                log($"{(hot ? "hot  " : "match")} workers={(workers == 1 ? "1 (serial)" : workers.ToString()),-10} " +
                    $"gen={r.GenMs,7:F0} ms  tick={r.TickMs,7:F2} ms  p95={r.P95Ms,7:F2} ms  heat={r.HeatMs,7:F2} ms  " +
                    $"steppedTiles={r.SteppedTiles,6:F1}  parallelFrames={r.ParallelFrames}/{ticks}  hash=0x{r.Hash:X16}{same}");
            }
        }
        log("done");
    }

    private static int ProbeThreads()
    {
        var ids = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();
        Parallel.For(0, 64, _ =>
        {
            ids[Environment.CurrentManagedThreadId] = true;
            double x = 0;
            for (int i = 0; i < 200_000; i++) x += i * 0.5;
            if (x < 0) Console.WriteLine(x);
        });
        return ids.Count;
    }

    private static ulong GoldenBotVsBot()
    {
        var size = new Size(320, 200);
        var (terrain, spawns) = new ToastGenerator().Generate(size, seed: 42);
        var world = new World(size, simulationSeed: 42);
        world.Initialize(terrain, spawns, decorationSeed: 43);
        var tanks = world.TankList.Tanks;
        var bots = new[] { new BotTankAI(seed: 11), new BotTankAI(seed: 22) };
        for (int frame = 0; frame < 1200; frame++)
            world.Advance(i => bots[i].GetInput(tanks[i], tanks[1 - i], world));
        return Fingerprint(world);
    }

    private readonly record struct Result(double GenMs, double TickMs, double P95Ms, double HeatMs,
        double SteppedTiles, int ParallelFrames, ulong Hash);

    private static Result RunMatch(int workers, bool hot, int ticks)
    {
        const int seed = 1234;
        const int warmup = 100;
        var size = new Size(1500, 750);
        var settings = SimulationSettings.FromTweaks() with
        {
            ThermalMaxWorkers = workers,
            ThermalParallelTileThreshold = workers == 1 ? int.MaxValue : Tweaks.World.ThermalParallelTileThreshold,
        };

        var sw = Stopwatch.StartNew();
        var (terrain, spawns) = new ToastGenerator().Generate(size, seed, LevelGenMode.Deterministic, 2);
        var world = new World(size, simulationSeed: seed, rules: MatchRules.Standard, simulationSettings: settings);
        world.Initialize(terrain, spawns, decorationSeed: seed + 1);
        double genMs = sw.Elapsed.TotalMilliseconds;

        // World.Profile reports and resets every 100 ticks; collect its sums at each report.
        double heatMs = 0;
        long stepped = 0;
        int parallelFrames = 0, profiledFrames = 0;
        bool measuring = false;
        world.Profile.ReportSink = line =>
        {
            if (!measuring || !line.StartsWith("[Profile]", StringComparison.Ordinal)) return;
            heatMs += world.Profile.SectionTime("heat").TotalMilliseconds;
            stepped += world.Profile.CooldownSteppedTiles;
            parallelFrames += world.Profile.CooldownParallelFrames;
            profiledFrames += world.Profile.FrameCount;
        };

        var tanks = world.TankList.Tanks;
        var bots = new BotTankAI[tanks.Count];
        for (int i = 0; i < bots.Length; i++) bots[i] = new BotTankAI(seed + 1 + i);
        uint lcg = 0x9E3779B9u;
        var tickMs = new double[ticks];

        for (int t = 0; t < warmup + ticks; t++)
        {
            if (t == warmup) measuring = true;
            if (hot && t % 3 == 0)
            {
                // Deterministic heat bursts across the map: the stress case for the heat step.
                lcg = lcg * 1664525u + 1013904223u;
                int x = 40 + (int)(lcg % (uint)(size.X - 80));
                lcg = lcg * 1664525u + 1013904223u;
                int y = 40 + (int)(lcg % (uint)(size.Y - 80));
                world.Terrain.AddHeatRadius(new Position(x, y), 400, 24);
            }
            long t0 = Stopwatch.GetTimestamp();
            world.Advance(i => bots[i].GetInput(tanks[i], BotTankAI.PickEnemy(tanks, i), world));
            if (measuring) tickMs[t - warmup] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }

        Array.Sort(tickMs);
        return new Result(genMs, tickMs.Average(), tickMs[(int)(ticks * 0.95)],
            profiledFrames == 0 ? 0 : heatMs / profiledFrames,
            profiledFrames == 0 ? 0 : (double)stepped / profiledFrames, parallelFrames, Fingerprint(world));
    }

    // Copied from SimulationFingerprintTests.
    private static ulong Fingerprint(World world)
    {
        var h = new Fnv64();
        h.Add(WorldStateHash.Compute(world));
        var terrain = world.Terrain;
        int area = terrain.Width * terrain.Height;
        for (int i = 0; i < area; i++)
        {
            h.Add(terrain.GetHeatTemperature(i));
            h.Add(terrain.GetAirTemperature(i));
        }
        var ledger = terrain.Ledger;
        h.Add(ledger.Injected);
        h.Add(ledger.SunkFixedTemperature);
        h.Add(ledger.SunkAmbientLoss);
        h.Add(ledger.SunkBase);
        h.Add(ledger.SunkTank);
        h.Add(ledger.ConvertedToCharge);
        h.Add(world.SumSystemThermalEnergy());
        h.Add(world.SumSystemCharge());
        foreach (var tank in world.TankList.Tanks)
        {
            h.Add(tank.Position.X);
            h.Add(tank.Position.Y);
            h.Add(tank.Direction);
            h.Add(tank.Heat);
            h.Add(tank.OffNetworkFloor);
            h.Add(tank.Reactor.Current.Heat.Value);
            h.Add(tank.Reactor.Current.Health.Value);
            h.Add((int)(tank.Base?.Reactor.Health ?? default));
        }
        foreach (var tankBase in world.TankBases.Bases)
        {
            h.Add(tankBase.Charge);
            h.Add(tankBase.ReactorHeatEnergy);
            h.Add(tankBase.Store.Ore);
        }
        foreach (var machine in world.Machines.Machines)
        {
            h.Add((int)machine.Type);
            h.Add((int)machine.State);
            h.Add(machine.IsAlive ? 1 : 0);
            h.Add(machine.Position.X);
            h.Add(machine.Position.Y);
            h.Add(machine.Charge);
            h.Add(machine.LastPumpedEnergy);
            h.Add(machine.LastChargeGiven);
            h.Add(machine.Reactor.Current.Heat.Value);
            h.Add(machine.Reactor.Current.Health.Value);
        }
        var projectiles = new ProjectileRenderState[world.Projectiles.Count];
        int count = world.Projectiles.CopyRenderStates(projectiles);
        h.Add(world.Projectiles.Count);
        for (int i = 0; i < count; i++)
        {
            var p = projectiles[i];
            h.Add(p.Position.X);
            h.Add(p.Position.Y);
            h.Add(p.Velocity.X);
            h.Add(p.Velocity.Y);
            h.Add((int)p.Type);
            h.Add(p.IsAlive ? 1 : 0);
        }
        return h.Value;
    }

    private struct Fnv64()
    {
        public ulong Value { get; private set; } = 14695981039346656037UL;

        public void Add(ulong bits)
        {
            for (int i = 0; i < 8; i++)
            {
                Value ^= (bits >> (i * 8)) & 0xFF;
                Value *= 1099511628211UL;
            }
        }

        public void Add(int value) => Add((ulong)(uint)value);
        public void Add(float value) => Add((ulong)(uint)BitConverter.SingleToInt32Bits(value));
        public void Add(double value) => Add((ulong)BitConverter.DoubleToInt64Bits(value));
    }
}
