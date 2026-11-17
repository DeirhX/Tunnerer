# Tunnerer.Core: the simulation

Core is the whole game without a window: terrain, heat, tanks, machines, the link network, projectiles, level
generation and the bot. It takes one `ControllerOutput` per tank per tick and exposes its state as plain data. It
references no SDL, ImGui or GPU code (`ArchitectureGuardTests`). The rules the tests enforce (determinism, energy
conservation, one heat solver) are listed in `AGENTS.md`; this file says where things are and how they fit.

## Module map

```
World.cs                     World: owns every subsystem, runs Advance over its list of systems (WorldSystem)
SimulationProfile.cs         Per-system timings, one section per system label
StepEvents.cs                StepEvent / StepEventLog: what happened during a step (World.StepEvents)
WorldStateHash.cs            One number for a world's state (determinism tests), and SyncDigest, which adds heat and
                             charge totals (what machines in a network match compare)
StatusLevel.cs               Ok / Warn / Bad for a HUD gauge (Tank.ShieldLevel, TankBase.ChargeLevel, ...)
Match/
  MatchRules.cs              Which optional systems a world runs (regrowth, pockets, ore, the core race); World.Rules
  MatchOutcome.cs            MatchReferee: Victory / Defeat / InProgress for one tank (World.MatchOutcomeFor)
  MatchResult.cs             Latches the referee's first answer (World.MatchWinner, World.IsMatchOver)
  SectorGoal.cs              The core tap: each base's CoreTap (tap, hold timer, tapped), the first base to tap the core
Config/
  Tweaks.cs                  Every gameplay and simulation constant (const / static readonly only)
  SimulationSettings.cs      The thermal and tank-heat constants, copied from Tweaks; tests inject their own
  FastRandom.cs              Small seeded RNG used by the simulation
Terrain/
  TerrainPixel.cs            TerrainPixel, ThermalMaterial, TerrainBehavior and the Pixel lookup tables
  TerrainGrid.cs             Pixel array, change list, SetPixel (metered) and SetPixelRaw (level gen only)
  TerrainGrid.Heat.cs        Terrain and air temperature fields, CoolDown, energy add / extract API
  TerrainGrid.HeatTiles.cs   The solver step over the tiles that are still changing
  TerrainGrid.HotRock.cs     PlaceHotRockPocket, PlaceFrostRockPocket, SetPinnedPixel
  TerrainGrid.Ore.cs         PlaceOre (level gen); FindOre and the per-tile ore counts behind it
  TerrainGrid.Materialization.cs, .Decoration.cs, .Render.cs   Level-gen markers to pixels, cleanup, Draw*ToSurface
  SightlineChanges.cs        The cells that started or stopped blocking a line of sight, for the link map
  ITerrain*.cs               Narrow interfaces over TerrainGrid (pixel read / write, heat source, change tracker)
Regrowth/                    (only in a world whose MatchRules.TerrainRegrowth is on)
  TerrainRegrowth.cs         Dirt regrowth: the rules, applied to each cell when its appointment comes
  RegrowthSchedule.cs        The appointments: at most one per cell, filed by tick
  RegrowthWrites.cs          The pixel writes regrowth has yet to hear of (TerrainGrid lists them)
  RegrowthBlockers.cs        Where no dirt grows (relay and pylon range), filed by map area
Thermal/
  TerrainHeatEngine.cs       The exchange stencil, capacity, conductance and loss tables, ResolveCell
  ThermalScheduler.cs        Per tick: step or not (nothing active, or throttled when nearly everything is)
  ThermalActivityTracker.cs  Active tiles (which tiles to step) and the heat dirty rect (for the renderer)
  ThermalLedger.cs           Energy created or destroyed, by kind
  TankHeatEngine.cs          One tank's heat exchange with terrain and air
Entities/
  Tanks/Tank.cs              Tank state and the per-tick order (below); delegates to the classes beside it
  Tanks/TankList.cs          Tank collection
  Tanks/SimulationContext.cs The slice of World a tank sees during Advance
  Tanks/TankTurret.cs        Aim, cooldown, bullet spawn
  Tanks/TankHeatModel.cs     Heat-flow formula shared by tank contact with terrain and air
  Tanks/TankThermals.cs      Tank heat, network coverage floor, base sink, refrigeration, ledger bookings
  Tanks/TankDigger.cs        Movement, terrain contact, dig / torch, mining ore into the hold; owns the
                             tank's RNG
  Tanks/TankBuildController.cs   Build keys: build, carry, plant; the payer rule (Quote)
  Tanks/BuildQuote.cs        BuildQuote, OrePayer, BuildRefusal: what a machine costs here and which stock pays
  Tanks/TankLifecycle.cs     Damage, death (the hold spills as ore, OreDrop), respawn, spawn shield
  Tanks/TankSpriteShape.cs   The tank's one shape (its sprite's cells): terrain contact, bullet hits, planting
  Bases/TankBase.cs, TankBases.cs   Bases (reactor, radiator, engine, charge, ore store)
  Bases/BaseUpgrades.cs      BaseUpgrade (base track and tank track), levels, costs and what each level does
  DamageCause.cs
  Machines/Machine.cs        MachineType, MachineState, Machine
  Machines/MachineBehavior.cs   MachineBehavior (action, interval, cost, BuildKey, traits), MachineBehaviors table
  Machines/HarvesterPathing.cs  Where a harvester can stand; the flood that finds the ore nearest by path
  Machines/MachineList.cs    Machine collection, filed by place (`CollectNear`); wrecks dead machines (leave the network, charge to heat, scorch)
  Machines/MachinePlacementPreview.cs   Result of World.PreviewPlacement
  Machines/MachineMaterializer.cs   The machine a tank carries before it is planted
  Projectiles/Projectile.cs  ProjectileType, Projectile and its factories
  Projectiles/ProjectileList.cs   ProjectileBehavior table, advance, explosions; owns the projectile RNG
  Projectiles/ExplosionDesc.cs    ExplosionFactory (shrapnel fans)
Network/                     The link network (not network matches: those are Desktop/Net and Tunnerer.Protocol)
  LinkPoint.cs, Link.cs, LinkMap.cs   Nodes, edges, refresh, power and flow
  NetworkCoverage.cs         Coverage nodes and the coverage a tank gets at a position
  NetworkStatus.cs           One base's harvesters, relays and heat store, for the HUD (World.GetNetworkStatus)
Sensing/
  VisionMap.cs               Fog of war for one side: visibility levels, explored cells, the terrain as last seen
  VisionEye.cs               One source's sight, kept between updates, and the ray and wall casting that fills it
  Radar.cs                   A tank's radar: pulses running out from the tank and the fading contacts where they reflected
  RadarContact.cs            What a radar return is: RadarEcho, RadarContact, RadarTarget
  RadarReach.cs              The radar's ranges and heat budget, scaled per tank by its radar upgrade
Collision/
  CollisionSolver.cs         Point query against tanks, machines and terrain (visitor callbacks); what stops a tank's hull
  WorldSectors.cs            Sector buckets for the tank broad phase
  Raycaster.cs               Bresenham lines; HasOpenLine (the one line-of-sight test)
Input/
  Controller.cs              ControllerOutput
  ControllerOutputCodec.cs   ControllerOutput as 28 bits (network, input logs); quantises the aim
Bots/                        The computer player: produces a ControllerOutput like a person would
  BotTankAI.cs               The bot's mode machine (TwitchMode)
  BotNavigator.cs, BotGunner.cs, BotBehaviorProfile.cs   Bot movement, fire decisions, play styles
  BotTrail.cs                The way the bot came, retraced to get home
LevelGen/
  ToastGenerator.cs          Cavern generator (LevelGenMode Deterministic or Optimized)
  GeneratorUtils.cs          Shared drawing helpers
  HotRockPockets.cs          Scatters hot rock pockets
  FrostRockPockets.cs        Scatters frost pockets
  OreDeposits.cs             Seeds emberite: specks, pockets and lumps with a warm core (OreDeposit, OreDepositKind)
  TerrainTestPatternGenerator.cs   Synthetic terrain for the renderer's --terrain-test mode
Rendering/
  RenderSnapshots.cs         TankRenderState, ProjectileRenderState, MachineRenderState, LinkRenderState
  TankSprites.cs             Tank bitmaps and colour palettes
Resources/Reactor.cs         Reactor, ReactorState, Heat, Health: a tank's or base's heat and shield
Resources/OreStore.cs        OreStore: a tank's hold, a base's store (ore is the only currency)
Resources/OreDrop.cs         Puts ore back into the ground, nearest free cells first, not under tanks or machines (a dead tank's hold)
Types/Position.cs, RectMath.cs   Position, Offset, Size, PositionF, VectorF, DirectionF, Color, Rect, BoundingBox
```

