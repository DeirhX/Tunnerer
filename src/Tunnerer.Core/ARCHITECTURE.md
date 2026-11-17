# Tunnerer.Core: the simulation

Core is the whole game without a window: terrain, heat, tanks, machines, the link network, projectiles, level
generation and the bot. It takes one `ControllerOutput` per tank per tick and exposes its state as plain data. It
references no SDL, ImGui or GPU code (`ArchitectureGuardTests`). The rules the tests enforce (determinism, energy
conservation, one heat solver) are listed in `AGENTS.md`; this file says where things are and how they fit.

## Module map

```
World.cs                     World: owns every subsystem, runs Advance; SimulationProfile (per-section timings)
StepEvents.cs                StepEvent / StepEventLog: what happened during a step (World.StepEvents)
MatchOutcome.cs              Victory / Defeat / InProgress for one tank (World.MatchOutcomeFor)
NetworkStatus.cs             One base's harvesters, relays and heat store, for the HUD (World.GetNetworkStatus)
StatusLevel.cs               Ok / Warn / Bad for a HUD gauge (Tank.ShieldLevel, TankBase.ChargeLevel, ...)
Config/
  Tweaks.cs                  Every gameplay and simulation constant (const / static readonly only)
  SimulationSettings.cs      The thermal and tank-heat constants, copied from Tweaks; tests inject their own
  FastRandom.cs              Small seeded RNG used by the simulation
Terrain/
  TerrainPixel.cs            TerrainPixel, ThermalMaterial, TerrainBehavior and the Pixel lookup tables
  TerrainGrid.cs             Pixel array, change list, SetPixel (metered) and SetPixelRaw (level gen only)
  TerrainGrid.Heat.cs        Terrain and air temperature fields, CoolDown, energy add / extract API
  TerrainGrid.HeatSparse.cs  Sparse (active-region) solver path
  TerrainGrid.HotRock.cs     PlaceHotRockPocket, PlaceFrostRockPocket, SetPinnedPixel
  TerrainGrid.Ore.cs         PlaceOre (level gen)
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
  Tanks/TankDigger.cs        Movement, terrain contact, dig / torch, mining ore into the hold, pickups; owns the
                             tank's RNG
  Tanks/TankBuildController.cs   Build keys: build, carry, plant; the payer rule (Quote)
  Tanks/BuildQuote.cs        BuildQuote, OrePayer, BuildRefusal: what a machine costs here and which stock pays
  Tanks/TankLifecycle.cs     Damage, death (the hold spills as ore, OreDrop), respawn, spawn shield
  Tanks/TankSpriteShape.cs   The tank's one shape (its sprite's cells): terrain contact, pickups, bullet hits, planting
  TankTurret.cs              Aim, cooldown, bullet spawn
  TankHeatModel.cs           Heat-flow formula shared by tank contact with terrain and air
  TankList.cs, TankBase.cs, TankBases.cs   Tank collection; bases (reactor, radiator, engine, charge, ore store)
  BaseUpgrades.cs            BaseUpgrade (base track and tank track), levels, costs and what each level does
  SimulationContext.cs       The slice of World a tank sees during Advance
  MachineMaterializer.cs     The machine a tank carries before it is planted
  Sprite.cs                  SpriteList: short-lived markers
  DamageCause.cs
  Machines/Machine.cs        MachineType, MachineState, Machine
  Machines/MachineBehavior.cs   MachineBehavior (action, interval, cost, BuildKey, traits), MachineBehaviors table
  Machines/MachineList.cs    Machine collection; wrecks dead machines (leave the network, charge to heat, scorch)
  Machines/MachinePlacementPreview.cs   Result of World.PreviewPlacement
  Projectiles/Projectile.cs  ProjectileType, Projectile and its factories
  Projectiles/ProjectileList.cs   ProjectileBehavior table, advance, explosions; owns the projectile RNG
  Projectiles/ExplosionDesc.cs    ExplosionFactory (shrapnel fans)
  Links/LinkPoint.cs, Link.cs, LinkMap.cs   Link network: nodes, edges, rebuild, power and flow
  Links/NetworkCoverage.cs   Coverage nodes and the coverage a tank gets at a position
Sensing/
  VisionMap.cs               Fog of war for one side: visibility levels, explored cells, the terrain as last seen
  Radar.cs                   A tank's radar: pulses running out from the tank and the fading contacts where they reflected
  RadarReach.cs              The radar's ranges and heat budget, scaled per tank by its radar upgrade
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
  FrostRockPockets.cs        Scatters frost pockets
  OreDeposits.cs             Seeds emberite: specks, pockets and lumps with a warm core (OreDeposit, OreDepositKind)
Rendering/
  RenderSnapshots.cs         TankRenderState, ProjectileRenderState, MachineRenderState, LinkRenderState, SpriteRenderState
  TankSprites.cs             Tank bitmaps and colour palettes
Resources/Resources.cs       Reactor, ReactorState, OreStore (a tank's hold, a base's store: ore is the only currency)
Resources/OreDrop.cs         Puts ore back into the ground, nearest free cells first (a dead tank's hold)
Types/Position.cs, RectMath.cs   Position, Offset, Size, PositionF, VectorF, DirectionF, Color, Rect, BoundingBox
```

