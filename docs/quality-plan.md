# Code-quality plan

Goal: keep Tunnerer open to new machines, materials and systems without the code turning into spaghetti.
The thermal core is sound (one stencil, one apply rule, ledger booking everywhere). The problems are around it:
nothing stops regressions, rules have several owners, Desktop computes gameplay, and the docs have drifted.

Baseline (2026-11-15): 0 warnings (no analyzers on), 317 tests pass, 1 skipped as flaky.

## Principles for every package

1. **Bit-for-bit unless stated.** Determinism and replay tests must pass unchanged. A package that changes
   simulation results or visuals is listed under "Decisions" and waits for the owner.
2. **Guard, then refactor.** A rule worth keeping gets a test that fails when it is broken.
3. **One owner per rule, in Core.** Desktop asks Core for answers (docked, match over, network status, build cost).
4. **Delete, don't keep a second implementation.**
5. **Small commits**, `dotnet test Tunnerer.slnx` green before each one.

## Wave 1: guardrails and cleanup (parallel)

**1A Guardrails** (build files, `tests/`, CI)
- `Directory.Build.props`: `Nullable`, `TreatWarningsAsErrors`, `AnalysisLevel=latest`, `AnalysisMode=Recommended`,
  `EnforceCodeStyleInBuild`. `.editorconfig`: CA1707 off in tests, CA1051 off (public struct fields are deliberate).
  Fix the ~30 remaining warnings (CA1822, CA1310, CA1305...).
- Guard tests, each a ratchet with today's offenders allow-listed so they can only shrink:
  - no `Random.Shared`, unseeded `new Random()`, `Environment.TickCount*`, `DateTime.Now/UtcNow`, `Stopwatch` in Core
    outside profiling;
  - no `Tweaks.` in `Thermal/` and `TerrainGrid.Heat*.cs`; no mutable statics in `Tweaks`;
  - no `.cs` file over 700 lines (existing offenders capped at current size).
- `FindRepositoryRoot` via `[CallerFilePath]`. One-time `dotnet format whitespace`; add
  `dotnet format --verify-no-changes` and an ubuntu runner to CI.

**1B Core settings and dead code** (Core only)
- Thermal reads go through `SimulationSettings`: ledger bookings in `Tank.cs`, `World.SumSystemThermalEnergy`,
  `TankBase.EngineEfficiency`, ambient/saturation/sleep constants in `TerrainGrid.Heat.cs`, relay interval.
- Delete unread `SimulationSettings` fields, `Tweaks.World.EnableStoneAmbientExchange` and the test lock around it.
- Delete dead Core code: `CaptureTerrainRenderSnapshot`/`TerrainRenderSnapshot`, `TankList.CheckTankCollision`,
  `MachineContext.ForTerrain`, unused `RemoveAll()`s, `ControllerOutput.ShootSecondary`, 5-arg `AddEnergyAt`,
  orphan doc comments.
- One line-of-sight helper (`Raycaster.HasOpenLine`) replacing the four copies in Link, LinkMap, NetworkCoverage,
  MachineBehavior.

**1C Desktop cleanup** (Desktop + `Tweaks.Screen/Colors`)
- Move `Tweaks.Screen` and `Tweaks.Colors` (read only by Desktop) into Desktop config; delete the forwarding wrapper.
- Delete dead Desktop code: `GameHud.ToAbgr`, duplicate `DrawGameViewportFullscreen`, `_gameTimer`,
  `GamePixelsUpload.Pixels/Quality`, `NativeContinuous.Enabled`, `HiResRenderQuality` and the dead staging branch;
  one mouse-in-viewport test.
- Entity animation uses the visual clock passed into `EntityLayerBuilder.Build`, not `Environment.TickCount64`.
- Guard tests: `EntityVertex` layout vs the attribute table; C# entity kind numbers vs `EntityPs.hlsl`.

## Wave 2: single owners in Core, split the large Core classes

**2A Core queries** (Core, then the small Desktop callers)
- `World.MatchOutcome` (replaces dead `SetGameOver`/`IsGameOver` and both Desktop copies of "match over").
- `Tank.IsDockedAtOwnBase` (replaces HUD and audio recomputation; fixes the one-frame-stale build slots).
- `World.GetNetworkStatus(base)`: harvester states, relay inflow, vent capacity, time to overheat.
- `World.PreviewPlacement(type, pos)`: link preview and sense heat.
- `MachineBehavior` gains `IsLinkNode`, `HasBattery`, `RequiresLinkToPlant`, `CarriesFlow` so the per-type switches
  in Tank, World, Machine and NetworkCoverage go away.

**2B Tank and bot** (`Tank.cs`, `BotTankAI.cs`)
- `Tank` becomes a coordinator over `TankThermals`, `TankBuildController`, `TankDigger`, `TankLifecycle`.
- `BotTankAI` splits into mode machine, navigator, gunner; named thresholds; reads `Tank.HeatBudget` and
  `TankBase.IsInside`. RNG call order preserved.

**2C Thermal bookkeeping** (`TerrainGrid.Heat*.cs`)
- `ThermalScheduler` (sleep/throttle), one shared apply-and-mark loop for full and sparse paths. Float op order kept.

## Wave 3: Desktop structure