## World

`new World(size, simulationSeed, rules)` creates the subsystems. `MatchRules` says which optional systems the world
runs: regrowth, hot and frost rock pockets, ore, and whether tapping the core wins the match. It is the one switch for
them (a system that can be turned off gets a flag there), and it is mixed into the hash clients compare before a
network match starts (`WorldStateHash.ComputeReady`). `World.Initialize(terrain, spawns, ...)` copies in a generated
level, materializes it, builds the bases, places hot and frost rock pockets (if the rules ask; the hot ones also give
the core, `World.CorePocket`, and with it the `SectorGoal`), seeds emberite ore (if the rules ask, `SeedOre`;
`World.OreDeposits`) and adds one tank per spawn. Ore is placed last and grows each deposit cell by cell from its
start, so generation never sweeps the grid; deposit counts scale with map area (`Tweaks.Ore`), and on a small map the
distance rules shrink and a floor of pockets and lumps applies (`OreDeposits.DistanceScale`). Every RNG World owns
(projectiles, link map, regrowth, each tank) is seeded from `simulationSeed`, and decorations and rock pockets from
the decoration seed (or, without one, from the spawns and
`simulationSeed`). Core never reads the clock or unseeded randomness; for a random match Desktop's `WorldFactory`
picks the seed. The bot takes its own seed, also from `WorldFactory`.

