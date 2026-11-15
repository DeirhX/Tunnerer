# Working on Tunnerer

Read this before changing code. `DESIGN.md` says where the game is going; `ARCHITECTURE.md` and
`src/Tunnerer.Core/ARCHITECTURE.md` say how the code is organised.

## Commands

Run from the repository root.

- Build: `dotnet build Tunnerer.slnx`
- Test: `dotnet test Tunnerer.slnx` (about 20 seconds; run it before every commit)
- Run: `dotnet run --project src/Tunnerer.Desktop` (Windows, macOS)

## Layout

- `src/Tunnerer.Core`: the simulation. No window, graphics or input-device dependencies; `ArchitectureGuardTests`
  enforces this.
- `src/Tunnerer.Desktop`: SDL3 window and input, renderer, HUD. It should read Core state and not own game rules.
  - `Rendering/SdlGpu`: the SDL_GPU backend (Metal, Direct3D 12, Vulkan).
  - `Rendering/Shaders`: the HLSL it draws.
- `tools/Tunnerer.ShaderCompiler`: build step that compiles the HLSL for SDL_GPU (DXIL, SPIR-V, MSL) on every OS.
- `tests/Tunnerer.Tests`: xUnit tests for Core, plus source rules for the renderers.

## Rules that tests enforce

- **Determinism.** The same seed and inputs give the same world every frame (`DeterminismTests`, `ReplayTests`).
  Do not read the wall clock, `Random.Shared` or unordered collections in simulation code.
- **Refactors keep results bit-for-bit.** `SimulationFingerprintTests` compares two fixed runs with recorded hashes;
  update a constant only when a change alters results on purpose, and say so in the commit message.
- **Energy is conserved.** Entities exchange *energy* with the grid; they never write temperatures. Energy that is
  created or destroyed is recorded in `ThermalLedger`, and `ThermalLedgerTests` checks the balance every tick.
- **One heat solver.** `TerrainHeatEngine.ComputeRegionDeltaConservative` is the only exchange stencil and
  `ResolveTerrainTemperature` is the only place that applies a step to a cell. The full-grid and sparse paths both
  go through them.
- **Shaders compile for every GPU API.** Every build compiles each shader to DXIL, SPIR-V and MSL, on every OS. Declare
  resources with the macros in `Rendering/Shaders/PostPs/Common/Registers.hlsli`, never `register()`, and give
  every `Texture2D` a `SamplerState` at the same index (`RenderingSourceGuardTests` and the shader build step
  check both).
- **Graphics API calls stay in the backend.** SDL_GPU code belongs in `Rendering/SdlGpu`
  (`RenderingSourceGuardTests`). Code shared with the game goes in `Rendering/`.

## Checklists

Adding a thermal material:

1. `ThermalMaterial` in `Terrain/TerrainPixel.cs`, and `Pixel.DeriveThermalMaterial`.
2. `MaterialCount`, `ComputeHeatCapacity` and `ComputeConductance` in `Thermal/TerrainHeatEngine.cs`.
3. Constants in `Config/Tweaks.cs`, copied in `SimulationSettings.FromTweaks`.
4. `SimulationTableGuardTests` must pass.

Adding a machine:

1. `MachineType` in `Entities/Machines/Machine.cs`.
2. An entry in `MachineBehaviors.Get` in `Entities/Machines/MachineBehavior.cs`.
3. Cost, interval and colour in `Config/Tweaks.cs`.

Adding a shader:

1. The `.hlsl` file in `Rendering/Shaders`, entry point `main`, resources declared with the register macros.
2. An `SdlGpuShader` item (with `Stage`) in `Tunnerer.Desktop.csproj`.

Tuning a number: constants live in `Config/Tweaks.cs`. Thermal code reads them through `SimulationSettings`, so
that tests can inject their own values.

## Habits

- Delete code that nothing calls; do not keep a second implementation beside the one in use.
- Give each rule one owner in Core and have Desktop ask for the answer.
- When a change alters simulation results on purpose, say so in the commit message.
