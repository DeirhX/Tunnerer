# Working on Tunnerer

Read this before changing code. `DESIGN.md` says where the game is going; `ARCHITECTURE.md` and
`src/Tunnerer.Core/ARCHITECTURE.md` say how the code is organised.

## Commands

Run from the repository root.

- Build: `dotnet build Tunnerer.slnx`
- Test: `dotnet test Tunnerer.slnx` (about 35 seconds; run it before every commit)
- Fast tests: `dotnet test Tunnerer.slnx --filter "Category!=Slow"` skips tests marked
  `[Trait("Category", "Slow")]` (over ~5 s each). Use it while iterating; mark new tests over ~5 s `Slow`.
- Run: `dotnet run --project src/Tunnerer.Desktop` (Windows, macOS)

## Layout

- `src/Tunnerer.Core`: the simulation. No window, graphics or input-device dependencies; `ArchitectureGuardTests`
  enforces this.
- `src/Tunnerer.Desktop`: SDL3 window and input, renderer, HUD. It should read Core state and not own game rules.
  - `Rendering/SdlGpu`: the SDL_GPU backend (Metal, Direct3D 12, Vulkan).
  - `Rendering/Shaders`: the HLSL it draws.
- `tools/Tunnerer.ShaderCompiler`: build step that compiles the HLSL for SDL_GPU (DXIL, SPIR-V, MSL) on every OS.
- `tests/Tunnerer.Tests`: xUnit tests for Core and for Desktop logic that needs no window, plus guard tests.

## Rules that tests enforce

- **Determinism.** The same seed and inputs give the same world every frame (`DeterminismTests`, `ReplayTests`).
  Do not read the wall clock, `Random.Shared` or unordered collections in simulation code. Results must also match
  across operating systems, so Core calls `DeterministicMath` instead of `Math`/`MathF` transcendentals
  (`SourceGuardTests`).
- **Energy is conserved.** Entities exchange *energy* with the grid; they never write temperatures. Energy that is
  created or destroyed is recorded in `ThermalLedger`, and `ThermalLedgerTests` checks the balance every tick.
- **One heat solver.** `TerrainHeatEngine.ComputeRegionDeltaConservative` is the only exchange stencil and
  `ResolveCell` is the only place that applies a step to a cell. Every tile the solver steps goes through them.
- **Heat costs what is changing, not the map.** The solver steps only tiles where heat still changes
  (`ThermalActivityTracker`); settled ground is left alone until something writes heat into it or digs it. Do not
  add a heat pass that visits every cell each tick, and wake a tile (`ActivateTileByCell`) from anything new that
  changes a temperature or a cell's material. Regrowth follows the same rule through `RegrowthTiles`.
- **Shaders compile for every GPU API.** Every build compiles each shader to DXIL, SPIR-V and MSL, on every OS. Declare
  resources with the macros in `Rendering/Shaders/PostPs/Common/Registers.hlsli`, never `register()`, and give
  every `Texture2D` a `SamplerState` at the same index (`RenderingSourceGuardTests` and the shader build step
  check both).
- **Graphics API calls stay in the backend.** SDL_GPU code belongs in `Rendering/SdlGpu`
  (`RenderingSourceGuardTests`). Code shared with the game goes in `Rendering/`.

## Checklists

Adding a thermal material:

1. `ThermalMaterial` in `Terrain/TerrainPixel.cs`, and `Pixel.DeriveThermalMaterial` if pixels map to it.
2. Its row in `Tweaks.World.ThermalMaterials` (`Config/Tweaks.cs`): heat capacity, conductivity, air exchange and
   either a fixed temperature (a source or sink) or a loss (the share of its heat it gives up to the outside each
   tick). Pair conductances are derived from the rows (the two conductivities
   in series, `ThermalMaterialTable.PairConductance`). Add a pair to the table's override list only when the game
   needs a value the rule cannot produce.
3. `SimulationTableGuardTests` must pass.