## World

`new World(size, parallelRegrowth, simulationSeed, ...)` creates the subsystems; `World.Initialize(terrain,
spawns, ...)` copies in a generated level, materializes it, builds the bases, places hot and frost rock pockets (if
asked; the hot ones also give the core, `World.CorePocket`), seeds emberite ore (if asked, `seedOre`;
`World.OreDeposits`) and adds one tank per spawn. Ore is placed last and grows each deposit cell by cell from its
start, so generation never sweeps the grid; deposit counts scale with map area (`Tweaks.Ore`), and on a small map the
distance rules shrink and a floor of pockets and lumps applies (`OreDeposits.DistanceScale`). Every RNG World owns
(projectiles, link map, regrowth, each tank) is seeded from `simulationSeed`, and decorations and rock pockets from
the decoration seed (or, without one, from the spawns and
`simulationSeed`). Core never reads the clock or unseeded randomness; for a random match Desktop's `WorldFactory`
picks the seed. `parallelRegrowth` (`--fast`) runs regrowth over rows in parallel, with the same result. The bot takes its own seed, also from `WorldFactory`.

`World.Advance(getInput)` runs one fixed step, in this order:

1. **Collision**: `CollisionSolver.Update` rebuilds the tank sector index.
2. **Heat**: `TerrainGrid.CoolDown` (see below). The energy-balance residual is measured here for the profile.
3. **Regrowth**: `TerrainRegrowth.Advance`. No new dirt grows within the coverage radius of a planted relay or
   pylon (`RegrowthBlocker`), linked or not, so the ground a network runs through stays open.
4. **Projectiles**: `ProjectileList.Advance`.
5. **Tanks**: `LinkMap.AgeOreFlow`, then `TankList.Advance` with a `SimulationContext`. Per tank (`Tank.Advance`):
   idle heat, base interaction (recharge, heat sink, docking: the hold banks into the store), network coverage, the
   network unload (inside linked coverage the hold moves `Ore.NetworkUnloadPerSecond` into the base store, booked on
   the links with `LinkMap.CarryOre` and recorded as `StepEventKind.OreUnloaded`), turret aim and fire,
   `TankDigger.Move`, spawn shield, shooting, pickups, `TankBuildController.Advance`, the upgrade keys, then
   `TankThermals.Advance` (exchange with the grid).
6. **Machines**: `MachineList.Advance` (a harvester asks `MachineContext.IsOnNetwork`, the coverage of its base's
   network at its position, before it works), then each pylon's last hand-out is booked on its route
   (`LinkMap.CarryCharge`).
7. **Sprites**: lifetime.
8. **Bases**: `TankBases.Advance` (reactor sink, radiator, engine counters), the bot's stand-in ore income for bases
   marked with `World.SetBotControlled`, then `World.AdvanceSectorGoal` (the core tap and its hold timer).
9. **Links**: `LinkMap.Advance` rebuilds the network every `Tweaks.World.RefreshLinkMapInterval` (200 ms), not
   every tick.
10. **Vision**: each `VisionMap` handed out by `World.TrackVision` is brought up to date and each `Radar` handed
    out by `World.TrackRadar` runs its pulse on (see "Radar" below).

