# Tunnerer.Core: the simulation

Core is the whole game without a window: terrain, heat, tanks, machines, the link network, projectiles, level
generation and the bot. It takes one `ControllerOutput` per tank per tick and exposes its state as plain data. It
references no SDL, ImGui or GPU code (`ArchitectureGuardTests`). The rules the tests enforce (determinism, energy
conservation, one heat solver) are listed in `AGENTS.md`; this file says where things are and how they fit.

## Module map

```
World.cs                     World: owns every subsystem, runs Advance; SimulationProfile (per-section timings)
MatchOutcome.cs              Victory / Defeat / InProgress for one tank (World.MatchOutcomeFor)
NetworkStatus.cs             One base's harvesters, relays and heat store, for the HUD (World.GetNetworkStatus)
Config/
  Tweaks.cs                  Every gameplay and simulation constant (const / static readonly only)
  SimulationSettings.cs      The thermal and tank-heat constants, copied from Tweaks; tests inject their own
  FastRandom.cs              Small seeded RNG used by the simulation
Terrain/
  TerrainPixel.cs            TerrainPixel, ThermalMaterial, TerrainBehavior and the Pixel lookup tables
  TerrainGrid.cs             Pixel array, change list, SetPixel (metered) and SetPixelRaw (level gen only)
  TerrainGrid.Heat.cs        Terrain and air temperature fields, CoolDown, energy add / extract API
  TerrainGrid.HeatSparse.cs  Sparse (active-region) solver path
  TerrainGrid.HotRock.cs     PlaceHotRockPocket
  TerrainGrid.Materialization.cs, .Decoration.cs, .Render.cs   Level-gen markers to pixels, cleanup, Draw*ToSurface
  TerrainRegrowth.cs         Dirt regrowth cellular automaton
  TerrainTestPatternGenerator.cs   Synthetic terrain for the renderer's --terrain-test mode
  ITerrain*.cs               Narrow interfaces over TerrainGrid (pixel read / write, heat source, change tracker)
Thermal/
  TerrainHeatEngine.cs       The exchange stencil, capacity and conductance tables, ResolveTerrainTemperature
  ThermalScheduler.cs        Per tick: sleep, full grid or sparse regions
  ThermalActivityTracker.cs  Active tiles (which regions to step) and the heat dirty rect (for the renderer)
  ThermalLedger.cs           Energy created or destroyed, by kind
  TankHeatEngine.cs          One tank's heat exchange with terrain and air
Entities/
  Tank.cs                    Tank state and the per-tick order (below); delegates to Tanks/*
  Tanks/TankThermals.cs      Tank heat, network coverage floor, base sink, refrigeration, ledger bookings
  Tanks/TankDigger.cs        Movement, terrain contact, dig / torch, pickups; owns the tank's RNG
  Tanks/TankBuildController.cs   Build keys: build, carry, plant
  Tanks/TankLifecycle.cs     Damage, death, respawn, spawn shield
  Tanks/TankSpriteShape.cs   The sprite bitmap used as the terrain-contact and pickup shape
  TankTurret.cs              Aim, cooldown, bullet spawn
  TankCollisionMask.cs       Shape bullets hit (separate from the sprite shape)
  TankHeatModel.cs           Heat-flow formula shared by tank contact with terrain and air
  TankList.cs, TankBase.cs, TankBases.cs   Tank collection; bases (reactor, radiator, engine, charge, stores)
  SimulationContext.cs       The slice of World a tank sees during Advance
  MachineMaterializer.cs     The machine a tank carries before it is planted
  Sprite.cs                  SpriteList: short-lived markers
  DamageCause.cs
  Machines/Machine.cs        MachineType, MachineState, Machine
  Machines/MachineBehavior.cs   MachineBehavior (action, interval, cost, BuildKey, traits), MachineBehaviors table
  Machines/MachineList.cs    Machine collection; removes dead machines (their charge becomes heat)
  Machines/MachinePlacementPreview.cs   Result of World.PreviewPlacement
  Projectiles/Projectile.cs  ProjectileType, Projectile and its factories
  Projectiles/ProjectileList.cs   ProjectileBehavior table, advance, explosions; owns the projectile RNG
  Projectiles/ExplosionDesc.cs    ExplosionFactory (shrapnel fans)
  Links/LinkPoint.cs, Link.cs, LinkMap.cs   Link network: nodes, edges, rebuild, power and flow
  Links/NetworkCoverage.cs   Coverage nodes and the coverage a tank gets at a position
Collision/
  CollisionSolver.cs         Point query against tanks, machines and terrain (visitor callbacks)
  WorldSectors.cs            Sector buckets for the tank broad phase
  Raycaster.cs               Bresenham lines; HasOpenLine (the one line-of-sight test)
Input/
  Controller.cs              ControllerOutput
  BotTankAI.cs               The bot's mode machine (TwitchMode)
  BotNavigator.cs, BotGunner.cs, BotBehaviorProfile.cs   Bot movement, fire decisions, play styles
LevelGen/
  ToastGenerator.cs          Cavern generator (LevelGenMode Deterministic or Optimized)
  GeneratorUtils.cs          Shared drawing helpers
  HotRockPockets.cs          Scatters hot rock pockets
Rendering/
  RenderSnapshots.cs         TankRenderState, ProjectileRenderState, MachineRenderState, LinkRenderState, SpriteRenderState
  TankSprites.cs             Tank bitmaps and colour palettes
Resources/Resources.cs       Reactor, ReactorState, MaterialAmount, MaterialContainer
Types/Position.cs, RectMath.cs   Position, Offset, Size, PositionF, VectorF, DirectionF, Color, Rect, BoundingBox, Surface
```