**3A HUD** (`Gui/`): split `GameHud` into layout, draw helpers, heat gauge, liquid gauges, build panel, warning
banner, match overlay, network panel with a pure `HintSelector` (unit-tested). Uses 2A queries only.
A `MachinePresentation` table (name, key, slot, sound, shader kind) keyed by `MachineType`; costs from
`MachineBehaviors`. Controls screen lists all build keys.

**3B Entity layer** (`EntityLayerBuilder`, `EntityPs.hlsl`): `EntityInterpolator` plus per-family emitters; shader split
into `Entity/*.hlsli`; drop colours the shader ignores; one `Coverage` name.

**3C Game shell** (`Game*.cs`): `WorldFactory` (match vs terrain test), `TerrainAuxUploader`, fewer
`_terrainTestMode` branches.

**3D Tests**: fold `TankHeatTestHelpers` into `TestSimulation`; capacity from `TerrainHeatEngine`; `Slow` trait on
tests over 5 s and the level-gen benchmark; fix or delete the skipped flaky test.

## Wave 4: docs and review

Rewrite both `ARCHITECTURE.md` files to match the code, including an honest "adding a machine" recipe.
Independent review of the whole diff.

## Decisions (owner's call; not started)

| Item | Effect |
|---|---|
| Remove non-deterministic fallbacks (parallel regrowth, "Optimized" level gen, wall-clock seeds); split `--fastgen` from determinism | changes `--fastgen` runs only |
| Fix `PostParamsBlock` one-float misalignment, add layout guard | changes how the game looks |
| Core per-step event list (`World.StepEvents`) and audio driven by it instead of state diffing | audio timing may shift |
| One tank collision shape (sprite vs `TankCollisionMask` vs footprint) | changes collision results |
| Data-driven thermal material table instead of N² conductance constants | refactor of the solver tables |
| Remove Core `Draw(Surface)` methods used only by tests | needs a test rendering replacement |

## Status

| Package | Status |
|---|---|
| 1A, 1B, 1C | done (merged 2026-11-15) |
| Golden fingerprint test (`SimulationFingerprintTests`) | done: refactors are checked bit-for-bit against recorded hashes |
| 2A, 2B, 2C | done: Core owns match outcome, docking, network status, placement preview, machine traits; Tank 685→257 lines, bot 583→211; thermal step ~10% faster |
| 3A, 3B, 3C, 3D | done: GameHud 1203→143, EntityLayerBuilder 634→92, EntityPs.hlsl 753→61 (compiled output byte-identical), Game.cs 358→280; flaky test fixed |
| 4 | done: architecture docs rewritten from the code (Core doc 518→221 lines), "Keeping the code healthy" in AGENTS.md; independent review found no serious defects, two weak guard tests tightened |

After Wave 4: 426 tests, 0 skipped, 0 warnings; no source file over 555 lines; fingerprints unchanged since 01d998e.

## Follow-ups found during the work (not started)

Behaviour-preserving:
- Rules the HUD still computes: "has left base" (`NetworkPanel`), "cooling down", "build from base store", radiator load and fill ratios, `PlayerWarnings` thresholds, heat-scale range. Move each to a Core query.
- Entity emitters still switch on `MachineType` in four places; add a shader kind to `MachinePresentation`.
- `TerrainHeatEngine.StepConservative` is now called only by tests; `TerrainGrid` keeps thin thermal forwards for `TerrainGrid.cs`/`HotRock.cs`.
- `VisualTime` is a real-time stopwatch, so step-locked screenshot runs are not reproducible; derive it from steps in automated runs.
- `RenderingSourceGuardTests`-style guard for `PostParamsBlock` layout vs `PostParams.hlsli` (blocked on the misalignment decision).
- Ubuntu CI job is non-blocking until it has passed once.
- `NetworkStatus.Collect` still checks machine types directly instead of asking `MachineBehaviors`.
- `[Profile+]` `coolPrep`/`coolCopy` are always 0 since the thermal snapshot copy was removed; drop them.
- Dead or unreachable: foam projectiles and their factories, `MachineList.TestCollide`.
- `EntityPs_DispatchesEveryShapeKind` and the thermal ratchet were tightened after review; `Machine.Advance` relay ignores `dt` (same result while `AdvanceStep` is the default).

Change results (owner's call, add to Decisions):
- `TankThermals` still reads coolant capacity, dock cooling, dig radius and base cooldown from `Tweaks`; routing them through `SimulationSettings` changes tests that inject settings.
- Bot reads the int `Reactor.Heat` mirror and pre-coolant thresholds instead of `Tank.HeatBudget`.
- Pylon plays the relay build sound (`SoundBank` has no pylon sound).
- Same seed and inputs give different results on Windows vs macOS/Linux (found by `SimulationFingerprintTests` in CI;
  the player-driving run differs, the network run does not). Likely platform `Math.Sin/Cos/Pow` rounding. Replays and
  seeds are therefore not portable across OSes. Fix by finding the transcendental calls in the simulation path and
  replacing them with table-based or deterministic versions; this changes results.
- Bullets never hit machines (nothing passes `onMachine`, nothing lowers machine health), so machines cannot be destroyed. Gameplay decision.
- `World.Initialize` seeds hot-rock pockets from `Environment.TickCount` when no decoration seed is given, even in deterministic mode.
