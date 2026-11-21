# Working on Emberflow

Read this before changing code. `DESIGN.md` says where the game is going; `ARCHITECTURE.md` and
`src/Emberward.Core/ARCHITECTURE.md` say how the code is organised.

## Commands

Run from the repository root.

- Build: `dotnet build Emberward.slnx`
- Test: `dotnet test Emberward.slnx` (about 35 seconds; run it before every commit)
- Fast tests: `dotnet test Emberward.slnx --filter "Category!=Slow"` skips tests marked
  `[Trait("Category", "Slow")]` (over ~5 s each, and the socket tests that must run on their own, in
  `TimedTestGroup`); about 8 seconds. Use it while iterating; mark new tests over ~5 s `Slow`. Slow tests start first
  (`SlowFirstCollectionOrderer`), and xUnit runs a class's tests one after another, so give a slow test a class of
  its own when its class already has one.
- Run: `dotnet run --project src/Emberward.Desktop` (Windows, macOS)

## Layout

- `src/Emberward.Core`: the simulation. No window, graphics or input-device dependencies; `ArchitectureGuardTests`
  enforces this.
- `src/Emberward.Desktop`: SDL3 window and input, renderer, HUD. It should read Core state and not own game rules.
  - `Net`: a client's part in a network match (`docs/multiplayer-design.md`).
- `src/Emberward.Protocol`: what the game client and the relay server share. It references no other project;
  `ArchitectureGuardTests` enforces this.
- `src/Emberward.Relay`: the relay server for network matches. It references only the protocol, never Core, so a
  gameplay change needs no new relay. Run: `dotnet run --project src/Emberward.Relay`.
  - `Rendering/SdlGpu`: the SDL_GPU backend (Metal, Direct3D 12, Vulkan).
  - `Rendering/Shaders`: the HLSL it draws.
- `src/Emberward.Rendering`: the rendering code every host shares (terrain fields and what a frame uploads of them,
  entity emitters and machine sprites, the shader constant block, sound (the step-event table, `SoundDirector` and `SoundBank`), the heat colours (`HeatScale`), what a build slot says (`BuildSlotView`), the HUD palette and the charge labels (`HudPalette`, `PowerLabels`) and the HUD's
  guidance: tutorial, toasts, build-key hint and warnings), and a client's part in a network match (`NetSession`,
  `SlotStepper`, `NetMatch`). Its sources are the files of `src/Emberward.Desktop` listed in
  `Emberward.Rendering.csproj`, which `Emberward.Desktop.csproj` leaves out: sharing a file means adding it to both
  lists. It references only Core and the protocol (`ArchitectureGuardTests`).
- `src/Emberward.Browser.Client`: the browser's match (local, or network through `BrowserNetMatch`) and what it
  draws, in plain .NET so the tests cover it.
- `src/Emberward.Browser`: the WebAssembly exports and the web page (`wwwroot`: WebGL, input, HUD, offline shell).
  Not in `Emberward.slnx`, because building it needs a .NET WebAssembly workload; `docs/browser.md` has the commands.
- `tools/browser`: publish, serve and test the browser client.
- `tools/Emberward.ShaderCompiler`: build step that compiles the HLSL for SDL_GPU (DXIL, SPIR-V, MSL) on every OS,
  and with `--web` to GLSL ES for the browser.
- `tests/Emberward.Tests`: xUnit tests for Core and for Desktop logic that needs no window, plus guard tests.

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
  changes a temperature or a cell's material.
- **Regrowth costs what is growing, not the map.** `TerrainRegrowth` keeps an appointment for each cell that can
  change (open ground beside dirt, scorch, dirt still growing) and looks at nothing else; it hears of new ones from
  pixel writes (`RegrowthWrites`). Do not add a regrowth pass that scans the map or its tiles, and write pixels
  through `SetPixel` or `SetPixelRaw` so that it hears of them.