`World.Advance(getInput)` runs one fixed step: it goes through the world's systems, a list of `WorldSystem` (a
profile label and what the system does each step) that `World.BuildSystems` puts together once, when the world is
built and again when it is initialised. A system the world does not have is not in the list and costs nothing:
regrowth only when `MatchRules.TerrainRegrowth` is on, the sector goal only when the world has a core. A new system
is a new entry, in the place its order needs; the profile picks up its label. The list, by label:

1. **coll**: `CollisionSolver.Update` rebuilds the tank sector index.
2. **heat**: `TerrainGrid.CoolDown` (see below), and the solver's own parts for the profile.
3. **regrow**: `TerrainRegrowth.Advance`. No new dirt grows within the coverage radius of a planted relay or
   pylon (`RegrowthBlocker`), linked or not, so the ground a network runs through stays open. Nothing scans the
   map: each cell that can change (open ground with more than two dirt neighbours, scorch, dirt still growing) has
   an appointment in `RegrowthSchedule` at the tick it is next looked at, with the wait drawn so that the odds are
   those of rolling on every pass. Pixel writes (`RegrowthWrites`) start new appointments, and so does a blocker
   going away. Open ground away from dirt or inside a blocker has none and costs nothing. The map is cut into
   regions of `RegrowthRegionSize` cells that each take their pass on a tick of their own, so a large map's
   regrowth is spread over the ticks between passes.
4. **proj**: `ProjectileList.Advance`.
5. **tanks**: `LinkMap.AgeOreFlow`, then `TankList.Advance` with the world's `SimulationContext` (built once). Per tank (`Tank.Advance`):
   idle heat, base interaction (recharge, heat sink, docking: the hold banks into the store), network coverage, the
   network unload (inside linked coverage the hold moves `Ore.NetworkUnloadPerSecond` into the base store, booked on
   the links with `LinkMap.CarryOre` and recorded as `StepEventKind.OreUnloaded`), turret aim and fire,
   `TankDigger.Move`, spawn shield, shooting, `TankBuildController.Advance`, the upgrade keys, then
   `TankThermals.Advance` (exchange with the grid).
6. **machines**: `MachineList.Advance` with the world's `MachineContext` (a harvester asks `MachineContext.IsOnNetwork`, the coverage of its base's
   network at its position, before it works; it mines plain ore only and leaves lump cores to the tank's torch,
   idling as `HarvesterState.CoreOnly` when nothing else is left), then each pylon's last hand-out is booked on its route
   (`LinkMap.CarryCharge`). A machine that needs the machines near a place (a pylon its batteries, a harvester the
   nodes that cover it, anything asking whether a spot is taken) asks `MachineList.CollectNear`, which returns them
   in list order, and never walks the whole list. A relay that found no heat to pump keeps the ground's
   `QuietHeatStamp` and skips its sweep until that number changes, which it does when heat is written or stepped
   in its reach.