`SimulationProfile` reports averages every 100 ticks when `ReportSink` is set: `[Profile]` (one time per section
above), `[Profile+]` (heat solver parts), `[Profile++]` (active tiles, sparse / parallel ticks) and `[ProfileE]`
(thermal energy and the ledger residual, which should stay near float noise).

Queries Desktop asks instead of computing itself: `World.MatchOutcomeFor` / `IsMatchOver`,
`Tank.IsDockedAtOwnBase`, `Tank.IsInOwnNetwork`, `Tank.IsUnloadingOre`, `Tank.QuoteBuild` (cost, payer, stock and
refusal for a machine), `Tank.LastBuildRefusal`, `World.GetNetworkStatus`, `World.PreviewPlacement`, `World.CoverageFor`,
`World.CollectCoverageNodes`, `World.TrackVision`, `World.TrackRadar`, `World.RadarOf`, and the `CopyRenderStates` snapshot of each entity list.

### Step events

Desktop learns about one-off happenings from `World.StepEvents`, never by comparing state between steps. Each
`Advance` clears the list first, then the code where something happens records a small `StepEvent` (kind, position,
the tank or side's colour, and a machine type, impact target, cell count or torch flag where they apply): shots
fired, bullet impacts, explosions, spent shrapnel, tank hits, deaths and respawns, moving, digging, pickups,
docking and leaving the base, base service, and machines built, planted, refused, damaged and wrecked. The list
holds the last step's events until the next step begins, so a caller that runs several steps per frame reads it
after each one. It is output only: nothing in the simulation reads it, so recording cannot change a result
(`SimulationFingerprintTests`), and the `StepEventLog` reuses its storage, so recording does not allocate in steady
state. Tanks record through `SimulationContext.Events`, projectiles through the log `ProjectileList` is built with,
and `World` itself records plantings and docking. A new kind of happening that presentation needs is a new
`StepEventKind`, recorded where it happens (`StepEventTests` covers each kind).

## Heat

Heat lives in two float fields on `TerrainGrid`, terrain and air. Energy is temperature times heat capacity
(`TerrainHeatEngine.GetHeatCapacity` per `ThermalMaterial`, `AirHeatCapacity` for air). In an air cell the
terrain field only mirrors the air field, so such a cell holds one store.

Each tick `TerrainGrid.CoolDown` asks `ThermalScheduler.Decide` what to do: sleep (nothing active, or the last step
was quiet), step the full grid, or step only the regions around active tiles (`ThermalActivityTracker`), in
parallel when there are many regions. Both paths use `TerrainHeatEngine.ComputeRegionDeltaConservative` and
`ResolveTerrainTemperature`, which pins fixed-temperature materials (base cells, `ConstantEnergy`, hot rock, frost rock, emberite cores) and
books what the pin removes or adds as `SunkFixedTemperature`.

Entities exchange energy through the `ITerrainHeatSource` API, never by writing temperatures:
`AddEnergyInRadiusArea` / `ExtractEnergyInRadiusArea` for transfers the entity books itself, `AddHeat` /
`AddHeatRadius` / `AddHeatTotalInRadiusArea` for heat that enters from outside (booked as injected). Extraction
takes only heat above ambient and never from hot or frost rock. A tank exchanges with the grid in both directions
(`AddEnergyInRadiusArea` with a signed amount), so a cold tank warms by chilling the cells under it. `SetPixel` books the capacity change when a cell changes
material; `SetPixelRaw` is unmetered and only for level generation.

`ThermalLedger` keeps `Injected`, the `Sunk*` totals and `ConvertedToCharge` (heat the base engine turned into
charge, or charge spent back as heat). `World.SumSystemThermalEnergy` (grid, tanks, base reactors) plus
`World.SumSystemCharge` (base stores, batteries) changes by exactly `Injected - TotalSunk` each tick.

The mechanics built on this (base reservoir and radiator, relays pumping heat home, the base engine and charge,
pylons and batteries, network coverage, the tank's temperature band, hot and frost rock pockets, the sector goal and
upgrades) are specified in `DESIGN.md` §4, the ore economy in §6.4; the code is in `TankBase`, `BaseUpgrades`,
`TankBuildController`, `MachineBehaviors`,
`TankThermals`, `TankHeatModel`, `NetworkCoverage`, `HotRockPockets`, `FrostRockPockets` and `World.AdvanceSectorGoal`.
The heat render texture encodes temperature signed (`TerrainGrid.HeatToByte`), so the terrain shader can draw ground
below 0 as frost.

## Vision

`World.TrackVision(tank)` returns a `VisionMap` for that tank's side and keeps it up to date at the end of every
`Advance`. A world nobody asked pays nothing. The map is output only: nothing in the simulation reads it, so
tracking cannot change a result (`VisionMapTests.TrackingVision_DoesNotChangeTheSimulation`, and the recorded
fingerprints do not move).

A side sees through `VisionSource`s (numbers in `Tweaks.Vision`):

- its tank, along open lines of sight to `TankRadius`, and everything within `TankNearRadius`, through solid ground
  too, so digging is not blind;
- its base, along open lines of sight over its coverage circle (the radius of `NetworkCoverage`);
- its link nodes (relays, pylons), over their whole coverage circles, through solid ground too, linked or not;
- every other planted machine of its base, `MachineNearRadius` around itself.

A source casts a Bresenham ray to each cell on the edge of its square, cut to a circle. A ray stops at the first
cell that blocks a link (`Pixel.BlocksLink`, the rule links and coverage use). The walls around the open cells in
sight are then lit `WallSkin` cells deep, less with every cell of depth. Each cell gets a level, 255 in plain sight
and fading over the last `EdgeFade` cells of the source's reach (a short reach: its outer half), so sight has no hard
edge anywhere.

The map holds, per cell: the level (`LevelAt`, `IsVisible`), whether it was ever seen (`IsExplored`), and `Known`,
the pixel the side last saw there (an `ITerrainPixelReadOnly`; it starts as a copy of the terrain). After each
update `KnownChanges` lists the cells whose known pixel changed, either because the terrain changed in sight or
because a cell that changed out of sight came back into it. `TryGetDirtyRect` bounds the cells whose level changed.

Cost follows the cells in sight, never the size of the map: every pass walks lists of lit cells, and no pass sweeps
the grid. The tank is recast every tick; bases and machines, which stay put, every
`FixedSourceRefreshTicks` ticks into their own layer. Keep it that way: maps are meant to grow.

### Radar

`World.TrackRadar(tank)` returns that tank's `Radar` (and tracks its side's vision, which the radar needs). It is
output only, like vision (`RadarTests.AWorld_RunsTheRadar_WithoutChangingTheSimulation`). Numbers are in
`Tweaks.Radar`.

