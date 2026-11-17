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
| Minutes | Cool down at base, dive, reach something valuable, get home | The heat budget runs out or the cargo is full |
| A session | Make one sector autonomous | The sector reaches thermal steady state at its quota |
| The campaign | Work inward, sector by sector, toward the core | The core is tapped |
| Endless | Replay sectors for a better score or under harder modifiers | Never |

The session loop in detail:

1. Cool down at base and dive.
2. Hit an obstacle that one dive's heat budget cannot brute-force.
3. Build infrastructure that makes that spot workable.
4. Extract what is behind it and haul it home.
5. Spend it on reach: better relays, a wider network, a bigger radiator.
6. Replace each manual job with a structure until the sector runs unattended.

## 4. Heat

### 4.1 Rules

- **Everything useful makes heat.** Digging, shooting, harvesting, pumping and, later, refining all inject heat.
  *(Proposed. Today only tank actions, the torch and explosions do.)*
- **The base is the main heat sink.** *(Decided.)* It has a finite reservoir and a radiator that vents at a fixed
  rate. The radiator is the shared constraint that everything competes for.
- **Heat reaches the base in two ways.** *(Decided.)*
  - **Tanks** carry it. A docked tank's heat is pumped into its base, settling it near 0 (§4.7).
  - **Relays** pipe it. A relay pulls heat from the ground around it and sends it to the base.
- **Tanks and relays have different jobs.** *(Proposed.)* The tank's heat budget decides how long you can stay
  somewhere hot.
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
powers you, and that power is what taps the core. §4.5 brings the first form of this forward into the core loop.

### 4.5 Power: heat in, charge out *(Prototype)*

Power is heat taking a detour. The base's dimensional link is a cold sink, and heat travelling from a hot place
to it can drive an engine. The engine's output is **charge**, which runs the machines. Spending charge turns it
back into heat where it is spent, so the energy ledger still accounts for every bit of it.

```
hot ground --relay--> base engine --> charge store --pylon--> batteries
     ^                    | waste heat                          |
     |                    v                                     |
     |                radiator                                  |
     +---------- work heat (digging, mining) <------------------+
```

- **The engine.** Every unit of heat a relay delivers passes through it. A fraction becomes charge and the rest
  goes into the base's heat store as before. The fraction depends on the temperature difference, like a real heat
  engine: `eta = min(dT / (dT + K), eta_max)`, where dT is how far the source is above the base. Warm dirt gives
  a trickle and hot rock gives a lot, so **the dangerous hot places are your power plants**. Heat turned into
  charge doesn't have to be vented, so better sources also ease the radiator bottleneck (§12.1).
- **Charge storage.** The base keeps a charge store and starts with enough for the first harvester, so the loop can
  start without power already in hand. Harvesters carry batteries, filled from the store when planted.
- **Pylons** are link nodes that hand base charge to batteries in direct line of sight within a short range,
  drawing it over the link network. A link carries orange heat in and blue charge out. A relay cools a spot and a
  pylon powers one, so a dig site needs both laid out with connecting lines of sight: a small **mining camp**.
- **Harvesters refuel.** Each mining action and each crawl step costs charge, which ends up as heat in the ground
  beside the harvester. Below a quarter full, a harvester stops mining and crawls back toward the nearest powered
  pylon of its base. It waits in the pylon's beam and goes back to work above 80%. With no pylon to go to, it works
  until the battery is flat.
- **Running at a loss.** A camp beside hot rock makes more charge than its harvesters spend. A camp in cold dirt
  only recovers the harvesters' own warm waste heat, at low efficiency, so it slowly drains the base. The loop only
  comes out ahead when it taps a real heat source.
- **Failing gently.** With the store empty, pylons stop and harvesters strand where they are. Relays keep pumping:
  they are passive heat pipes.
- **Accounting.** Heat converted to charge, and charge spent back as heat, are booked in the ledger
  (`ConvertedToCharge`). Thermal energy plus all stored charge balances against what entered and left the system.

Still to do (Proposed):

- Charge balance per minute as part of the steady-state check (§7.2): a sector is finished when its camps make at
  least the charge they spend.