## World

`new World(size, deterministicSimulation, simulationSeed, ...)` creates the subsystems; `World.Initialize(terrain,
spawns, ...)` copies in a generated level, materializes it, builds the bases, places hot rock pockets (if asked)
and adds one tank per spawn. With `deterministicSimulation` every RNG World owns (projectiles, link map, regrowth,
each tank) is seeded from `simulationSeed`; otherwise some fall back to the clock. The bot takes its own seed
(Desktop's `WorldFactory` supplies one). The clock fallbacks are the allow-list in `SourceGuardTests`.

`World.Advance(getInput)` runs one fixed step, in this order:

1. **Collision**: `CollisionSolver.Update` rebuilds the tank sector index.
2. **Heat**: `TerrainGrid.CoolDown` (see below). The energy-balance residual is measured here for the profile.
3. **Regrowth**: `TerrainRegrowth.Advance`.
4. **Projectiles**: `ProjectileList.Advance`.
5. **Tanks**: `TankList.Advance` with a `SimulationContext`. Per tank (`Tank.Advance`): idle heat, base
   interaction (recharge, heat sink, docking), network coverage, turret aim and fire, `TankDigger.Move`, spawn
   shield, shooting, pickups, `TankBuildController.Advance`, then `TankThermals.Advance` (exchange with the grid).
6. **Machines**: `MachineList.Advance`, then each pylon's last hand-out is booked on its route (`LinkMap.CarryCharge`).
7. **Sprites**: lifetime.
8. **Bases**: `TankBases.Advance` (reactor sink, radiator, engine counters).
9. **Links**: `LinkMap.Advance` rebuilds the network every `Tweaks.World.RefreshLinkMapInterval` (200 ms), not
   every tick.

`SimulationProfile` reports averages every 100 ticks when `ReportSink` is set: `[Profile]` (one time per section
above), `[Profile+]` (heat solver parts), `[Profile++]` (active tiles, sparse / parallel ticks) and `[ProfileE]`
(thermal energy and the ledger residual, which should stay near float noise).

Queries Desktop asks instead of computing itself: `World.MatchOutcomeFor` / `IsMatchOver`,
`Tank.IsDockedAtOwnBase`, `World.GetNetworkStatus`, `World.PreviewPlacement`, `World.CoverageFor`,
`World.CollectCoverageNodes`, and the `CopyRenderStates` snapshot of each entity list.

## Heat

Heat lives in two float fields on `TerrainGrid`, terrain and air. Energy is temperature times heat capacity
(`TerrainHeatEngine.GetHeatCapacity` per `ThermalMaterial`, `ThermalCapacityAir` for air). In an air cell the
terrain field only mirrors the air field, so such a cell holds one store.

Each tick `TerrainGrid.CoolDown` asks `ThermalScheduler.Decide` what to do: sleep (nothing active, or the last step
was quiet), step the full grid, or step only the regions around active tiles (`ThermalActivityTracker`), in
parallel when there are many regions. Both paths use `TerrainHeatEngine.ComputeRegionDeltaConservative` and
`ResolveTerrainTemperature`, which pins fixed-temperature materials (base cells, `ConstantEnergy`, hot rock) and
books what the pin removes or adds as `SunkFixedTemperature`.

Entities exchange energy through the `ITerrainHeatSource` API, never by writing temperatures:
`AddEnergyInRadiusArea` / `ExtractEnergyInRadiusArea` for transfers the entity books itself, `AddHeat` /
`AddHeatRadius` / `AddHeatTotalInRadiusArea` for heat that enters from outside (booked as injected). Extraction
takes only heat above ambient and never from hot rock. `SetPixel` books the capacity change when a cell changes
material; `SetPixelRaw` is unmetered and only for level generation.

`ThermalLedger` keeps `Injected`, the `Sunk*` totals and `ConvertedToCharge` (heat the base engine turned into
charge, or charge spent back as heat). `World.SumSystemThermalEnergy` (grid, tanks, base reactors) plus
`World.SumSystemCharge` (base stores, batteries) changes by exactly `Injected - TotalSunk` each tick.

The mechanics built on this (base reservoir and radiator, relays pumping heat home, the base engine and charge,
pylons and batteries, network coverage, tank coolant, hot rock pockets) are specified in `DESIGN.md` §4; the code
is in `TankBase`, `MachineBehaviors`, `TankThermals`, `NetworkCoverage` and `HotRockPockets`.

## Links

Planted machines whose behavior has `IsLinkNode` (relays, pylons) and every base are `LinkPoint`s. At each rebuild
`LinkMap` pairs points within `MaximumTheoreticalLinkDistance`, marks a link live when it is short enough and
`Raycaster.HasOpenLine` finds no blocking pixel (`Pixel.BlocksLink`), and powers points breadth first from the
bases, recording each point's `UpstreamLink` and `Hops`. Relays book pumped heat and pylons book charge along that
route (`LinkMap.CarryHeat`, `LinkMap.CarryCharge`), which the renderer draws as flow. `LinkMap.PreviewLink` answers
"would a node here link?" for planting and for `World.PreviewPlacement`.

## Level generation and the bot

`ToastGenerator.Generate(size, seed, mode)` scatters `Tweaks.LevelGen.TreeSize` points, picks spawns
(`SelectSpawns`), joins the points with a minimum spanning tree (Kruskal) drawn as dirt lines, then either expands
and smooths single-threaded (`Deterministic`) or dilates, adds noise, smooths in parallel and carves spawn
connectivity (`Optimized`, used by `--fast`).

`BotTankAI` is a mode machine (`TwitchMode`: leave base, roam and fight, return, recharge). `BotNavigator` picks
moves, `BotGunner` decides fire, and `BotProfileCycle` rotates the `BotBehaviorProfile` play styles. All three
share one `Random`, so the order of their calls within a tick is part of the behaviour.

## Recipes

Each recipe lists every place that has to change today. Run `dotnet test Tunnerer.slnx` after; the table tests
named below fail when a step is missed.

### Adding a machine

Core:

1. A value in `MachineType` (`Entities/Machines/Machine.cs`).
2. A `MachineBehavior` in `MachineBehaviors` (`Entities/Machines/MachineBehavior.cs`), added to the
   `BuildTable(...)` call that fills `MachineBehaviors.All`: its type, action interval, action, colour, build cost
   and `BuildKey`, plus the traits Core reads instead of the type: `BatteryCapacity`, `IsLinkNode`,
   `RequiresLinkToPlant`, `IsCharger`. The action is a static method in `MachineBehaviors` taking
   `(Machine, MachineContext)`.
3. Constants (cost, interval, battery) in `Tweaks.Machine`, colour in `Tweaks.Colors`.
4. Each `BuildKey` builds exactly one machine, so a fourth machine needs a new key: a value in `BuildKey`, a field
   in `ControllerOutput`, and its edge detection in `TankBuildController.Advance`.
5. `NetworkStatus.Collect` counts harvesters and relays by type; extend it if the HUD should report the new one.
6. Update `MachineBehaviorTableTests` (it names each machine's key and traits).

Desktop:

7. A `MachinePresentation` in `MachinePresentations` (`Presentation/MachinePresentation.cs`): names, keyboard key,
   build-panel slot, build sound, plant hint. `MachinePresentationTests` checks keys and slots are distinct and fit
   the panel.
8. For a new `BuildKey`: `KeyboardController`, `GameSimulationStepper` (merges keyboard and scripted input) and
   `Game.ScriptedInput.cs` (scripted `Build` numbers).
9. Drawing: a case in `Rendering/Entities/MachineQuads.cs` and, if its preview differs, `PreviewQuads.cs`. A new
   shape needs a `Kind*` constant in `EntityShape`, the matching `kKind*` in `Shaders/Entity/Layout.hlsli`, the
   shape function (e.g. `Shaders/Entity/Machines.hlsli`) and a branch in `EntityPs.hlsl`; `EntityLayoutGuardTests`
   checks the three agree.
10. `Gui/HintSelector.cs` names the machines in its hints; add one if the player needs telling.

### Adding a projectile

1. A value in `ProjectileType` (`Entities/Projectiles/Projectile.cs`) and a factory method on `Projectile`.
2. An entry in `ProjectileList.BuildBehaviors`: an advance method `(Projectile, CollisionSolver, ProjectileList)`
   and a draw colour. Use `CollisionSolver.TestPoint` to hit tanks or terrain, `ProjectileList`'s RNG for chance,
   and the heat API (`AddHeat`, `AddHeatRadius`) for heat, so the ledger stays balanced.
3. Something has to spawn it: today only `TankTurret` (bullets) and `ExplosionFactory` (shrapnel) do.
4. Desktop: `Rendering/Entities/ProjectileQuads.cs` draws bullets and shrapnel as hot shots and anything else with
   the foam shape; add a case for a new look. `Audio/GameAudio.cs` plays impacts for bullets and shrapnel only.

### Adding a thermal material

1. A value in `ThermalMaterial` (`Terrain/TerrainPixel.cs`), contiguous from zero.
2. Map pixels to it in `Pixel.DeriveThermalMaterial`. A new `TerrainPixel` also needs an entry in
   `Pixel.BuildBehaviorTable` and its name in the reviewed list of `SimulationTableGuardTests`.
3. In `Thermal/TerrainHeatEngine.cs`: raise `MaterialCount`; add a case to `ComputeHeatCapacity` and a pair for
   every other material in `ComputeConductance` (unlisted pairs silently get the base-to-base value). If it holds
   a fixed temperature, add it to `IsFixedTemperatureMaterial` and `GetFixedTemperature`; if entities must not
   draw heat from it, extend `CannotExtractFrom` in `TerrainGrid.Heat.cs`.
4. Constants in `Tweaks.World`, a property on `SimulationSettings` and its copy in `SimulationSettings.FromTweaks`.
   Thermal code reads only the settings (`SourceGuardTests`).
5. Desktop shades pixels, not materials: a new pixel needs a case in `TerrainAuxBuilder` and
   `TerrainMaterialCoverage`.

### Adding a collidable entity

`CollisionSolver.TestPoint` is the shared point query, but only bullets use it today. Tanks move against terrain
alone (`TankDigger`), machine placement checks `World.IsMachineSpotOccupied`, and roaming harvesters ask
`MachineContext.IsOccupied`. To make bullets (or a new projectile) hit a new entity:

1. A list for it reachable from `World`, and a parameter for it in `CollisionSolver.Update` (called first thing in
   `World.Advance`); add a sector index like the tanks' if there will be many.
2. An optional callback parameter on `TestPoint` for the new entity, checked in the same way as `onMachine`.
3. Pass the callback where hits should land, e.g. `AdvanceBullet` in `ProjectileList`.