Every `PulsePeriodTicks` the radar sends a pulse from the tank's cell, which stays the pulse's origin however the
tank moves. The pulse is a fan of `RaysPerPulse` rays, turned a fraction of a ray spacing each pulse so later pulses
fill the gaps of earlier ones. Its front runs `FrontCellsPerTick` outward a tick to `Range`, and each ray walks only
the cells the front crossed that tick, so a return appears when the front reaches it, near ones first. One pulse is
in flight at a time; `PulseInFlight`, `PulseOrigin` and `PulseRadius` let Desktop draw its ring. A ray is a
Bresenham line that never cuts a corner: a diagonal step goes through the side cell the line crosses first, so it
cannot slip between two cells that touch only at a corner. Along a ray:

- dirt and open ground let it through, and so do the walls of the tank's own base;
- another side's machine (`RadarTarget`), or gold, reflects as `Metal` and stops it;
- another tank (a `RadarTarget` marked `Alive`) reflects as `Life` and stops it;
- rock, concrete, hot and frost rock and another side's base walls reflect as `Solid` and stop it;
- stepping from dirt into open ground (a tunnel, a cave) gives a `Cavity` return and the ray goes on, at most
  `WeakReturnsPerRay` times.

A ray stops at the first thing that reflects it whether or not the return is kept, so nothing behind it answers.