7. **bases**: `TankBases.Advance` (reactor sink, radiator, engine counters), then the bot's stand-in ore income for
   bases marked with `World.SetBotControlled`.
8. **goal**: `SectorGoal.Advance`, the core tap and its hold timer (only in a world with a core).
9. **match**: `MatchResult.Latch`, the result, fixed once the referee has one.
10. **links**: `LinkMap.Advance` brings the network up to date every `Tweaks.World.RefreshLinkMapInterval`
   (200 ms), not every tick, and at once when a machine's point is switched off.
11. **dock**: each tank notes whether it has left its base (`Tank.HasLeftBase`) and records docking (`Tank.ObserveDocking`).
12. **vision**: each `VisionMap` handed out by `World.TrackVision` is brought up to date and each `Radar` handed
    out by `World.TrackRadar` runs its pulse on (see "Radar" below).

`SimulationProfile` reports averages every 100 ticks when `ReportSink` is set: `[Profile]` (one time per system,
under its label), `[Profile+]` (heat solver parts) and `[Profile++]` (active and stepped tiles, stepped / parallel ticks).
With `Profile.AuditEnergy` (Desktop: `--energy-audit`) it adds `[ProfileE]`: the thermal energy and the ledger
residual over the report window, which should stay near float noise. That line sums every cell of the map, so it
is off by default and never runs per tick.