The tank does not use charge. Its one limit is heat (§4.6); a second meter on the tank was rejected so the player
never tracks two resources at once.

### 4.6 Network coverage: the leash *(Prototype)*

Exploring should be planned, not free. The network you have built is your cooling line: inside its coverage the
tank is cooled through the links; outside it, the tank heats up. How far you can go is how long your heat lasts
off the network. There is no new meter: the TEMP gauge is the only thing to watch.

- **Coverage.** The base and every powered relay and pylon cover a circle around themselves. A position is covered
  when it is inside a node's circle and in direct line of sight through open ground, the same rule links use, so
  coverage follows your tunnels.
- **Relays hold the ground open** *(Prototype)*. No new dirt grows within the range of a planted relay or pylon,
  linked or not, so tunnels along a network stay dug. Elsewhere dirt closes in again, slowly.
- **Inside coverage** the tank's heat goes down the links into the base reactor, so it competes with the relays for
  the same radiator. Coverage only takes heat away; it never chills the tank below 0.
- **Outside coverage** the tank's heat floor climbs steadily. Work heat from digging and firing still sheds into the
  ground as usual, but the tank can never cool below the floor. Leaving at 0, an idle tank lasts about 100 seconds
  out; every burst of digging spends some of what is left once the floor is high. Back in coverage the floor falls at
  the network's cooling rate, so a moment's dip is not a full reset. Sub-zero ground (a frost pocket, §4.7) is a real
  cold sink: there the floor follows the tank down, so cooling off at one buys back time.
- **What stops spamming relays.** A relay is only useful where it can see the network. Cooling strength falls with
  each link hop from the base, so the far end of a long chain barely holds the tank. Every node also loads the
  shared radiator. Going much further means a forward base: a big, planned decision.
- **What the player sees.** Each node draws a faint field with a soft rim, brighter while the tank is off network, so
  the way back reads at a glance. While covered, a thin tether carries the tank's heat to its node. Off network an
  "OFF NETWORK ~Ns" tag above the TEMP gauge shows how long until the floor reaches the safe maximum.

### 4.7 Temperature: too hot, too cold *(Prototype)*

The tank lives in a band around 0. Both ends hurt, and the world has places at both ends.

- **Above 100 the tank burns.** Overheating takes shield damage per degree over the limit, every tick.
- **Below 0 the tank is chilled.** It slows down, linearly to about a third of its speed at -40, and below -25 it
  takes frostbite damage per degree, the same way overheating does. Negative temperatures are unusual, never a
  reserve to stock up on.
- **The base settles the tank near 0.** Docked at its own base, a share of the tank's heat above 0 is pumped into the
  base reservoir every tick, on top of conduction with the base floor. A hot tank cools fast at first and levels off
  near 0; a cold tank is warmed back by the floor. A full reservoir stops the pump.
- **Heat exchange follows the temperature difference.** A tank drifts toward the temperature of whatever it stands in,
  in both directions: hot ground warms it, cold ground chills it, and it warms or chills the cells under it in turn.
- **Frost pockets** are indestructible rock pinned at -60, scattered at middle distances from the bases and away from
  hot pockets. Beside one, a hot tank sheds heat quickly, which makes it a place to cool down on a dive (and, off
  network, a way to buy back time, §4.6). Stay too long and the tank gets chilled, then frostbitten. Frost is drawn as
  an icy tint with a slow sparkle on any ground below 0, so cold places read at a glance.

### 4.8 The sector goal and base upgrades *(Prototype)*

Building has to pay off, and exploring has to lead somewhere.

- **The goal: tap the core.** Every map with hot rock has a large hot core at its centre (moved to the nearest spot
  clear of the bases when a base spawns too close). The sector is won when relays within about 22 cells of the core
  make at least 5k charge/s for the player's base, held for 60 seconds. Falling short drains the hold timer at twice
  real time, so the tap has to be steady, not a lucky burst.
- **Why it needs everything.** The core is far from both bases, so reaching it means a relay chain (the network
  leash, §4.6). By the numbers one relay by the core falls well short of the goal rate (a relay pumps 300 a tick and the engine converts at most half), so it takes several, and their heat loads
  the radiator. Hauling the dirt for all of that is what harvesters are for.