Every range below, and the heat budget, comes from the `RadarReach` the pulse was sent with: `Tweaks.Radar` scaled
by the tank's base's radar upgrade (`BaseUpgrades.RadarRangeScale`, `RadarHeatBudgetScale`), which World hands to
`Radar.Advance`. `Radar.PulseRange` is the reach of the pulse in flight, for drawing its ring. A pulse that runs
longer than `PulsePeriodTicks` delays the next one. Each kind of thing returns only from within its own range of the
pulse's origin (`Tweaks.Radar`): rock from
`SolidRangeMin` for a thin shell up to the full `Range` for a formation `FormationDepth` cells deep along the ray
(`Radar.SolidRange` looks behind the face, only for rock beyond the near range), gold from `GoldRange`, machines
from `MachineRange`, tanks from `LifeRange`, caves from `CavityRange`, heat from `HeatRangeMin` to `HeatRangeMax` by
intensity. Out of range the thing still stops the ray.

Heat weakens the rays. A ray can cross `HeatBudgetDegreeCells` in all: it starts at strength 1 and loses 1/budget for every degree a cell is above
`HeatThreshold`, scaled by the length of path a cell stands for along that ray (so the loss per distance is the same
in every direction), before anything in that cell can reflect it. What it then finds is recorded with the strength
left (`RadarContact.Strength`). Every hot cell the ray goes through gets a `Heat` contact whose strength is how hot
the cell is (1 at `HeatFullTemperature`), at most `HeatReturnsPerRay` a ray, and the ray stops when its strength runs
out: a few cells into very hot ground, many into warm ground. A reflector at or above `HeatScrambleTemperature`
returns `Heat` instead of what it is. So a hot zone shows as heat marks reaching a little way into it from the side
the pulse came from, and nothing behind them. A contact is removed as soon as the side sees its cell, and the side's
own machines are not radar targets: the radar tells a side only what it cannot see or does not remember. `Radar.HeatLoss` is the share of
the last finished pulse's rays that heat absorbed. Cold does nothing to the rays. This is the radar reading the heat
field, not changing it.

A return becomes a `RadarContact` (cell, echo, birth tick, strength) unless it tells the side nothing: the cell is in
sight, or it is terrain the side has explored and remembers as it is now. So remembered ground only shows where it
has changed. Contacts live at most `ContactLifetimeTicks` in a fixed ring (`ContactCount`, `ContactAt`), oldest
first, and never move. Each tick of a pulse, after the rays have walked, the radar reconciles the older contacts: a
new return in a cell replaces the old contact there, and a contact the front has just passed, on the nearest ray,
when that ray got at least that far, is checked against the world and dropped unless it still holds (the rock is
still there, the ground still hot, a target still covers the cell) and is still news. A contact in the shadow of
whatever its ray stopped at is not checked, so the radar never learns what it could not reach; it fades instead.
A tick of a pulse walks about `RaysPerPulse * FrontCellsPerTick` cells and looks at each contact once; ticks
between pulses only age contacts. Neither depends on the size of the map.

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
   `BuildTable(...)` call that fills `MachineBehaviors.All`: its type, action interval, action, colour, ore cost
   and `BuildKey`, plus the traits Core reads instead of the type: `BatteryCapacity`, `IsLinkNode`,
   `RequiresLinkToPlant`, `IsCharger`, `IsMiner`, `IsHeatPump`. The action is a static method in `MachineBehaviors` taking
   `(Machine, MachineContext)`.
3. Constants (interval, battery) in `Tweaks.Machine`, its ore cost in `Tweaks.Ore`, colour in `Tweaks.Colors`.
4. Each `BuildKey` builds exactly one machine, so a fourth machine needs a new key: a value in `BuildKey`, a field
   in `ControllerOutput`, and its edge detection in `TankBuildController.Advance`.
