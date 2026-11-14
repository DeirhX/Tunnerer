# Tunnerer: Game Design

Status: direction agreed in a planning session on 2026-11-14. This document records the intended game, not the
current one. For how the code is organised, see `ARCHITECTURE.md` and `src/Tunnerer.Core/ARCHITECTURE.md`.

Each item is marked **Decided** (the direction is settled) or **Proposed** (suggested during planning, not yet
confirmed). The full list is in [Decision log](#13-decision-log).

## 1. Vision

Tunnerer is a top-down digging and building game in which **heat is the waste product of everything you do, and
your base is the only place it can leave the world**. You drive a tank that digs, hauls, builds and fights. You
push out from a cold base into hot ground, build the infrastructure that makes each place workable, and leave
behind a network that runs without you. You do not grow by owning more things. You grow by being able to get rid
of more heat, from further away, in hotter ground.

The game moves away from its origin as a one-on-one tank duel. A single AI opponent is not enough to carry it.

## 2. Pillars

1. **Heat is simulated honestly.** Energy is conserved and every source and sink is accounted for. Mechanics are
   built on the simulation, never around it.
2. **One system, not four.** Hazards, mobs, minerals and structures all act through heat. A feature that does not
   touch heat needs a strong reason to exist.
3. **Autonomy is the goal.** A place is finished when it runs without the player.
4. **Every solution creates the next problem.** New ground changes what kind of problem heat is, and new load
   stresses what was built before.
5. **The player is a builder who can fight.** Structures do the standing work; the tank does the new work.

## 3. Core loop

There are four nested loops. Each should be satisfying without the one above it.

| Timescale | Loop | Ends when |
|---|---|---|
| Minutes | Pre-cool at base, dive, reach something valuable, get home | The coolant charge runs out or the cargo is full |
| A session | Make one sector autonomous | The sector reaches thermal steady state at its quota |
| The campaign | Work inward, sector by sector, toward the core | The core is tapped |
| Endless | Replay sectors for a better score or under harder modifiers | Never |

The session loop in detail:

1. Pre-cool at base and dive.
2. Hit an obstacle that one coolant charge cannot brute-force.
3. Build infrastructure that makes that spot workable.
4. Extract what is behind it and haul it home.
5. Spend it on reach: more coolant, better relays, a bigger radiator.
6. Replace each manual job with a structure until the sector runs unattended.

## 4. Heat

### 4.1 Rules

- **Everything useful makes heat.** Digging, shooting, harvesting, pumping and, later, refining all inject heat.
  *(Proposed. Today only tank actions, the torch and explosions do.)*
- **The base is the main heat sink.** *(Decided.)* It has a finite reservoir and a radiator that vents at a fixed
  rate. The radiator is the shared constraint that everything competes for.
- **Heat reaches the base in two ways.** *(Decided.)*
  - **Tanks** carry it. A tank docked at its own base is refrigerated below ambient; that coolant charge is spent
    by absorbing heat in the field.
  - **Relays** pipe it. A relay pulls heat from the ground around it and sends it to the base.
- **Tanks and relays have different jobs.** *(Proposed.)* Coolant decides how long you can stay somewhere hot.
  Relays decide whether a hot place becomes permanently workable. A tank is not a meaningful way to cool the
  world, and should not be tuned to become one.

### 4.2 Conductors and insulators *(Decided)*

Minerals have thermal jobs:

- **Conductors** make better relays and real heat lines: placed material that carries heat along a route.
- **Insulators** make walls that keep heat out of a tunnel.

This fits the solver, which already exchanges heat between neighbouring cells using a conductance per pair of
materials. A conductor or insulator is a new terrain material with its own row in that table; routing follows from
the simulation. Digging is already a weak form of this, because dirt conducts far worse than stone.

### 4.3 Leaks to close *(Proposed)*

Heat currently leaves the world in ways that bypass the base. Each one weakens "the base is the sink":

- Base cells are pinned at ambient, so heat that conducts into the base interior is deleted without using the
  reservoir.
- Digging hot dirt out to air destroys that cell's heat.
- An optional global dirt cooling removes heat everywhere.

### 4.4 Heat as a resource *(Proposed)*

Late in the campaign the gradient between hot rock and a cold base drives generators. What was killing you now
powers you, and that power is what taps the core.

## 5. The player *(Decided: mainly a builder that can also fight, as in Factorio)*

### 5.1 Every manual verb has an automated counterpart

A sector only completes when it runs without the player, so nothing may depend on the tank forever.

| The tank does it first | A structure takes over |
|---|---|
| Dig | Harvester or drill |
| Haul ore home | Hauler or conveyor |
| Carry heat out on the coolant charge | Relays and conductor lines |
| Shoot what attacks | Turrets |

### 5.2 Combat

- **Structures defend; the tank attacks.** Holding the line is the network's job. Clearing a nest is a deliberate
  expedition the player equips for.
- **Fighting spends heat.** Firing heats the tank, so a fight draws on the same budget as a dive.
- **Death is cheap.** The tank respawns at base. The player loses the cargo and the time, not the progress.

### 5.3 What the tank needs

- Cargo capacity, as an upgradeable stat.
- A placement interface for structures. This is real work: the game renders at 320×200 and builds with single key
  presses today.

## 6. The world

### 6.1 Dangerous terrain *(Decided in principle; specifics Proposed)*

Some ground cannot simply be driven through. It has to be mined out or taken apart with a plan, and supporting
structures make that easier.

- **Quenchable hot rock.** Hot rock stops being indestructible. Cooled below a threshold by relays, it becomes
  minable and yields metal. This is the first hazard to build, since hot rock exists.
- **Gas pockets** that ignite above a temperature.
- **Unstable rock** that collapses unless propped.

### 6.2 Mobs and nests *(Decided in principle; behaviour Proposed)*

Mobs crawl out of nests. They are **heat-seeking**: warm tunnels and working relays attract them, and nests wake
as the ground around them warms. Sloppy heat management therefore causes the mob problem, and relays become
something to defend.

Across the whole game, the total heat the player rejects raises the threat level, in the way pollution drives
enemy evolution in Factorio.

Mobs are the most expensive feature here: they need pathfinding through terrain that keeps changing, spawning
rules and combat tuning.

### 6.3 Minerals and metals *(Decided in principle)*

Rare minerals and metals sit in the terrain, and the player has reasons to collect and use them:

- Each has a thermal job (see 4.2), plus a coolant mineral that adds reservoir or tank capacity.
- The best deposits are in the hottest ground.
- Each sector's minerals unlock the technology for the next, so sectors cannot be skipped.

### 6.4 Structures

Existing: harvester, charger, relay. All cost only dirt today, although build costs already carry a minerals
field. Planned: conductor lines, insulating walls, haulers, turrets, props, heat pumps, generators.

### 6.5 The rival tank *(Proposed)*

The opposing tank stops being the centre of the game. It becomes one possible sector twist: a competitor for the
same deposits.

## 7. World structure: sectors

### 7.1 Separate maps in one persistent world *(Proposed; the freeze rule in 7.3 builds on it)*

The world is a map of sectors. Each sector is its own playfield with one dominant problem. Finished sectors keep
producing and feed the next ones.

Reasons to prefer this over one continuous world:

- A new site lets the player rebuild better with new technology, instead of demolishing the first base.
- A sector gives a session a natural end.
- The per-pixel heat simulation only has to run where the player is.

What is given up is one unbroken world to scroll across. A world map that shows each sector's heat overlay
recovers part of that.

### 7.2 Completion: thermal steady state *(Proposed)*

A sector is complete when it meets an output quota while heat in equals heat out, without player input for a set
time. The energy ledger already measures the balance.

### 7.3 Freeze and resume *(Decided)*

- A completed sector **freezes to a fixed output rate** and stops simulating.
- It **resumes simulation when a new problem is injected**, such as enemy presence.

Rules that go with it *(Proposed)*:

1. **Simulation only runs while the player is present.** An injected problem puts the sector on alert, and its
   output drops by a simple rule until the player visits.
2. **Headroom buys time.** At freeze, the sector records its spare cooling capacity beyond the quota. More margin
   means output holds longer under alert. This rewards over-engineering and gives a reason to improve a sector
   that already passed.
3. **Problems come from the player's own actions**, not a random timer: opening a neighbouring sector, raising a
   quota, or crossing a total-heat threshold. An alert should read as a consequence the player could have foreseen.
   Being pulled back to defend finished sectors for no reason is a known failure of this model.
4. **Re-freezing requires steady state again**, possibly at a new output rate.

A frozen sector's snapshot stays valid however long the player is away, because it was taken at equilibrium.

### 7.4 Sector twists *(Proposed)*

Each sector breaks one assumption. Examples: no stone to conduct through, a magma flow crossing the map, a base
that cannot vent, mobs that eat relays, a rival tank.

## 8. Progression

### 8.1 The goal: reach the core *(Proposed)*

The world gets hotter toward its centre, and the bases start at the cold rim. At the centre is the thing the
player is trying to tap. Every tunnel, relay and upgrade is a step toward it.

### 8.2 Each ring changes what kind of problem heat is *(Proposed)*

| Ring | What heat is | What the player learns to build |
|---|---|---|
| 1 | A personal timer | Coolant charge, short dives |
| 2 | A logistics problem | Relay and conductor networks with limited throughput |
| 3 | Hotter than the sink absorbs passively | Active heat pumps, which cost power and make more heat |
| 4 | Hot enough to destroy the player's own structures | Insulation and alloys from deeper minerals |
| 5 | A resource | Generators running on the gradient |

### 8.3 What keeps earlier builds relevant

- The shared radiator: every new outpost loads the same exit.
- Heat-seeking mobs: a bigger, warmer network is a bigger target.
- Quotas that step up and force a rebuild of the trunk line.

### 8.4 Persistence *(Proposed)*

One persistent world per save. Progress comes from technology unlocked by minerals found in that world. There is
no skill tree that carries between saves; one can be added later if the loop proves to need it.

## 9. Why keep playing

Systems borrowed from games where they worked:

| From | System | Here |
|---|---|---|
| Mindustry | Captured sectors keep producing and export to new ones | Finished sectors ship minerals and unlock hotter neighbours |
| Factorio: Space Age | Each planet breaks one assumption | Each sector has one twist |
| Factorio | Pollution drives enemy evolution | Total heat rejected wakes the nests |
| Satisfactory | Delivery milestones in phases | Quotas that step up |
| Subnautica | Depth gated by equipment | Hotter sectors need alloys from the previous one |
| Zachtronics games | Histograms of a solution against others | A score per sector |
| Creeper World | A fluid enemy that floods the map | Heat is the creeping enemy |
| Oxygen Not Included | Heat as the late-game problem, then a power source | Rings 3 to 5 |

## 10. What the player is proud of

- The heat overlay of a sector: a cold artery cut through glowing rock.
- Honest numbers per sector: output per unit of heat rejected, time to steady state, headroom.
- How far inward the save has reached.

## 11. Starting point in the code

On the thermal integration work (branch `integrate/thermal-ledger-and-visuals` at the time of writing; not yet on
`master`), the game already has:

- An energy-exact heat simulation over terrain and air fields, with a ledger of every source and sink.
- A base with a finite reservoir and a radiator.
- Relays that pump heat from a radius into the base when linked by line of sight.
- Tank coolant: pre-cooling at the home base, spent by absorbing heat.
- Hot rock pockets as permanent heat sources, opt-in per world.
- Harvester and charger machines, gold as a pickup, and one bot opponent.

Gaps between that and this design, using the constants in `Tweaks.cs` in the working copy on 2026-11-14:

1. **Nothing forces the player into heat.** Digging adds 0.008 heat per pixel and a shot 0.25, against a safe
   limit of 100.
2. **The base is not a bottleneck.** The radiator vents about 1,000 energy per tick against a 50,000 reservoir; a
   relay pumps 300.
3. **Tanks carry little.** A fully pre-cooled tank holds about 3,000 energy.
4. **Relays teleport heat.** There is no line capacity and no route to choose or cut.
5. **Heat leaks outside the loop** (see 4.3).
6. **There is one map, one generator and no world map, save format or sector state.**

These figures come from reading the constants, not from play.

## 12. Build order

### 12.1 First prototype

Three changes, to find out whether the step from ring 1 to ring 2 is fun:

1. A temperature gradient toward the centre of the map.
2. Machines that emit heat when they work.
3. A radiator tuned to be the real bottleneck.

### 12.2 After that

Each step should be playable without the next.

1. Minerals with mineral build costs; conductor and insulator materials.
2. Quenchable hot rock.
3. Cargo and a placement interface.
4. Steady-state detection and a sector quota.
5. One mob type and one nest; turrets.
6. World map, sector save, freeze and resume.
7. Heat pumps, structure failure by temperature, generators.

## 13. Decision log

| Decision | Status |
|---|---|
| Take physical heat simulation seriously and build the loop on heat management | Decided |
| Bases are the main heat sinks; heat returns by tank or by relay | Decided |
| Add environmental hazards, mobs from nests, and dangerous terrain that must be mined out or disassembled | Decided |
| Supporting structures make that easier | Decided |
| Rare minerals and metals with a reason to collect and use them | Decided |
| Minerals act as thermal conductors and insulators | Decided |
| The goal is an autonomous system | Decided |
| A finished sector freezes to a fixed output rate and resumes when a problem is injected | Decided |
| The tank is mainly a builder that can also fight | Decided |
| Separate sector maps in one persistent world | Proposed |
| A sector completes at thermal steady state | Proposed |
| Reach the core; rings that change the kind of heat problem | Proposed |
| Everything useful makes heat; the radiator is the shared bottleneck | Proposed |
| Heat-seeking mobs; threat scales with heat rejected | Proposed |
| Quenchable hot rock as the first hazard | Proposed |
| Alerts are caused by the player's actions; headroom buys time | Proposed |
| No skill tree across saves | Proposed |
| Rival tank becomes an optional sector twist | Proposed |

## 14. Open questions

- How large is a sector, and how long should one take?
- What does a sector export, and how does it travel between sectors?
- Do conductor lines replace relays or sit alongside them?
- How does a heat line fail: by throughput, by temperature, or by being cut?
- How is the output drop under alert calculated?
- What exactly is at the core?
- Is there multiplayer, and if so is it cooperative or competitive?
