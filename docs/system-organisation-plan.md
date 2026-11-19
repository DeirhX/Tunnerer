# Organising the code by game system

The aim is that a game system can be added, switched off for a match, and later removed without touching code that
belongs to other systems. This file records what has been done towards that and the steps still planned, in the
order I would take them. Each step should keep simulation results bit-for-bit (`SimulationFingerprintTests`) unless
it says otherwise.

## Done

| Change | Where |
|---|---|
| `MatchRules`: one switch for a match's optional systems, part of the hash clients compare before a network match | DeirhX/Emberward#40 |
| The sector goal and the match result out of `World` (`Match/`) | DeirhX/Emberward#40 |
| `World.Advance` runs an ordered list of named systems; a system the world does not have is not in the list | DeirhX/Emberward#42 |
| Core folders by system: `Bots/`, `Network/`, `Regrowth/`, all tank code in `Entities/Tanks/` | DeirhX/Emberward#42 |
| The core tap's per-base state kept by `SectorGoal`; Desktop asks `World.CoreTapOf` | DeirhX/Emberward#42 |
| `Entities/Bases/`, `MachineMaterializer` in `Machines/`, `Resources.cs` split | DeirhX/Emberward#43 |
| The unused sprite path and the composite pixel buffer deleted | DeirhX/Emberward#43 |
| Gold retired; "Removing a system" checklist in `AGENTS.md` | the gold PR |

The PRs are stacked, each on the one before it. Merge them in order and retarget the next one to `master` after each
merge, before starting new work on top.

## Next steps

### 1. One watcher per system in the guidance

`Gui/Guidance/GameEventWatcher.cs` keeps a flag for every system (relays, harvesters, stores, charge, coverage,
radar, ore, upgrades, the core) and every new system edits it. Split it into small watchers, one per system, each
with its own state and messages, behind one interface that the feed calls. `TutorialDirector` (one list of steps for
every system), `HintSelector` and the `StepEventSounds` switch have the same shape and can follow the same way.

Done when adding or removing a system's notifications touches only that system's watcher and the list of watchers.

### 2. Vision and radar tracking out of `World`

`World` still holds `TrackVision`, `TrackRadar`, `RadarOf` and the per-step update of each side's sight (about 100
lines). Move them to a `Sensing` class that `World` owns and runs as the `vision` entry of `BuildSystems`, the way
the sector goal moved to `Match/`. `World` keeps thin queries for Desktop.

### 3. Split `Tweaks.World`

It mixes regrowth, the link map, the thermal solver's tiles and hot and frost pocket placement. Give each its own
section (`Regrowth`, `Links`, `Thermal`, `Pockets`) so a system's constants live and die with it. Thermal code reads
through `SimulationSettings`, so `SimulationSettings.FromTweaks` changes with it.

### 4. Ore and pockets out of `TerrainGrid`

`TerrainGrid.Ore.cs` (placing ore, `FindOre` and the per-tile ore counts behind it) and `TerrainGrid.HotRock.cs`
(placing pockets, pinned pixels) are another system's code inside the grid's partial class. Move each into a class
of its own that the grid or `World` holds, so that `MatchRules.SeedOre` and the pocket counts switch off the code
and not just its use.

### 5. The remaining per-base and per-tank state

`TankBase` and `Tank` still carry state for several systems: network coverage and the off-network floor (in
`TankThermals`), the ore hold and the base store, and the base's radiator, engine and charge. Move them one system at
a time into the system that owns the rule, with a `World` query for Desktop, as `SectorGoal` now does with
`CoreTap`. Start with coverage, which has the clearest owner (`Network/`), then ore.

### 6. Tests by system

`tests/Emberward.Tests` has about 90 files in one folder. Group them into folders named after the systems
(`Thermal/`, `Network/`, `Sensing/`, `Match/`, ...) so that removing a system mostly removes a folder.

### Smaller items

- `TerrainPs.hlsl` still tests for entity-marked pixels (alpha below 0.999). Nothing marks pixels since the composite
  buffer went; check whether anything else lowers the alpha and delete the branch if not.
- Match recordings do not store `MatchRules`; a replay assumes the network-match rules. Write the rules into the
  recording header when a second set of rules is in use.
- `SimulationContext` has optional delegates where null means a rule is off (coverage, building). Tests build tanks
  without a world through them; replace the null-means-off cases with explicit test doubles if they get in the way.
- `Core/Rendering/TankSprites.cs` holds both the tank's shape (gameplay: collision, digging) and its palettes
  (presentation). Keep the shape in Core and move the palettes to Desktop.