5. `NetworkStatus.Collect` counts harvesters and relays by type; extend it if the HUD should report the new one.
6. Update `MachineBehaviorTableTests` (it names each machine's key and traits).

Desktop:

7. A `MachinePresentation` in `MachinePresentations` (`Presentation/MachinePresentation.cs`): names, keyboard key,
   build-panel slot, build sound, plant hint, and its `MachineSprite` (`Rendering/Entities/MachineSprite.cs`): shader
   shape kind, body size, whether it turns to its heading or stands upright with a gauge, its sense ring, battery
   gauge and preview reach. `MachineQuads` and `PreviewQuads` draw from it; neither switches on the type.
   `MachinePresentationTests` checks keys and slots are distinct and fit the panel, and that every sprite is complete.
8. For a new `BuildKey`: `KeyboardController`, `GameSimulationStepper` (merges keyboard and scripted input) and
   `Game.ScriptedInput.cs` (scripted `Build` numbers).
9. A new shape (one no sprite uses yet) needs a `Kind*` constant in `EntityShape`, the matching `kKind*` in
   `Shaders/Entity/Layout.hlsli`, the shape function (e.g. `Shaders/Entity/Machines.hlsli`) and a branch in
   `EntityPs.hlsl`; `EntityLayoutGuardTests` checks the three agree.
10. `Gui/HintSelector.cs` names the machines in its hints; add one if the player needs telling.

### Adding a projectile

1. A value in `ProjectileType` (`Entities/Projectiles/Projectile.cs`) and a factory method on `Projectile`.
2. An entry in `ProjectileList.BuildBehaviors`: an advance method `(Projectile, CollisionSolver, ProjectileList)`
   and a draw colour. Use `CollisionSolver.TestPoint` to hit tanks, machines or terrain (bullets skip their own side's tanks and machines), `ProjectileList`'s RNG for chance,
   and the heat API (`AddHeat`, `AddHeatRadius`) for heat, so the ledger stays balanced.
3. Something has to spawn it: today only `TankTurret` (bullets) and `ExplosionFactory` (shrapnel) do.
4. Desktop: `Rendering/Entities/ProjectileQuads.cs` draws bullets and shrapnel as hot shots and throws for any
   other type; add a case for a new projectile. Record what presentation should hear about in `World.StepEvents` (bullets record `BulletImpact`, shrapnel `ShrapnelSpent`).

### Adding a thermal material

1. A value in `ThermalMaterial` (`Terrain/TerrainPixel.cs`), contiguous from zero.
2. Map pixels to it in `Pixel.DeriveThermalMaterial`. A new `TerrainPixel` also needs an entry in
   `Pixel.BuildBehaviorTable` and its name in the reviewed list of `SimulationTableGuardTests`.
3. Its row in `Tweaks.World.ThermalMaterials`, a `ThermalMaterialTable` with one `ThermalMaterialProperties` per
   material (the constructor throws on a missing or doubled row):
   - `HeatCapacity`: energy per degree.
   - `Conductivity`: the conductance of half a cell. Two touching cells conduct their halves in series,
     `ka * kb / (ka + kb)`, so a material against itself conducts half its value and against a much better
     conductor (a base, a gold core) nearly all of it. Air's value is the air field's, between neighbouring cells.
     A pair that needs a value the rule cannot produce gets a `ThermalPairOverride` in the table's override list
     (either order; today dirt against stone, hot rock, base and gold/core). The engine copies the pair table once
     at construction, so the solver's inner loop pays one array read.
   - `AirExchange`: conductance between the cell's terrain and the air in the same cell.
   - `FixedTemperature`: set it for a source or sink; `ResolveTerrainTemperature` pins the cell there each step.
   If entities must not draw heat from it, extend `CannotExtractFrom` in `TerrainGrid.Heat.cs`.
   The table reaches thermal code through `SimulationSettings.ThermalMaterials` (`SourceGuardTests`), so a test can
   swap a row with `ThermalMaterialTable.With`.
4. Desktop shades pixels, not materials: a new pixel needs a case in `TerrainAuxBuilder` and
   `TerrainMaterialCoverage`.

### Adding a collidable entity

`CollisionSolver.TestPoint` is the shared point query, but only bullets use it today. Tanks move against terrain
alone (`TankDigger`), machine placement checks `World.IsMachineSpotOccupied`, and roaming harvesters ask
`MachineContext.IsOccupied`. To make bullets (or a new projectile) hit a new entity:

1. A list for it reachable from `World`, and a parameter for it in `CollisionSolver.Update` (called first thing in
   `World.Advance`); add a sector index like the tanks' if there will be many.
2. An optional callback parameter on `TestPoint` for the new entity, checked in the same way as `onMachine`.
3. Pass the callback where hits should land, e.g. `AdvanceBullet` in `ProjectileList`.
