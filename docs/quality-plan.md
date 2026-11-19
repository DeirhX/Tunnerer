# Code-quality plan

Goal: keep Emberward open to new machines, materials and systems without the code turning into spaghetti.
The thermal core is sound (one stencil, one apply rule, ledger booking everywhere). The problems are around it:
nothing stops regressions, rules have several owners, Desktop computes gameplay, and the docs have drifted.

Baseline (2026-11-15): 0 warnings (no analyzers on), 317 tests pass, 1 skipped as flaky.

## Principles for every package

1. **Bit-for-bit unless stated.** Determinism and replay tests must pass unchanged. A package that changes
   simulation results or visuals is listed under "Decisions" and waits for the owner.
2. **Guard, then refactor.** A rule worth keeping gets a test that fails when it is broken.
3. **One owner per rule, in Core.** Desktop asks Core for answers (docked, match over, network status, build cost).
4. **Delete, don't keep a second implementation.**
5. **Small commits**, `dotnet test Emberward.slnx` green before each one.

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

## Decisions

All owner decisions are made and done (2026-11-16); see Status.

## Status

| Package | Status |
|---|---|
| 1A, 1B, 1C | done (merged 2026-11-15) |
| Golden fingerprint test (`SimulationFingerprintTests`) | done: refactors are checked bit-for-bit against recorded hashes |
| 2A, 2B, 2C | done: Core owns match outcome, docking, network status, placement preview, machine traits; Tank 685→257 lines, bot 583→211; thermal step ~10% faster |
| 3A, 3B, 3C, 3D | done: GameHud 1203→143, EntityLayerBuilder 634→92, EntityPs.hlsl 753→61 (compiled output byte-identical), Game.cs 358→280; flaky test fixed |
| 4 | done: architecture docs rewritten from the code (Core doc 518→221 lines), "Keeping the code healthy" in AGENTS.md; independent review found no serious defects, two weak guard tests tightened |

Cross-platform determinism (2026-11-16): Windows and macOS/Linux gave different results for the same seed because
`Math.Sin/Cos/Pow` and `MathF.*` call the C runtime, which rounds differently per OS. Core now calls
`Types/DeterministicMath` (polynomials using only exactly-rounded operations, ~8 ns per call), and
`SourceGuardTests.Core_UsesDeterministicMath` forbids the platform functions in Core. Windows results are unchanged;
one fingerprint now holds on every OS. Hot-rock pockets without a decoration seed are now seeded from the spawns and
simulation seed instead of the clock.

No unseeded fallbacks (2026-11-16): `ProjectileList`, `Tank`, `BotTankAI`, `ToastGenerator` and terrain decoration
require a seed, and Desktop picks a random one for `--fast` matches (and logs it). Terrain regrowth has one
implementation: hashed per-cell rolls on the step clock, optionally parallel over rows, giving the same result either
way (`TerrainRegrowthTests`). Core's clock allow-list now holds only profiling timers. Seeded runs are unchanged.

Behaviour-preserving cleanup (2026-11-16, PR after #24): every rule the HUD computed is now a Core query (`Tank`,
`TankBase`, `World`, `NetworkStatus`; `PlayerStatusTests`); entity emitters draw machines from
`MachinePresentation.Sprite` with no `MachineType` switch (vertex hashes identical); `NetworkStatus` asks the
`IsMiner`/`IsHeatPump` traits; dead code deleted (foam projectiles and sprite, `MachineList.TestCollide`,
`StepConservative`, always-zero profile timings, 18 unused tweaks). Fingerprints unchanged; Ubuntu CI is required.

Owner decisions (2026-11-16, decisions-g3): `PostParamsBlock` matches `cbuffer PostParams` again
(`PostParamsLayoutGuardTests` packs the .hlsli and compares offsets; gold veins, scorch glow and terrain lighting
now use their configured values); the pylon has its own build sound; automated runs take visual time from the
frame count, so repeated screenshot runs are byte-identical; `--fast` only means parallel (level gen,
materialisation, regrowth), the simulation is always deterministic and `LinkMap` always uses hashed ids.
Fingerprints unchanged.

Owner decisions, rest (2026-11-16, PR after #25):
- Bullets hit machines: enemy bullets damage a machine (`Tweaks.Weapon.BulletDamage`), a tank's own bullets pass
  through its own machines, shrapnel ignores them. At zero health a machine is wrecked: out of the network that
  tick, battery dumped as booked heat, a 5x5 scorch mark that regrows like any scorch, a small shrapnel burst
  (`MachineDamageTests`; new fingerprint run `EnemyMachinesShotToPieces`).
- The bot decides from `Tank.HeatBudget` and the Core heat queries (`Tweaks.Bot`); over 16 bot-vs-bot seeds it
  overheats ~25% less and dies 35 times instead of 43 (new fingerprint run `BotVsBot`).
- `TankThermals` reads `SimulationSettings`; the thermal ratchet's allow-list is empty.
- Thermal materials are one table (`ThermalMaterialTable`): a row per material, series-conductance rule, seven
  explicit overrides for the tuned pairs. Bit-for-bit.
- One tank shape (`TankSpriteShape`, the sprite's cells) for terrain contact, bullet hits and harvester avoidance;
  `TankCollisionMask` and the 7x7 footprint are gone (`TankShapeTests`). Bit-for-bit.
- `World.StepEvents` records each step's happenings; audio plays them instead of diffing state
  (`StepEventTests`, `StepEventSoundsTests`).
- Core's `Draw(Surface)` and `Surface` are gone. The fingerprint hash now reads projectiles, sprites and turret tips
  straight from Core state instead of rasterising them, so all four fingerprints were re-recorded once (no
  simulation change).

After Wave 4: 426 tests, 0 skipped, 0 warnings; no source file over 555 lines; fingerprints unchanged since 01d998e.

## Follow-ups found during the work (not started)

Behaviour-preserving:
- `TerrainGrid` keeps thin thermal forwards for `TerrainGrid.cs`/`HotRock.cs`.
- HintSelector and BuildPanel compare `dirt >= cost` themselves; a `Tank.CanBuild(type)` query would own it.
- `EntityPs_DispatchesEveryShapeKind` and the thermal ratchet were tightened after review; `Machine.Advance` relay ignores `dt` (same result while `AdvanceStep` is the default).

Found while doing the decisions:
- Some bots never leave their base (stuck in ExitBase, overheating inside it; e.g. seed 101, tank 1). Predates the
  heat-budget change.
- `NetworkPanel` compares the powered-relay count between frames; a Core query or step event would own it.
- Machines get no hit flash when damaged (needs a render-state field and shader work).
- `CannotExtractFrom` in `TerrainGrid.Heat.cs` hard-codes hot rock; a table column would own it.