Queries Desktop asks instead of computing itself: `World.MatchOutcomeFor` / `IsMatchOver`,
`World.CoreTapOf` (a base's progress on the sector goal),
`Tank.IsDockedAtOwnBase`, `Tank.IsInOwnNetwork`, `Tank.IsUnloadingOre`, `Tank.QuoteBuild` (cost, payer, stock and
refusal for a machine), `Tank.LastBuildRefusal`, `World.GetNetworkStatus`, `World.PreviewPlacement`, `World.CoverageFor`,
`World.CollectCoverageNodes`, `World.TrackVision`, `World.TrackRadar`, `World.RadarOf`, and the `CopyRenderStates` snapshot of each entity list.

### Step events

Desktop learns about one-off happenings from `World.StepEvents`, never by comparing state between steps. Each
`Advance` clears the list first, then the code where something happens records a small `StepEvent` (kind, position,
the tank or side's colour, and a machine type, impact target, cell count or torch flag where they apply): shots
fired, bullet impacts, explosions, spent shrapnel, tank hits, deaths and respawns, moving, digging, mining ore into the hold,
docking and leaving the base, base service, ore unloaded by a tank or banked by a harvester, and machines built,
planted, refused, damaged and wrecked. The list
holds the last step's events until the next step begins, so a caller that runs several steps per frame reads it
after each one. It is output only: nothing in the simulation reads it, so recording cannot change a result
(`SimulationFingerprintTests`), and the `StepEventLog` reuses its storage, so recording does not allocate in steady
state. Tanks record through `SimulationContext.Events`, machines through `MachineContext.Events`, projectiles through the log `ProjectileList` is built with,
and `World` itself records plantings and docking. A new kind of happening that presentation needs is a new
`StepEventKind`, recorded where it happens (`StepEventTests` covers each kind).

## Heat

Heat lives in two float fields on `TerrainGrid`, terrain and air. Energy is temperature times heat capacity
(`TerrainHeatEngine.GetHeatCapacity` per `ThermalMaterial`, `AirHeatCapacity` for air). In an air cell the
terrain field only mirrors the air field, so such a cell holds one store.

The exchange is `TerrainHeatEngine.ComputeRegionDeltaConservative`: a cell's terrain trades heat with its own air
and with the solid cells beside it, and its air with the air beside it, but only where at least one of the two
cells is open. The air inside solid ground carries nothing from one solid cell to the next, so ground conducts as
its material row says; dirt is the insulator (30 times worse than stone). `ResolveCell` then applies the step to a
cell: it pins fixed-temperature materials (base cells, `ConstantEnergy`, hot rock, frost rock, emberite cores) and books what the
pin removes or adds as `SunkFixedTemperature`, and it takes each material's `Loss` from unpinned ground and from
open air, a small share of the temperature above or below ambient, booked as `SunkAmbientLoss`. The loss stands for
heat leaving to the deep ground and to the surface. It is what gives a hot or frost pocket a halo of a fixed size
(about a dozen cells in dirt) instead of one that spreads for ever, and open air loses faster than ground, so a
dug-out chamber is a radiator for whatever stands in it.

Because of the loss, heat settles, and the solver only works where it has not. `ThermalActivityTracker` keeps a
grid of tiles (`ThermalActiveTileSize` cells a side). A tile is active when the last step still changed a cell in it
(`ThermalSettledGrowStep` away from ambient, `ThermalSettledFadeStep` back toward it) or when something wrote heat
into it or changed a cell's material next to heat (`SetPixel`). Each tick `TerrainGrid.CoolDown` steps the active
tiles and the ring of tiles around them (`TerrainGrid.HeatTiles.cs`), one tile per thread when there are many:
every tile's deltas are computed from the same state, tiles trade across the sides they share (each writes its own
half of the trade, `RegionEdges`), and what the tiles found is merged in tile order, so the result is the same on
any number of threads. A stepped tile's side toward a tile left out is closed, so before the step the set is grown
across every side that still carries heat (`EdgeCarriesHeat`): a settled pocket and its halo are stepped together
or not at all, never cut apart. Being stepped along does not make a settled tile active. A change of material
wakes its tile only where a cell involved is more than `ThermalWakeTemperature` from ambient. A settled pocket, old tunnel or untouched ground costs nothing until it is disturbed. With no
active tile the solver does not run; `ThermalScheduler` also holds it to every few ticks when nearly the whole map
is active.

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
`TankThermals`, `TankHeatModel`, `NetworkCoverage`, `HotRockPockets`, `FrostRockPockets` and `SectorGoal`.
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

Cost follows what changes, never the size of the map or the number of cells in sight. Each source keeps its own
cast (a `VisionEye`: the level it gives every cell of the square it can reach), and a cell's level is the highest
any source gives it. `World` lists every source each tick, which costs next to nothing, and the map does work only
for:

- a source that is new or has moved (a source is its position and radii: the tank when it drives, a harvester when
  it crawls), which is cast, and one that is gone, whose cells fall back to what the other sources give them;
- ground that blocks sight where it did not, or the reverse, inside a source's square. A tank or base, which see
  along open lines, are cast again. A machine sees through the ground, so its rays light the same cells whatever
  the terrain is and only the walls lit around open ground can differ: it repairs the changed cells and
  `WallSkin` + 1 cells around them (`VisionCast.CastWindow`);
- a changed cell that is in sight, whose `Known` pixel is brought up to date.

A pixel change that blocks sight no differently (a scorch mark on open ground, regrowing dirt that settles) casts
nothing. The map learns of changes from the terrain's change list (`ITerrainChangeTracker`), which `World` keeps to
one step's worth; a cell written without `SetPixel` during play is not noticed, by vision or by the renderer.
`VisionMap.CellsCastInLastUpdate` is the measure of the work, and it is zero while nothing moves and nothing is dug.
`VisionMapTests.AMapKeptUpToDate_ShowsWhatAFreshLookShows` checks the result against a map that looks at the same
world for the first time. Keep it that way: maps are meant to grow, and so are networks of machines.

### Radar

`World.TrackRadar(tank)` returns that tank's `Radar` (and tracks its side's vision, which the radar needs). It is
output only, like vision (`RadarTests.AWorld_RunsTheRadar_WithoutChangingTheSimulation`). Numbers are in
`Tweaks.Radar`.

Every `PulsePeriodTicks` the radar sends a pulse from the tank's cell, which stays the pulse's origin however the
tank moves. The pulse is a fan of `RaysPerPulse` rays, turned a fraction of a ray spacing each pulse so later pulses
fill the gaps of earlier ones. Its front runs `FrontCellsPerTick` outward a tick to `Range` (both scaled by the radar
upgrade, so a pulse always takes the same time to reach full range; `PulseFront` keeps the fraction of a cell a
scaled front carries), and each ray walks only
the cells the front crossed that tick, so a return appears when the front reaches it, near ones first. One pulse is
in flight at a time; `PulseInFlight`, `PulseOrigin` and `PulseRadius` let Desktop draw its ring. A ray is a
Bresenham line that never cuts a corner: a diagonal step goes through the side cell the line crosses first, so it
cannot slip between two cells that touch only at a corner. Along a ray:

- dirt and open ground let it through, and so do the walls of the tank's own base;
- another side's machine (`RadarTarget`) reflects as `Metal` and stops it;
- another tank (a `RadarTarget` marked `Alive`) reflects as `Life` and stops it;
- rock, concrete, hot and frost rock and another side's base walls reflect as `Solid` and stop it;
- emberite (plain or core) reflects as `Ore` and stops it;
- stepping from dirt into open ground (a tunnel, a cave) gives a `Cavity` return and the ray goes on, at most
  `WeakReturnsPerRay` times.

A ray stops at the first thing that reflects it whether or not the return is kept, so nothing behind it answers.

Every range below, and the heat budget, comes from the `RadarReach` the pulse was sent with: `Tweaks.Radar` scaled
by the tank's base's radar upgrade (`BaseUpgrades.RadarRangeScale`, `RadarHeatBudgetScale`), which World hands to
`Radar.Advance`. `Radar.PulseRange` is the reach of the pulse in flight, for drawing its ring. A pulse that runs
longer than `PulsePeriodTicks` delays the next one. Each kind of thing returns only from within its own range of the
pulse's origin (`Tweaks.Radar`): rock from
`SolidRangeMin` for a thin shell up to the full `Range` for a formation `FormationDepth` cells deep along the ray
(`Radar.FormationRange` looks behind the face, only beyond the near range), ore from `OreRangeMin` for a speck up to
`OreRangeMax` for a deposit `FormationDepth` cells deep (the same probe, counting only plain ore no warmer than
`HeatThreshold`, so a lump's warm heart lends its rim no reach), machines
from `MachineRange`, tanks from `LifeRange`, caves from `CavityRange`, heat from `HeatRangeMin` to `HeatRangeMax` by
intensity. Out of range the thing still stops the ray.

Heat weakens the rays. A ray can cross `HeatBudgetDegreeCells` in all: it starts at strength 1 and loses 1/budget for every degree a cell is above
`HeatThreshold`, scaled by the length of path a cell stands for along that ray (so the loss per distance is the same
in every direction), before anything in that cell can reflect it. What it then finds is recorded with the strength
left (`RadarContact.Strength`). Every hot cell the ray goes through gets a `Heat` contact whose strength is how hot
the cell is (1 at `HeatFullTemperature`), at most `HeatReturnsPerRay` a ray, and the ray stops when its strength runs
out: a few cells into very hot ground, many into warm ground. A reflector at or above `HeatScrambleTemperature`
returns `Heat` instead of what it is; ore included, so a lump, whose core is pinned warm and heats its shell past
that, shows as heat and its contents are found by going there. So a hot zone shows as heat marks reaching a little way into it from the side
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
still there, the ore not yet mined, the ground still hot, a target still covers the cell) and is still news. A contact in the shadow of
whatever its ray stopped at is not checked, so the radar never learns what it could not reach; it fades instead.
A tick of a pulse walks about `RaysPerPulse * FrontCellsPerTick` cells and looks at each contact once; ticks
between pulses only age contacts. Neither depends on the size of the map.

## Links

Planted machines whose behavior has `IsLinkNode` (relays, pylons) and every base are `LinkPoint`s.
`LinkMap` pairs points within `MaximumTheoreticalLinkDistance`, marks a link live when it is short enough and
`Raycaster.HasOpenLine` finds no blocking pixel (`Pixel.BlocksLink`), and powers points breadth first from the
bases, recording each point's `UpstreamLink` and `Hops`. It keeps its links between refreshes and does only what
changed: a new point gets its links, a point switched off loses them, and a link's line of sight is cast again
only when a cell in a tile its line crosses started or stopped blocking (the terrain lists those cells in
`SightlineChanges`, from every pixel write). Power is searched again only when one of those changed something.
The result is the one a rebuild from nothing gives, in the same order (`LinkMapTests`). Relays book pumped heat and pylons book charge along that
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

The bot drives its tank through the same `ControllerOutput` as a player, and asks Core what a player sees:

- **What is in the way.** `World.ProbeTankStep` gives the answer the tank's own move gets (`None`, `Dirt`, `Hard`,
  `Solid` or `Entity`), and `CollisionSolver.SurveyTankTerrain` how much of the hull would stand in the open. It
  never reads single cells, because the hull is 7 cells wide.