- **Shaders compile for every GPU API.** Every build compiles each shader to DXIL, SPIR-V and MSL, on every OS. Declare
  resources with the macros in `Rendering/Shaders/PostPs/Common/Registers.hlsli`, never `register()`, and give
  every `Texture2D` a `SamplerState` at the same index (`RenderingSourceGuardTests` and the shader build step
  check both). A shader function returns only at its end: write `if (needed) { ... }`, not `if (!needed) return;`.
  Windows browsers compile the early-return form through FXC, which hung on it (`RenderingSourceGuardTests`,
  `docs/browser.md`).
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
   action, interval, colour, build cost, build key, its `Capacitor` (capacity, transfer rate, `PowerTier`, idle draw;
   DESIGN.md §4.9), and the traits Core reads instead of the type (`RunsOnCharge`, `IsLinkNode`,
   `RequiresLinkToPlant`, `IsMiner`, `IsHeatPump`, `IsGenerator`, `IsTurret`, `NeedsEnergyNetwork`), and its `UpgradePaths` if it takes per-machine
   upgrades (`MachineUpgrades.cs` says what each path does). `MachineBehaviorTableTests` must pass.
3. Cost, interval, colour and capacitor numbers in `Config/Tweaks.cs`, or in a partial of its own beside it
   (`Tweaks.Turrets.cs`) while `Tweaks.cs` is near the file-length cap.
