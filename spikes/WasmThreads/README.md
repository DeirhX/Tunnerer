# Spike: Emberward.Core in the browser, with threads

A throwaway measurement, not part of the solution. It asks two questions about a browser port: how fast does
`Emberward.Core` step when compiled to WebAssembly, on one thread and with .NET's experimental multithreaded runtime
(`WasmEnableThreads`), and does it give the desktop's exact bits?

- `Bench/Bench.cs`: the benchmark, shared by both apps. Checks the golden `BotVsBot` fingerprint from
  `SimulationFingerprintTests`, probes how many threads `Parallel.For` really uses, then steps the 1500x750 map
  with two bots for 600 ticks (and again with heat bursts that keep ~964 tiles stepping) at 1, 2, 4 and 8 heat
  workers, printing tick times and a fingerprint of each run.
- `Native/`: the same benchmark as a desktop console app, for the baseline.
- `Browser/`: the browser app (AOT-compiled; `Microsoft.NET.Sdk.WebAssembly`).
- `serve.py`: a static server that sends the cross-origin isolation headers (COOP/COEP) threads need.

## Running

Needs the `wasm-tools` and `wasm-experimental` workloads (`dotnet workload install wasm-tools wasm-experimental`).

```bash
dotnet run -c Release --project spikes/WasmThreads/Native
```

```bash
cd spikes/WasmThreads/Browser
dotnet publish -c Release -p:WasmEnableThreads=false -o ../out/st
rm -rf obj bin && dotnet publish -c Release -p:WasmEnableThreads=true -o ../out/mt
python3 ../serve.py ../out/mt/wwwroot 8702   # then open http://localhost:8702
```

Clear `obj` and `bin` between the two publishes; the two runtimes differ.

## Results (2026-11-17, Apple M5 Max, .NET 10.0.15, Chromium)

Mean milliseconds per tick (budget at 24 ticks/s: 41.7 ms). The threaded columns come from the run with the browser
pane visible; the native and one-thread browser columns come from the first run. Runs in a hidden pane were ~3x
slower, so trust the ratios more than the absolute numbers.

| | Native 1 / 8 workers | Browser, one-thread build | Browser, threads: 1 | 2 | 4 | 8 |
|---|---|---|---|---|---|---|
| Two bots | 2.16 / 0.44 | 6.97 | 6.65 | 3.53 | 1.90 | 1.20 |
| Heat bursts | 8.45 / 1.41 | 24.1 | 23.3 | 12.0 | 6.42 | 3.77 |

- The browser is about 3x slower than native at the same thread count. Threads scale 5.5x on two bots and 6.2x on
  heat bursts from one to eight workers.
- The golden fingerprint matches in both browser builds, and every run's hash is identical across native, browser,
  and all worker counts.
- With threads, .NET runs on a worker and the page stays responsive.
- Firefox and Safari were not tried.

The browser client has since taken this up as an opt-in second runtime; `docs/browser.md` ("Two runtimes") has
what was found in WebKit.