- **Scoring a move.** Open ground beats dirt in proportion to the open share of the hull; a step toward the target
  earns the profile's `TargetPull` times the cosine to it, the same at any range; hot ground ahead
  (`World.GroundHeatAt`) costs a point a degree; rock, walls and other bodies are not tried. Roaming near home it
  does not head back toward its base.
- **The torch.** Dirt is dug the plain way (half speed, no heat) unless the profile hurries and the heat budget is
  above three quarters. Rock is torched only by committed moves (leaving the base, heading home, a breakout) and
  only above half the budget, because torched rock leaves ground that cooks the tank.
- **Stuck and going nowhere.** Standing still counts as stuck after the patience the obstacle warrants (rock being
  torched takes a while). Stuck, or four seconds without coming nearer its target, it commits to a short manoeuvre
  (a sidestep, or a held heading toward the target) instead of choosing a new move every tick. A breakout (a held
  heading) ends when the enemy comes into sight, and a heading is only chosen when one is open: walled in on every
  side, `ChooseBreakoutHeading` says so (no movement) and the bot goes on with its plan. A roaming plan made in the
  enemy's sight is also dropped when sight is lost, and one made out of sight when it is found, so the turret is
  never left aimed for the other case.
- **The way home.** `BotTrail` keeps the way it came as waypoints (loops dropped); heading home it retraces them,
  so it needs no torch when it is hot. A return that gets no nearer the base for 20 s (a sealed door, a dead-end
  trail) gives up and roams again, and for the next 30 s low health or heat does not send it home (a kill, or
  standing in its own base, still does); a slow tank on a long way home keeps getting nearer and never gives up.

It still has no route planner (moves are scored one step at a time), sees the whole map and always knows where the
enemy is.

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
     conductor (a base core) nearly all of it. Air's value is the air field's, between neighbouring cells.
     A pair that needs a value the rule cannot produce gets a `ThermalPairOverride` in the table's override list
     (either order; today only pairs of pinned sinks). The engine copies the pair table once
     at construction, so the solver's inner loop pays one array read.
   - `AirExchange`: conductance between the cell's terrain and the air in the same cell.
   - `FixedTemperature`: set it for a source or sink; `ResolveCell` pins the cell there each step.
   - `Loss`: for a material that stores heat, the share of its temperature above ambient it gives up to the outside
     each tick. Leave it zero only for a pinned material; ground without a loss never settles.
   If entities must not draw heat from it, extend `CannotExtractFrom` in `TerrainGrid.Heat.cs`.
   The table reaches thermal code through `SimulationSettings.ThermalMaterials` (`SourceGuardTests`), so a test can
   swap a row with `ThermalMaterialTable.With`.
4. Desktop shades pixels, not materials: a new pixel needs a case in `TerrainAuxBuilder` and
   `TerrainMaterialCoverage`.

### Adding a collidable entity

`CollisionSolver.TestPoint` is the shared point query; only bullets use it today. A tank's step is stopped by the
ground under its hull (`CollisionSolver.TestTankTerrain`) and by `CollisionSolver.BlocksTank`: any other live tank,
and planted machines of another side (found with `MachineList.CollectNear`, the machines near the hull, not
the whole list). A tank's own machines never block it (its own bullets pass them too), and
something it already overlaps where it stands holds it only for a step that brings it nearer the other body's
centre, so two overlapping bodies can always part and never pass through one another.
`TankDigger.Move` asks both through `SimulationContext`; `World.ProbeTankStep` gives the same answer for a step not
yet taken. Machine placement checks `World.IsMachineSpotOccupied`, and roaming harvesters ask
`MachineContext.IsOccupied`. To make bullets (or a new projectile) hit a new entity:

1. A list for it reachable from `World`, and a parameter for it in `CollisionSolver.Update` (called first thing in
   `World.Advance`); add a sector index like the tanks' if there will be many.
2. An optional callback parameter on `TestPoint` for the new entity, checked in the same way as `onMachine`.
3. Pass the callback where hits should land, e.g. `AdvanceBullet` in `ProjectileList`.
4. If it should stop tanks, a loop for it in `CollisionSolver.BlocksTank` (a near-place query, not a walk over every
   one), with the same "already overlapping" exception (`Parts`: a step that does not bring the tank nearer is free).