4. Desktop: a `MachinePresentation` (name, key label, build slot, plant hint; `Presentation/MachinePresentation.cs`), a
   `MachineSprite` in `MachineSprites` (what the entity emitters draw on desktop and in the browser: shader shape,
   size, gauge, sense ring, capacitor gauge, preview reach; `Rendering/Entities/MachineSprite.cs`) and its build sound in
   `StepEventSounds.BuildSound` (`Audio/StepEventSounds.cs`). `MachinePresentationTests` must pass. A new shape also needs a shader branch. Each machine
   needs a distinct `BuildKey` with desktop, browser and codec wiring. Bit 31 of the encoded input is the only free one
   (it was the pylon's fifth build key): a fifth build key takes it and bumps the protocol. The full recipe is in `src/Emberward.Core/ARCHITECTURE.md`.

Adding a shader:

1. The `.hlsl` file in `Rendering/Shaders`, entry point `main`, resources declared with the register macros.
2. An `SdlGpuShader` item (with `Stage`) in `Emberward.Desktop.csproj`.

Removing a system (the gold pickup was the first; its commit is a worked example):

1. Find every reference: `git grep -n -i <name>` over the whole repository, shaders and docs included, and again
   for its types, its `Tweaks` constants, its `StepEventKind` values and any `TerrainPixel` it adds.
2. Core: its `MatchRules` flag and its entry in `World.BuildSystems`; the state it keeps; its `Tweaks` section;
   its `StepEventKind` values; its `TerrainPixel` rows (and the name list in `SimulationTableGuardTests`); its
   level-generation step. Delete what only it called (gold was the only pickup, so the pickup step went too) and
   rewrite comments that list it beside other things.
3. Desktop: the HUD, guidance, sound (`StepEventSounds`) and presentation that read it. In the renderer: its material
   code in `TerrainAuxBuilder` and `Constants.hlsli`, its shader files and their `#include`, and any `PostParams`
   row. Remove a row from `PostParamsBlock` and `PostParams.hlsli` together and raise the layout version on both
   (`PostParamsLayoutGuardTests`).
4. Tests: delete the tests of what was removed. A test that only used it as a stand-in keeps what it tests with
   something else; check that the replacement still reaches the case (ranges differ between things).
5. If level generation or the simulation changes, update the constants in `SimulationFingerprintTests` and say so
   in the commit message.
6. Docs: `DESIGN.md` (what is in the game, ranges, the decision log), the architecture files, and these
   checklists.

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
    `Tweaks`; `Tweaks` has no mutable statics; pixels change through `SetPixel` outside level generation; no `.cs`
    file over 700 lines. Each is a ratchet: today's offenders
    are allow-listed, a new one fails, and a fixed one fails until its entry is deleted. Split a long file; never
    raise a cap.
  - `RenderingSourceGuardTests`: SDL_GPU only in `Rendering/SdlGpu`; shader resources only through the register
    macros.
  - `EntityLayoutGuardTests`: `EntityVertex` matches the vertex attribute table, and the shape kinds and machine
    flags in `EntityShape` match `Shaders/Entity/Layout.hlsli` and the dispatch in `EntityPs.hlsl`.
  - `SimulationTableGuardTests`, `MachineBehaviorTableTests`, `MachinePresentationTests`: every enum value has its
    table entries.
  - `SnapshotCoverageTests`: every field a snapshot reaches can be saved, and every `NotSaved` mark is listed with
    its reason.
- **A machine looks at what is near it, not at every machine.** `MachineList.CollectNear` returns the machines
  around a place, in list order. Use it (or `MachineList.Chargers`) for any rule that involves other machines;
  do not loop over `MachineList.Machines` inside a machine's action, a collision test or a per-tank query. A
  relay sleeps while its ground has no heat (`QuietHeatStamp`); anything new that changes a temperature must
  go through the heat field's own writers, which wake the tile and so the relay.
- **The link map keeps its links.** `LinkMap` casts a link's line of sight again only when a cell near it
  started or stopped blocking (`SightlineChanges`) and searches power again only when a link or point changed.
  Do not add a rebuild of every pair or every link on a timer; what a refresh gives must stay what a rebuild
  from nothing gives (`LinkMapTests`).
- **Vision is output only, and its cost follows what changes.** `VisionMap` (fog of war) is read by Desktop and by
  nothing in the simulation. A source is cast when it appears or moves, or when ground that blocks sight changes
  within its reach; sources that stand still in ground that stays as it is cost nothing. Do not add a pass that
  recasts every source or sweeps the grid each tick, and change pixels during play through `SetPixel`, which is
  how vision and the renderer learn of it.
- **The swarm costs what is out, not what is nested.** `EmberiteSwarm` (`MatchRules.Emberites`, needs `SeedOre`) keeps a nest as
  counters and holds only the creatures out of their nests in its list. A dormant nest costs one distance test per tank
  every 6 ticks; do not add a per-creature or per-nest pass that runs while nothing is out (`EmberiteSwarmTests` checks that
  `CreatureSteps` stays 0). Flow fields are built only while a creature reads them. Its heat is conserved (booked in the
  ledger, counted in `World.SumSystemThermalEnergy`).
- **Entities are drawn by extrapolation in the vertex shader.** An `EntityVertex` carries its corner in world cells and
  its motion in cells per step; `EntityVs.hlsl` draws it at `corner + motion * StepAlpha` and moves the camera the same
  way. Give a new moving quad its motion (`EntityQuads.Add(..., motion)`), and keep `EntityVertex`, `EntityVertexAttributes`,
  `webgl.js` (`ENTITY_ATTRIBUTE_FLOATS`) and `VSIn` in step (`EntityLayoutGuardTests`).
- **A system that can be switched off has a flag in `MatchRules`** (`Match/MatchRules.cs`) and costs nothing while
  the flag is off: `World.BuildSystems` leaves it out of the step. Do not add a `World` constructor argument, a
  property set after construction, or a null delegate that means "off". The rules are in the hash clients compare
  before a network match starts.
- **A system keeps its own state.** What a system tracks per base or per tank lives in the system, and `World`
  answers for it (the sector goal keeps each base's `CoreTap`; Desktop asks `World.CoreTapOf`). Do not add a new
  system's fields to `TankBase` or `Tank`: then removing the system leaves them untouched.
- **A new field is in the snapshot.** `WorldSnapshot` (a late joiner's copy of the match) saves every field reachable
  from the world and the bots. Mark a field `[NotSaved("reason")]` only when it is rebuilt on load
  (`ISnapshotLoaded`), fixed for the match by the settings, output only, or scratch a step fills before it reads it,
  and add it to the list in `SnapshotCoverageTests`. Hold state in fields, arrays and `List<T>`: the saver refuses a
  dictionary, a set or a delegate, and a keyed lookup is rebuilt on load, never saved. `SnapshotRoundTripTests` must
  pass.
- **A seat changes hands only through the bundle.** In a network match, who drives a tank (a player or a bot, and
  the base's `IsBotControlled`) changes only when a bundle's human mask does, applied by `Net/Seats` before the step,
  so every client and every replay changes it at the same tick. Do not make or drop bots, or call
  `World.SetBotControlled`, from anything else once the match has started.
- Delete code that nothing calls; do not keep a second implementation beside the one in use.
- Ask the shared question instead of spelling it out: `Machine.IsStanding` / `StandsFor(base)`; `Pixel.IsOpenGround`,
  `IsSoil`, `IsSolidGround`, `IsAnyBaseCell`; `BaseUpgrades.BaseCoverageRadius` / `NodeCoverageRadius`; per-second
  rates on `CoreTap` and `TankBase`; HUD colours from `HudTheme` (`LevelColor`, `WithAlpha`, `WithAlphaByte`; their
  values live in `HudPalette`, which the browser shares). A new
  pixel kind, machine or upgrade then changes one place.