- **Upgrades: what the dirt store buys.** Docked at its own base, the tank spends the base dirt store with keys 1-4.
  Each upgrade has three levels; level n costs 600 x n dirt.
  - **Radiator:** vents one more unit (500 energy) per tick per level, so more relays and more tank cooling fit.
  - **Network reach:** +6 cells on every coverage circle per level.
  - **Engine:** +10% maximum efficiency per level, and warm sources convert better.
  - **Insulation:** the off-network clock runs 25% slower per level.
- **Harvesters are income.** Their dirt goes straight into the store that buys upgrades, so a camp of harvesters
  near the base turns into a bigger radiator and a longer reach.
- **The HUD** shows the goal (core tap rate and hold timer) at the top of the base panel, an upgrade list on the left
  of the view while docked (clear of the base's top and bottom entrances), and a banner when the sector is complete.

## 5. The player *(Decided: mainly a builder that can also fight, as in Factorio)*

### 5.1 Every manual verb has an automated counterpart

A sector only completes when it runs without the player, so nothing may depend on the tank forever.

| The tank does it first | A structure takes over |
|---|---|
| Dig | Harvester or drill |
| Haul ore home | Hauler or conveyor |
| Carry heat home in the tank | Relays and conductor lines |
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

- Each has a thermal job (see 4.2), plus a mineral that adds reservoir capacity.
- The best deposits are in the hottest ground.
- Each sector's minerals unlock the technology for the next, so sectors cannot be skipped.

### 6.4 Structures

Existing: harvester, relay, pylon. All cost only dirt today, although build costs already carry a minerals
field. Planned: conductor lines, insulating walls, haulers, turrets, props, heat pumps, generators.

### 6.5 The rival tank *(Proposed)*

The opposing tank stops being the centre of the game. It becomes one possible sector twist: a competitor for the
same deposits.

### 6.6 Fog of war and radar *(Prototype)*

The player sees only what their side sees, so that learning the shape of the world takes travelling through it.

- **Three kinds of ground.** *In sight*: shown live. *Remembered*: seen before, shown as it was last seen, drained
  toward a cool blue-grey and dimmed, with no entities and no live heat. *Unknown*: never seen, black.
- **What sees.** The tank sees along open tunnel (about 45 cells) and a little through solid ground around itself.
  The base sees along open ground over its coverage circle. A relay or pylon sees its whole coverage circle, through
  the ground, whether or not it is linked; every other machine sees a few cells around itself. The network is
  therefore the player's eyes as well as its cooling line (§4.6): a place stays lit, with its live heat, only while
  something of yours stands there. The edge of sight is a gradient, never a hard line.
- **Radar** *(Prototype)*. Every 4 seconds the tank sends a pulse: a faint ring that runs outward from where the
  tank stood, taking about 3 seconds to reach its full range of 150 cells, well past sight. Dirt and open ground let
  it through; rock, hot and frost rock, gold, another side's base walls, machines and tanks reflect it and stop it.
  Each return is a single blip at the first cell that reflected, flaring the moment the ring reaches it (near
  things first) and fading over 10 seconds: green for rock, amber for metal, and a white-violet dot in a ring for
  something alive (today: the enemy tank; mobs later). What lies behind a reflector is in its
  shadow, so the outline of a thing is only learned by pinging it from several sides, and successive pulses hit
  slightly different points, so a shape fills in while you wait. When a later pulse passes an old blip it checks it
  again: a blip of an enemy that has driven off, or of ground that has changed, goes out; one hidden in a new
  shadow stays until it fades. A tunnel or cave behind dirt gives a weak blue return and lets the pulse on. The
  Different things show from different distances: a large rock formation from the pulse's full 150 cells, a thin
  wall only from about 60, gold from 90, machines from 75, a tank from 60, a cave from 50, and hot ground from 50
  to 120 by how hot it is. Beyond its range a thing still casts its shadow but leaves no blip, so the far picture is
  only the big shapes and the detail is a surprise kept for when you get close. The
  radar reports only news: nothing in sight, nothing in remembered ground unless it has changed since, and never
  your own machines. A blip goes the moment you see its cell.
- **Heat blinds the radar** *(Prototype)*. Hot ground absorbs the pulse: every hot cell a ray crosses takes some of
  it, more the hotter it is, and shows as a red dot, brighter the hotter the cell. The pulse gets through many warm
  cells but only a few very hot ones before it is spent, so a hot zone shows as red dots reaching a little way into
  it from your side, deeper where it is cooler, and nothing behind them. A return that got through warm ground is
  fainter, and anything too hot to read, hot rock included, returns only its heat. Once relays have cooled the
  ground the next pulse clears the dots. This is the radar's tie to heat (pillar 2). Still open: whether pinging should cost heat,
  and whether the tank's own heat should weaken its radar.
- **It is information, not physics.** Vision never changes the simulation: heat, machines and mobs run the same
  seen or unseen. Whether unseen ground may be simulated more cheaply is a separate question (below).

**Larger maps.** Sectors are meant to grow well beyond today's largest map (1500 by 750), so the cost of a step
has to follow what is active, not the area of the map. Vision already works that way. The heat solver steps only
active regions but still does bookkeeping over the grid, and that is the first thing to make proportional to
activity. Simulating unseen ground more coarsely is the option after that; it would have to keep energy conserved
and results deterministic, and the freeze rule (§7.3) already covers whole sectors the player has left.

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
| 1 | A personal timer | Heat budget, short dives |
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
- Relays that pump heat from a radius into the base when linked by direct line of sight through open tunnel.
- Harvesters that roam near where they were planted, mining dirt into the base store; a relay keeps them cool.
- Tank temperature in a band around 0: docking settles it near 0, cold slows it, both extremes damage it.
- Hot rock pockets as permanent heat sources and frost pockets as cold ones, opt-in per world.
- Harvester, relay and pylon machines, gold as a pickup, and one bot opponent.

Gaps between that and this design, using the constants in `Tweaks.cs` in the working copy on 2026-11-14:

1. **Nothing forces the player into heat.** Digging adds 0.008 heat per pixel and a shot 0.25, against a safe
   limit of 100.
2. **The base is not a bottleneck.** The radiator vents about 1,000 energy per tick against a 50,000 reservoir; a
   relay pumps 300.
3. **Tanks carry little.** A tank's whole safe range is about 2,000 energy.
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

The power loop (§4.5) is in as a prototype: the engine, the charge store, pylons, and harvester batteries with the
return-to-pylon behaviour. Network coverage (§4.6) is in too. Their numbers are starting guesses to tune in play.

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
| Power is converted relay heat; machines run on charge and refuel at pylons | Prototype |
| The tank's range is set by network coverage and its heat; no second meter on the tank | Prototype |
| The sector goal is tapping the core; the dirt store buys base upgrades | Prototype |
| Heat-seeking mobs; threat scales with heat rejected | Proposed |
| Quenchable hot rock as the first hazard | Proposed |
| Alerts are caused by the player's actions; headroom buys time | Proposed |
| No skill tree across saves | Proposed |
| Rival tank becomes an optional sector twist | Proposed |
| Fog of war: the player sees what their tank, base and machines see; the rest is remembered or unknown | Prototype |
| Radar: pulses from the tank leave blips where they reflect, well beyond sight | Prototype |
| Hot ground absorbs the radar pulse; the cells it got through show as red dots by heat | Prototype |
| Maps grow well beyond 1500 by 750; a step's cost must follow activity, not map area | Decided |

## 14. Open questions

- How large is a sector, and how long should one take?
- What does a sector export, and how does it travel between sectors?
- Do conductor lines replace relays or sit alongside them?
- How does a heat line fail: by throughput, by temperature, or by being cut?
- How is the output drop under alert calculated?
- What exactly is at the core?
- Is there multiplayer, and if so is it cooperative or competitive?
- Should the bot (and later mobs) be limited to what it could see, or stay all-knowing?
- What makes a step cheap on a very large map: bookkeeping that follows activity, or coarser simulation of ground
  nobody sees?