Adding a machine:

1. `MachineType` in `Entities/Machines/Machine.cs`.
2. A `MachineBehavior` in `MachineBehaviors` (`Entities/Machines/MachineBehavior.cs`), listed in `MachineBehaviors.All`:
   action, interval, colour, build cost, build key, and the traits Core reads instead of the type (`BatteryCapacity`,
   `IsLinkNode`, `RequiresLinkToPlant`, `IsCharger`, `IsMiner`, `IsHeatPump`). `MachineBehaviorTableTests` must pass.
3. Cost, interval, colour and battery size in `Config/Tweaks.cs`.
4. Desktop: a `MachinePresentation` (name, key, build slot, sound, and the `MachineSprite` the entity emitters draw:
   shader shape, size, gauge, sense ring, battery, preview reach; `Presentation/MachinePresentation.cs`).
   `MachinePresentationTests` must pass. A new shape also needs a shader branch. Only three build keys exist
   (`BuildKey`), so a fourth machine also needs a new key. The full recipe is in `src/Tunnerer.Core/ARCHITECTURE.md`.

Adding a shader:

1. The `.hlsl` file in `Rendering/Shaders`, entry point `main`, resources declared with the register macros.
2. An `SdlGpuShader` item (with `Stage`) in `Tunnerer.Desktop.csproj`.

Tuning a number: constants live in `Config/Tweaks.cs`. Thermal code reads them through `SimulationSettings`, so
that tests can inject their own values.

## Keeping the code healthy

- **One owner per rule, in Core; Desktop asks.** If the HUD, sound or renderer needs a game answer (docked, match
  over, network status, build cost, placement preview), add a query to Core and call it; do not recompute it.
  To learn that something happened (a shot, a hit, a death, a planting), read `World.StepEvents` after each step
  instead of comparing state between steps; a new kind of happening is a new `StepEventKind`.
- **Refactors keep results bit-for-bit.** `SimulationFingerprintTests` compares two fixed runs with recorded
  hashes. Change a constant only when a change alters results on purpose, and say so in the commit message.
- **Analyzers are errors.** `Directory.Build.props` turns on nullable, `AnalysisMode=Recommended`, code style in
  build and `TreatWarningsAsErrors`; CI also runs `dotnet format --verify-no-changes`. Fix the warning; suppress
  only in `.editorconfig` with a reason.
- **Guard tests** (each fails with the place to fix):
  - `ArchitectureGuardTests`: Core references no Desktop, SDL, ImGui or graphics packages.
  - `SourceGuardTests`: no clocks or unseeded randomness in Core; thermal code reads `SimulationSettings`, not
    `Tweaks`; `Tweaks` has no mutable statics; no `.cs` file over 700 lines. Each is a ratchet: today's offenders
    are allow-listed, a new one fails, and a fixed one fails until its entry is deleted. Split a long file; never
    raise a cap.
  - `RenderingSourceGuardTests`: SDL_GPU only in `Rendering/SdlGpu`; shader resources only through the register
    macros.
  - `EntityLayoutGuardTests`: `EntityVertex` matches the vertex attribute table, and the shape kinds and machine
    flags in `EntityShape` match `Shaders/Entity/Layout.hlsli` and the dispatch in `EntityPs.hlsl`.
  - `SimulationTableGuardTests`, `MachineBehaviorTableTests`, `MachinePresentationTests`: every enum value has its
    table entries.
- **Vision is output only, and its cost follows what changes.** `VisionMap` (fog of war) is read by Desktop and by
  nothing in the simulation. A source is cast when it appears or moves, or when ground that blocks sight changes
  within its reach; sources that stand still in ground that stays as it is cost nothing. Do not add a pass that
  recasts every source or sweeps the grid each tick, and change pixels during play through `SetPixel`, which is
  how vision and the renderer learn of it.
- Delete code that nothing calls; do not keep a second implementation beside the one in use.
