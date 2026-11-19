# Finite heat follow-ups

`DESIGN.md` §4.10 tracks the finite world design. PR #72 made emberite finite. The next implementation step on PR #75
made hot rock, frost rock and the base finite. Standard matches use only finite materials. The custom fixed temperature
and its ledger bucket remain available to solver tests.

## Starting values

| Body or rate | Prototype value |
|---|---:|
| Hot rock | Starts at 100 °C, holds 128 energy per degree per cell, loses 0.005% of its excess per step |
| Frost rock | Starts at −60 °C, holds 16 energy per degree per cell, exchanges 0.03% of its difference with ambient per step |
| Core of an emberite lump | Starts at 600 °C, holds 16 energy per degree per cell |
| Plain emberite | Starts at 9 °C, below the radar heat threshold of 10 °C |
| Base area cooling and stock radiator | Up to 480 energy drawn and 500 energy vented per tick |
| Base reactor | 50,000 energy at capacity, from ambient when empty to 20 °C above ambient when full |

Tank actions that add heat to hot or frost rock use the same input energy as ordinary stone. Relays can use the seeded
stock and any heat put back into the pocket. They have no access to an infinite terrain source. The engine scale is
120 °C, with its existing 50% unupgraded cap. Generator design can use a local temperature difference and the finite
stock directly. The energy network's capacitor rates and idle draw keep their current values.

## What still needs playtesting or design work

- **§4.10.1 and §4.10.6. Heat budget and tuning.** Test whether the supply lasts long enough to reach and tap the
  core in typical play, and tune hot pockets, frost pockets and the radiator together. Keep emberite core tuning at
  the existing starting values until play gives a reason to revisit it. Check whether an intact lump should hold
  against a relay longer. Dropped ore starts unheated. In an isolated 49 × 49 dirt map with a full radius-8 core and
  two relay intakes 10 cells from its centre, the stock engine fell below 5,000 charge/s before a full 60-second tap.
  One engine upgrade held at least 5,341 charge/s after two idle minutes. Two held at least 5,155 charge/s after ten.
  This probe uses direct intake at the relay rate and the stock radiator, with no base area heat load.
- **§4.10.4. Load under play.** In a Release run of a 1500 × 750 standard map (seed 8181, decoration seed 8182, idle
  input for 2,880 ticks after generation), the solver averaged 220 active and 712 stepped tiles of 1,128. The
  pre-change branch averaged 226 active and 711 stepped. The core cooled from 98.2 °C to 83.3 °C during the probe.
  Compare isolated timings and activity under sustained play before changing the sparse solver.
- **§4.10.9. Sector freeze.** The campaign still needs a sector save and resume system. Save the temperature fields
  and reservoirs when freezing a sector, and only evolve them while its simulation is active.
- **§4.10.10. Presentation.** The shader and radar already use live temperatures. Visually check cooling pockets on
  both clients when tuning. The radar should still find cooled plain emberite as ore.

The other challenges are covered by the simulation and regression tests. Relays drain sources (§4.10.2), the
radiator and reachable heat stock have separate limits (§4.10.3), and ambient loss bounds the halo (§4.10.5). The
existing snapshots and world hashes cover the temperature fields and reactor stock (§4.10.7). Generation books
seeded heat and cold in the ledger (§4.10.8). Base walls and its core store heat, and the base vents the heat it
actually takes into the reactor (§4.10.11).
