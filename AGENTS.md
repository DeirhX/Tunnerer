# Working on Tunnerer

Read this before changing code. `DESIGN.md` says where the game is going; `ARCHITECTURE.md` and
`src/Tunnerer.Core/ARCHITECTURE.md` say how the code is organised.

## Commands

Run from the repository root.

- Build: `dotnet build Tunnerer.slnx`
- Test: `dotnet test Tunnerer.slnx` (about 20 seconds; run it before every commit)
- Run: `dotnet run --project src/Tunnerer.Desktop` (Windows only)

## Layout

- `src/Tunnerer.Core`: the simulation. No window, graphics or input-device dependencies; `ArchitectureGuardTests`
  enforces this.
- `src/Tunnerer.Desktop`: SDL2 window, DirectX 11 renderer, HUD, input. It should read Core state and not own game rules.
- `tests/Tunnerer.Tests`: xUnit tests for Core.

## Rules that tests enforce

- **Determinism.** The same seed and inputs give the same world every frame (`DeterminismTests`, `ReplayTests`).
  Do not read the wall clock, `Random.Shared` or unordered collections in simulation code.
- **Energy is conserved.** Entities exchange *energy* with the grid; they never write temperatures. Energy that is
  created or destroyed is recorded in `ThermalLedger`, and `ThermalLedgerTests` checks the balance every tick.
- **One heat solver.** `TerrainHeatEngine.ComputeRegionDeltaConservative` is the only exchange stencil and
  `ResolveTerrainTemperature` is the only place that applies a step to a cell. The full-grid and sparse paths both
  go through them.

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

Tuning a number: constants live in `Config/Tweaks.cs`. Thermal code reads them through `SimulationSettings`, so
that tests can inject their own values.

## Habits

- Delete code that nothing calls; do not keep a second implementation beside the one in use.
- Give each rule one owner in Core and have Desktop ask for the answer.
- When a change alters simulation results on purpose, say so in the commit message.
