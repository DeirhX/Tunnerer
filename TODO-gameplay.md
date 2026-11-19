# TODO: finish the finite-heat plan

Status at commit `a6e43f36` (branch `codex/tank-cargo-heat`, PR #72):

- **Done:** plain ore and lump cores are finite. Plain ore is seeded at 9 °C (`Ore.PlainStartTemperature`) and
  cools like ground. Lump cores use the `EmberiteCore` material (heat capacity 16 per degree, starting at 600 °C,
  `Ore.CoreStartTemperature`). Seeded heat goes through `SeedHeatedPixel` and is booked in `ThermalLedger`.
  Every ore cell glows, scaled by its temperature.
- **Not done:** hot rock, frost rock and the base sinks are still pinned (`FixedTemperature` in
  `Tweaks.World.ThermalMaterials`). Removing those pins is the job of this file.

Design sources: `DESIGN.md` §4.1 ("Every body is finite"), §4.3 (leaks, pinned ring, outside loss), §4.4, §4.7 (frost
pockets), §4.8 (sector goal), §4.10 (challenges), §6.3 (emberite), §11 (code-state note), §13 (decision log), §14
(open questions). Code notes: `src/Emberward.Core/ARCHITECTURE.md` (thermal section near lines 210-230, the seeding note
near 354, the pair table and loss rules near 480-495). Rules: `AGENTS.md`.

## 1. Where the pins are

| Item | Location | Today | Role |
|---|---|---|---|
| Base row | `src/Emberward.Core/Config/Tweaks.cs:70` | `FixedTemperature: 0f` | the base interior is a pinned sink |
| ConstantEnergy row (base core) | `Tweaks.cs:71` | `FixedTemperature: 0f` | `TerrainPixel.BaseCore` maps here (`TerrainPixel.cs:205`) |
| HotRock row | `Tweaks.cs:72` | `FixedTemperature: 100f` | hot pockets are permanent sources |
| FrostRock row | `Tweaks.cs:75` | `FixedTemperature: -60f` | frost pockets are permanent sinks |
| Pinned pair overrides | `Tweaks.cs:84-86` | base-base 0.2, constant-constant 0.3, base-constant 0.45 | conductances between pinned sinks; the rule derives all others |
| Base temperature readers | `src/Emberward.Core/Entities/Bases/TankBase.cs:42`, `src/Emberward.Core/Entities/Tanks/TankThermals.cs:30` | read Base `FixedTemperature` | the efficiency and docking reference use 0 |
| Pinned placement | `src/Emberward.Core/Terrain/TerrainGrid.HotRock.cs:15,23,50,62-64` | `PlacePinnedPocket` → `SetPinnedPixel` → `SeedHeatedPixel(GetFixedTemperature)` | hot and frost pockets |
| Pocket generators | `src/Emberward.Core/LevelGen/HotRockPockets.cs`, `FrostRockPockets.cs` | scatter the pockets | |
| World wiring | `src/Emberward.Core/World.cs:216-236` | hot pockets, sector core, frost pockets, ore last, then `HotRockPrewarmTicks` `CoolDown` calls | |
| Fixed-temperature engine path | `src/Emberward.Core/Thermal/TerrainHeatEngine.cs:41-50, 95-107, 358-361` | pinned cells return their fixed temperature and book `FixedCellEnergySunk` | |
| Pin validation | `src/Emberward.Core/Thermal/ThermalMaterialTable.cs:19-37, 62-75` | a pinned row cannot have a loss; unpinned rows need a loss in [0, 1) | |
| Ledger bucket | `src/Emberward.Core/Thermal/ThermalLedger.cs:20, 42, 53, 61` | `SunkFixedTemperature` | the energy a pin adds or removes |
| Outside loss | `TerrainHeatEngine.cs:95,107`; `TerrainGrid.Heat.cs:416` (`SinkCellToAmbient`); `TankBase.cs:321-322` | `Loss` per row; base cooldown uses `SinkCellToAmbient` | |
| Ambient | `src/Emberward.Core/Config/Tweaks.cs:44` (`ThermalAmbientTemperature = 0f`) | | |
| Sector goal | `src/Emberward.Core/World.cs` (`new SectorGoal(corePosition, …)`) | the hot core the goal taps | |
| Radar heat threshold | `Tweaks.cs:470` (`Radar.HeatThreshold = 10`); `Radar.cs:336, 348, 432, 477, 591` | cells above 10 read as heat | |

Tuning constants (`Tweaks.cs`, about lines 88-112): `HotRockPocketCount` 8, `HotRockCoreRadius` 8,
`HotRockCentreBias` 1.6, `HotRockMinRadius` 3, `HotRockMaxRadius` 5, `HotRockMinDistanceFromBase` 60,
`HotRockMinDistanceBetween` 50, `HotRockPrewarmTicks` 240; `FrostRockPocketCount` 5, `FrostRockMinRadius` 2,
`FrostRockMaxRadius` 4, `FrostRockMinDistanceFromBase` 45, `FrostRockMinDistanceFromHot` 35,
`FrostRockMinDistanceBetween` 40.

Tests that touch the pins, pockets or sinks (check each one): `HotRockTests`, `HeatEquilibriumTests`,
`FullGridHeatStep`, `MatchOutcomeTests`, `MatchRulesTests`, `PowerLoopTests`, `SectorGoalTests`,
`SimulationFingerprintTests`, `SimulationTableGuardTests`, `SnapshotTankRoundTripTests`, `SyncDigestTests`,
`TankCollisionTests`, `TankTemperatureTests`, `TerrainBehaviorTests`, `TerrainMaterialCoverageTests`,
`TerrainPixelTests`, `TestHelpers`, `ThermalLedgerTests`, `WorldThermalSimulationTests`, `OreDepositTests`.

## 2. The work, in order

1. **Base sinks.** Make the base cells (Base, ConstantEnergy) finite, with capacity and an outside loss. The base's
   reservoir and radiator stay the main sink (DESIGN §4.1, §4.10 item 11). Replace the efficiency and docking
   reference in `TankBase.cs:42` and `TankThermals.cs:30` with the base's own stored temperature, or with ambient.
   Re-derive the efficiency numbers and check `PowerLoopTests`.
2. **Hot rock.** Keep the starting temperature (100 °C) as an initial condition, drop `FixedTemperature`, and give the
   row a capacity and a loss. Keep the centre-core logic and `HotRockPrewarmTicks`. Decide what the sector goal needs:
   if the goal must stay hot until tapped (§4.8), the goal must be measured by stored energy, not by the core's
   temperature.
3. **Frost rock.** The same change: start at -60 °C, drop the pin, let it warm back toward ambient.
4. **Pair table.** Remove the pinned-pair overrides (`Tweaks.cs:84-86`) or re-derive them from the rows
   (`ThermalMaterialTable.PairConductance`). `ARCHITECTURE.md` near line 482 says today only pairs of pinned sinks
   are listed.
5. **Ledger.** Once nothing is pinned, `SunkFixedTemperature` has no source. Either remove the bucket or keep it
   documented as zero. Update `ThermalLedger`, `ThermalLedgerTests` and `SimulationTableGuardTests`.
6. **Seeding.** Hot and frost rock seed through `SeedHeatedPixel`, the same path as the ore. Check the ledger balance
   from tick 0 (`NetExternal`).
7. **Pin validation.** `ThermalMaterialTable` rejects a pinned row with a loss. Once no row is pinned, remove or
   keep the check; keep the rule that an unpinned row needs a loss, or ground never settles (`ARCHITECTURE.md` near
   491).
8. **Fingerprints.** `SimulationFingerprintTests` will change. Update the constants and say so in the commit message.
   Coordinate the order with the other session (section 4).
9. **Snapshots.** If you add any stored energy field, make sure it is saved. `SnapshotCoverageTests` and
   `SnapshotRoundTripTests` must pass.
10. **Docs.** Update `DESIGN.md` §4.4 and §11 (both say "pinned until §4.1 lands"), §4.7 (frost), the decision log,
    and `src/Emberward.Core/ARCHITECTURE.md`.

## 3. Challenges to solve (DESIGN §4.10)

Each needs an answer before the numbers are final:

1. **The match's energy budget.** The pins were the only supply that never ran out. Size the stock so a match lasts,
   against the base radiator's 500 per tick.
2. **Relays drain sources.** A relay that pulls heat from a pocket uses it up. A relay makes a place workable only
   until it cools.
3. **The radiator is no longer the only constraint.** With finite sources, total heat falls over time. Reword the
   decision that the radiator is the bottleneck (DESIGN §13, "Everything useful makes heat").
4. **Cost on a large map.** A finite body changes until it reaches ambient, so more tiles stay active for longer.
   The rule that a step's cost follows activity must still hold. Measure on 1500x750 before tuning.
5. **Halos.** The pinned ring that bounded a pocket's halo (DESIGN §4.3) goes. The outside loss must now bound a
   finite pocket's spread, and "dangerous" becomes a duration, not a distance.
6. **Tuning is a stock problem.** A starting temperature no longer guarantees danger. Hot rock, lumps, frost rock and
   the radiator must be tuned together.
7. **Determinism, saves and multiplayer.** Stored heat is part of the state: the match hash, the snapshot, the
   fingerprints and replays all change. Every client must start from the same stock.
8. **The ledger's baseline.** The starting stock is seeded energy, booked from tick 0.
9. **Sectors freeze.** A frozen sector (DESIGN §7.3) keeps a fixed output. Its stored heat must freeze too, or it
   cools while nobody plays.
10. **Visuals and the radar.** Glow, frost tint and the radar's heat threshold (`Radar.HeatThreshold`, 10) must follow
    temperature. A cooling speck dims and eventually reads as ground, and the radar must still find emberite from
    afar.
11. **The base interior.** Base cells become ordinary cells, so heat conducted into the base stays in the ground until
    the reservoir takes it. Check that the base still acts as the sink.

Opportunity: quenchable hot rock (DESIGN §6.1) already wants rock that cools. Finite bodies make it consistent.

## 4. Coordination

- **Another session** (ref `e775ee`, "Upgradables review and improvements") will implement the finiteness of heat
  sources itself, after its tranche 1 (capacitors, behind a `MatchRules` flag, default off). It has not committed
  and does not plan to. Its clash list: `Tweaks.World.ThermalMaterials`, the fingerprints, the base engine efficiency
  (it uses the pinned 0), and a generator design that assumes a hot-rock source. It reads DESIGN §4.1 and §4.10. Its
  own DESIGN §4.9 is the energy network; this plan is §4.10.
- **Before you start the pin removal**, check with the user which session does it. Two sessions editing the same
  thermal rows will conflict.
- **Fingerprint order.** Update `SimulationFingerprintTests` after `a6e43f36` is merged, not before.
- **Ore files** were committed in `a6e43f36`: `OreDeposits.cs`, `TerrainGrid.Ore.cs`, `TerrainGrid.HotRock.cs` (the ore
  parts), `OreMaterial.hlsli`, `TerrainAuxPass.hlsli`. Avoid conflicting edits there.

## 5. Open decisions for the user

- **Core start.** 600 °C × 16 capacity per cell. A bare core beside a tank kills it in 1–2 s. A large lump stays above
  100 °C for about 17 s and above 60 °C for about 57 s (`TankTemperatureTests`). This is the main tuning knob.
- **Relays drain finite lumps.** A relay cools a lump in tens of seconds, so a lump is harvestable early. Should an
  intact core hold against a relay? (DESIGN §14)
- **Plain ore starts at 9 °C**, just under the radar threshold of 10. Raise the threshold with the start if you raise
  the start.
- **Death-dropped ore** (`OreDrop`, written with `SetPixel`) is unseeded and cold.
- **Bots** do not avoid hot lumps. The late-join slot in `NetSessionJoinTests` changed from 3 to 2, and the cause is
  not confirmed.
- **Outside loss** must bound halos: ground loses about a third of its heat in a minute, open air in about 15 seconds
  (DESIGN §4.3).
- **The radiator as bottleneck** (DESIGN §13). Does the everything-finite decision change it?
- **Sector freeze** (DESIGN §7.3) must freeze stored heat as well.

## 6. Verify

- `dotnet build Emberward.slnx` (this also compiles every shader to DXIL, SPIR-V and MSL)
- `dotnet test Emberward.slnx --filter "Category!=Slow"` (about 40 seconds)
- `dotnet test Emberward.slnx` (about 2.5 minutes; run before every commit)
- `dotnet format Emberward.slnx --verify-no-changes`
- Playtest: `tools/playtest/scenario.ps1`, screenshots in `artifacts/playtest`
- Rules from `AGENTS.md`: the simulation is deterministic (no clocks, `Random.Shared` or unordered collections);
  entities exchange energy and never write temperatures; there is one exchange stencil (`ComputeRegionDeltaConservative`)
  and one place that applies a step (`ResolveCell`); heat costs what changes (wake tiles with `ActivateTileByCell`);
  shader resources use the register macros; no `.cs` file goes over 700 lines.
