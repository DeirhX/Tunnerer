# Emberflow: Game Design

Status: direction agreed in a planning session on 2026-11-14. This document records the intended game, not the
current one. For how the code is organised, see `ARCHITECTURE.md` and `src/Emberward.Core/ARCHITECTURE.md`.

Each item is marked **Decided** (the direction is settled) or **Proposed** (suggested during planning, not yet
confirmed). The full list is in [Decision log](#13-decision-log).

## 1. Vision

Emberflow is a top-down digging and building game in which **heat is the waste product of everything you do, and
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

- **Every body is finite.** *(Decided; implemented.)* Every cell has a heat capacity and an energy. Hot rock,
  frost rock and emberite start with a stock of heat or cold, which spreads through grounded materials and tends
  toward ambient. The base starts at ambient and can store heat. The radiator and the outside loss in §4.3
  provide the ongoing routes for rejecting heat.
- **Everything useful makes heat.** Digging, shooting, harvesting, pumping and, later, refining all inject heat.
  *(Proposed. Today only tank actions, the torch and explosions do.)*
- **The base is the main heat sink.** *(Decided.)* It has a finite reservoir and a radiator with a fixed vent rate.
  Delivered heat competes for this vent capacity. Making charge also depends on how much heat remains within reach.
- **Heat reaches the base in two ways.** *(Decided.)*
  - **Tanks** carry it. A docked tank transfers heat to its base as it cools toward the reactor temperature (§4.7).
    Docking also draws heat from nearby terrain and air into the visited base's reservoir while it has room.
  - **Relays** pipe it. A relay pulls heat from the ground around it and sends it to the base.
- **Tanks and relays have different jobs.** *(Proposed.)* The tank's heat budget decides how long you can stay
  somewhere hot.
  Relays draw down the stock so a hot place becomes workable sooner. A tank is not a meaningful way to cool the
  world, and should not be tuned to become one.

### 4.2 Conductors and insulators *(Decided)*

Minerals have thermal jobs:

- **Conductors** make better relays and real heat lines: placed material that carries heat along a route.
- **Insulators** make walls that keep heat out of a tunnel.

This fits the solver, which already exchanges heat between neighbouring cells using a conductance per pair of
materials. A conductor or insulator is a new terrain material with its own row in that table; routing follows from
the simulation. Digging is already a form of this: dirt is the insulator of the world (it conducts 30 times worse
than stone, and the air inside solid ground carries no heat through it), so a pocket buried in dirt keeps its heat
until a tunnel uncorks it.

### 4.3 Leaks to close *(Proposed)*

Two routes bypass the base:

- Digging hot dirt out to air destroys that cell's heat.
- Ground and open air exchange a small share of their heat with the outside every tick (deep ground, the surface).
  This takes finite bodies toward ambient, limits the reach of a pocket's halo and lets a dug-out chamber cool
  whatever stands in it. Hot rock loses its stock more slowly than ordinary stone.

Base walls and the base core store heat and conduct it onward. The base draws up to 480 energy a tick from its
surroundings into the reservoir. Its radiator vents up to 500 a tick, including fractional energy, at the stock rate.

### 4.4 Heat as a resource *(Proposed)*

Late in the campaign the gradient between hot rock and a cold base drives generators. With every body finite (§4.1)
that gradient decays as the rock cools, so generators need a source that keeps it up (§4.10). What was killing you now
powers you, and that power is what taps the core. §4.5 brings the first form of this forward into the core loop.

### 4.5 Power: heat in, charge out *(Prototype)*

Power is heat taking a detour. Heat from a hot place drives the engine on the way to the base's finite reactor,
which vents through the dimensional link. The engine's output is **charge**, which runs the machines. Spending charge turns it
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
  engine: `eta = min(dT / (dT + K), eta_max)`, where dT is how far the source is above the reactor. The reactor
  starts at ambient and rises by up to 20 °C as it fills. At ambient an unupgraded engine converts 20% of heat from
  30 °C dirt and about 45% from fresh hot rock. Heat turned into
  charge doesn't have to be vented, so better sources also ease the radiator bottleneck (§12.1). A relay draws from
  the terrain field first and then the remaining air. Each draw contributes charge at its own source temperature.
- **Charge storage.** The base keeps a charge store and starts with enough for the first harvester, so the loop can
  start without power already in hand. Harvesters carry batteries, filled from the store when planted.
- **Pylons** are link nodes that hand base charge to batteries in direct line of sight within a short range,
  drawing it over the link network. A link carries orange heat in and blue charge out. A relay cools a spot and a
  pylon powers one, so a dig site needs both laid out with connecting lines of sight: a small **mining camp**.
- **Harvesters refuel.** Each action of work on ore and each crawl step costs charge, which ends up as heat in the ground
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

*Direction (Proposed, §4.9):* the loop above stays, but relays become the only node (no pylon, no harvester battery
trips); every machine gets a capacitor, a priority tier and an idle draw, so a cut-off machine drains; local
generators add producers beyond the base; and an empty store makes the base *dark* (no shield, slow respawn), which
the tank can restart by carrying emberite home. The tank still has no charge meter.

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
- **The network is also a drop-off** *(Prototype)*. Inside coverage the tank's ore hold unloads into the base store
  at 4 units a second, and the base store pays for what the tank builds there (§6.4). An unlinked node accepts
  nothing.
- **Direction (Proposed, §4.9).** The relay becomes the only node, with pumping and coverage one circle. The off-network
  clock is the tank's reserve: burning a carried unit of emberite extends it and takes its warmth out of the tank,
  and running out is staged (warning, degraded, stranded, then heat death), not a cliff. Relays and generators push
  the safe zone out; far lumps pay for the risk.
- **What the player sees.** Each node draws a faint field with a soft rim, brighter while the tank is off network, so
  the way back reads at a glance. While covered, a thin tether carries the tank's heat to its node, and while the
  hold unloads, gold crystals run beside it and along the links home. Off network an
  "OFF NETWORK ~Ns" tag above the TEMP gauge shows how long until the floor reaches the safe maximum. Tethers,
  links and charge beams grow out from their source when they connect and dissolve when they break, so gaining or
  losing the network is something you see happen, not a line popping on or off.

### 4.7 Temperature: too hot, too cold *(Prototype)*

The tank lives in a band around 0. Both ends hurt, and the world has places at both ends.

- **Above 100 the tank burns.** Overheating takes shield damage per degree over the limit, every tick.
- **Below 0 the tank is chilled.** It slows down, linearly to about a third of its speed at -40, and below -25 it
  takes frostbite damage per degree, the same way overheating does. Negative temperatures are unusual, never a
  reserve to stock up on.
- **Docking cools toward the reactor temperature.** At its own base, a tank transfers a share of its heat above the
  reactor temperature to the reservoir each tick. Heat also moves between the tank and its surroundings by
  conduction. A full reservoir stops the pump.
- **Heat exchange follows the temperature difference.** A tank drifts toward the temperature of whatever it stands in,
  in both directions: hot ground warms it, cold ground chills it, and it warms or chills the cells under it in turn.
  Solid ground transfers heat faster than air. The more of the tank's footprint touches solid ground, the stronger
  that contact is. Each transfer stops when the tank and the material it touches reach the same temperature.
- **Frost pockets** are indestructible rock that starts at -60 and warms back toward ambient as it takes heat, scattered at middle distances from the bases and away from
  hot pockets. Beside one, a hot tank sheds heat quickly, which makes it a place to cool down on a dive (and, off
  network, a way to buy back time, §4.6). Stay too long and the tank gets chilled, then frostbitten. Frost is drawn as
  an icy tint with a slow sparkle on any ground below 0, so cold places read at a glance.

### 4.8 The sector goal and base upgrades *(Prototype)*

Building has to pay off, and exploring has to lead somewhere.

- **The goal: tap the core.** Every map with hot rock has a large hot core at its centre (moved to the nearest spot
  clear of the bases when a base spawns too close). The sector is won when relays within about 22 cells of the core
  make at least 5k charge/s for the player's base, held for 60 seconds. Falling short drains the hold timer at twice
  real time, so the tap has to be steady. The core cools even before the relays reach it. The longer it sits, the more
  important engine upgrades become for sustaining the tap (§4.10).
- **Why it needs everything.** The core is far from both bases, so reaching it means a relay chain (the network
  leash, §4.6). By the numbers one relay by the core falls well short of the goal rate (a relay pumps 300 a tick and the engine converts at most half), so it takes several, and their heat loads
  the radiator. Mining the ore for all of that is what harvesters are for.
- **Upgrades: what the ore store buys.** Docked at its own base, the tank spends the base's ore store (§6.4). There
  are two tracks of three levels each (seven upgrades), every level costing 25, 60 and then 140 ore whichever the upgrade:
  - The **base track**, keys 1-5:
    - **Radiator:** vents one more unit (500 energy) per tick per level, so more relays and more tank cooling fit.
    - **Network reach:** +6 cells on every coverage circle per level.
    - **Engine:** +10% maximum efficiency per level, and warm sources convert better.
    - **Insulation:** the off-network clock runs 25% slower per level.
    - **Sensors:** relays, pylons and the base see 8 cells further per level. Relays and pylons already see 10 cells
      beyond their coverage circle at level 0. Sight is output only (§6.5), so this changes nothing but the ore it cost.
  - The **tank track**, keys 6-7:
    - **Cargo hold:** +30 ore in the tank's hold per level (40, 70, 100, 130).
    - **Radar:** +20% on every radar range and +25% on each ray's heat budget per level (§6.6). The radar is output
      only, so this level changes nothing in the simulation but the ore it cost. A pulse that runs longer than the
      4-second period delays the next one.
- **Harvesters are income.** Their ore goes straight into the store that buys upgrades, so a camp of harvesters
  inside the network turns into a bigger radiator and a longer reach.
- **Direction (Proposed, §4.9).** The global upgrades above stay as the baseline. On top of them, each machine is
  upgraded individually (two levels, next to the machine; a relay chooses Tap, Sensor or Conduit), and a Conduit level
  raises a relay's charge throughput.
- **The HUD** shows the goal (core tap rate and hold timer) at the top of the base panel, an upgrade list on the left
  of the view while docked (clear of the base's top and bottom entrances; the base track above the tank track, with
  the ore store, each upgrade's level, next cost and key), and a banner when the sector is complete.

### 4.9 Resources, the energy network and the fall of a base *(Proposed)*

One sentence to hold: **relays are your network; everything else plugs into it and runs on the charge the network
makes.** This section is the direction for §4.5, §4.6 and §4.8; none of it is built except the Sensors upgrade and,
behind match rules, the capacitors, island balance, local generators, per-machine upgrades and turrets.

**Three things to track, not four**

| | What it is | What it limits |
|---|---|---|
| Heat | the tank's budget, and the ground's | the tank's band around 0 (§4.7); the base through its heat store and radiator |
| Charge | the network's budget | machines, the base shield, respawn speed, turrets |
| Emberite (ore) | what is spent and burned | machines and upgrades (§6.4); fuel for charge and for the tank's reserve |

They cross only here, so the ledger stays simple: heat to charge in engines and generators (with a loss); charge to
heat in every machine action, idle draw and throughput loss; ore to charge when burned (a booked source); tank
actions to heat (created from outside, as now); and carried ore warms the tank.

**Relays are the only node; the pylon is dropped.** A pylon shares everything with a relay (links, coverage, cooling,
drop-off, sight, keeping tunnels open) except charging harvester batteries, and a harvester only works inside linked
coverage anyway. Every camp ended up as "relay plus pylon", which is one decision on two keys, and nobody extends a
network with the 10-ore pylon when the 4-ore relay does it. So the relay becomes one circle (pumping and coverage the
same radius, strongest at the centre) and a harvester draws charge from the network while covered, with no refuel
trips. Removal follows the checklist in `AGENTS.md`; not started. The relay's name collides with the multiplayer
relay server, so renaming it is open but can wait. Sight stays wider than coverage on purpose (§4.8 Sensors).

**The energy network**

- **Capacitors.** Every machine has a capacity, a transfer rate, a priority tier and a small idle draw (relays
  included). The base store is the base's capacitor. The harvester battery becomes this trait.
- **Islands.** Each connected group balances once per tick: producers fill the pool, consumers and capacitors draw
  from it by tier, capped by their rates. Tiers are fixed by machine type: base defence, then turrets, then economy.
  A shortage starves the lowest tier first.
- **Cut off.** A machine that loses the network keeps its charge and drains it by its idle draw and use, so a remote
  harvester runs for a while and then stops, unless its island has a producer. A turret is stricter: it holds fire
  as soon as it is cut off from every island (see Turrets below).
- **Cost.** A machine at rest costs nothing per tick: levels move linearly between changes, and an island is
  recomputed only when links or demand change.
- **Ledger.** Stored charge in every capacitor counts toward the balance, and spent charge becomes heat where it was
  spent. A guard test: a closed loop of powered machines on ambient ground never gains charge.
- *Built behind a match rule (`MatchRules.EnergyNetwork`, off by default).* Same-side live links join relays, pylons
  and generators to their base or to a local generator island. A group without a producer or a base drains from
  its own capacitors. Harvesters can recharge in sight of a local island but deliver ore only through base coverage.
  Relays can be planted with a link to a local island. Relays, pylons, harvesters and generators use the economy
  tier and turrets the turrets tier; nothing is in the base defence tier yet. Pylons hand out nothing and harvesters make no refuel trips. A relay still
  pumps while its capacitor is empty if it has a route home (§4.5). Idle draw reaches the ground as heat when a
  capacitor is settled, at most every 5 s.
- **Seeing it.** With the rule on, every machine in sight near the tank carries a small label: charge over capacity
  (`2.4k/3.6k`), charge a second in and out (`+120 -40/s`) and a thin fill bar, green while its island feeds it,
  amber while it runs on its own charge, red once it is empty and cut off. Labels fade out between 48 and 112 cells
  from the tank and when the view is zoomed far out, and G hides or shows them (on by default). The base panel adds
  a row: what the engine and linked generators make against what the island draws a second, and the net.
  A producing generator's violet label shows charge made per second. The overlay only reads the
  network (`World.MachinePowerOf`, `World.BasePowerOf`). Local matches turn the rule on with `--energy-network` on
  the desktop or `?energy` in the browser; network matches keep the rules their room agreed on.
- **Throughput.** Each relay has a transfer limit and a chain is as fast as its slowest node. Charge passing through
  loses a few percent as heat at the relay; a hot relay throttles and then takes damage. The Conduit upgrade
  raises the limit (about x1.5, then x2) and is capped.
- **Conductors sit alongside relays** (this answers the open question in §14): relays are the nodes, a conductor
  line is a trunk that carries heat, and Conduit carries charge.

**Local generator** (a new machine). Heat only at first. It converts the temperature difference to its own
surroundings (efficiency capped near 0.35, under the base engine's 0.5), has a hard output cap, and dumps its waste
heat into the ground around it, so output falls as the ground warms and generators packed together choke each other.
The tank's torch and shots create heat from outside the system, so the cap and the gradient rule keep that from
being free power. Emberite fuel (fed from the tank's hold) comes after the heat numbers are tuned. A forward
radiator (later, expensive) could serve as a generator's cold sink and restart the hop falloff.

*Built with the energy rule:* Y builds the generator for 18 ore. Plant it near hot ground. Its intake is outside
its 3-cell exhaust radius and within 6 cells. Each tick it draws up to 220 heat and produces up to 65 charge
(1,560/s), returning the rest to the exhaust. It deposits charge in the base store when linked, and otherwise
keeps it in a 4,800-charge reserve that its island can draw. The reserve also catches output when the base store
is full. It travels with the generator if a link is cut and becomes heat if the generator is destroyed. The
generator's own capacitor is separate and follows the same idle and top-up rules as other machines. A local
island supplies charge. Cooling tanks and delivering ore still require base coverage.

**Dark base and shield**

- **Dark** is an empty base store. It ends by itself once income beats drain (with a threshold, so it does not flicker).
  While dark: no shield, no regen or tank heal, turrets off, harvesters strand, vision shrinks, respawn is slowest
  (about 15 s, against 3 s), and the base takes extra damage.
- **Shield.** It blocks a share of every hit, about 80% at a full store, falling smoothly with charge to 0. It never
  blocks a hit entirely.
- **Restart by hand.** Docking while dark empties the tank's hold (never the base ore store) into the engine until the
  store passes the threshold (about 15 ore, a pocket). The shield then rebuilds over about 30 s, which is the window
  in which the base is still very exposed.
- **The fall of a base.** Cut the supply (relays, generators, links), the store drains, the shield thins, then break
  the base. Only a destroyed base ends a side; there are no lives. Relays cost 4 ore, so a cut has to cost the
  defender time (relink and warm-up, dearer links), or rebuilding makes attacks pointless. Tune in play.

**Death and exploring**

- **Death is cheap.** Unlimited respawns; the cost is the cargo, which stays on the ground (within 6 cells) to be
  fetched. A starved base respawns more slowly. A stranded player may give up and respawn at once.
- **The reserve is the off-network clock (§4.6), not a second meter.** Burning a carried unit of emberite buys reserve
  time and takes its warmth (0.1 degrees a second a unit) out of the tank, so a big load is range and risk together.
- **Running out is a state, not a cliff:** warning, then degraded (slower dig, shorter vision, weaker shots), then
  stranded (crawl and dig only), then heat death as today. Each has a counter. Relays and generators push the safe
  zone out, and far lumps pay for the risk (§6.3).

**Machines and upgrades**

- **Per-machine upgrades**, two levels, bought next to the machine with a key; the nearest machine in reach is
  highlighted (amber when affordable, dim when not) and shows level pips. A relay chooses Tap (more intake and
  charge, hotter), Sensor (more sight) or Conduit (throughput and a bigger capacitor).
  *Built behind a match rule (`MatchRules.MachineUpgrades`, off by default; local matches turn it on with the energy
  rule, `--energy-network` or `?energy`).* F beside one of the tank's own machines opens its panel (on touch, a tap
  on the lit machine); while it is open, 1-3 buy a path for the nearest own machine within 12 cells (the earliest
  built on a tie), paid from the base ore store: 8 ore for level 1, 16 for level 2. The first purchase fixes the
  path. Tap pumps 50% more heat a level and spends 1.5 more charge a tick standing, which becomes heat at the relay;
  Sensor adds 10 cells of sight a level; Conduit makes the capacitor 1.5 then 2 times its size and needs the energy
  rule (its throughput effect comes with the throughput limits). The panel closes on F, out of reach, or after a
  purchase. The relay and the turret have paths (the turret's below); the input is bit 30 with the upgrade key
  (protocol 9).
- **Turrets** need power, make heat when firing, and fire only at enemy tanks in range and line of sight, with a
  stable tie-break. Mobs (§6.2) hunt heat, which makes the pumping relays what a defence is built around.
  *Built with the energy rule:* U builds a turret for 24 ore. It is no link node (it does not extend the network,
  cool tanks or keep tunnels open): like a harvester it joins the island whose coverage it stands in, its base's or
  a local generator's, and it fires only while it is on an island, its base is not dark and its capacitor (2,400,
  topped up at 40 a tick in the turrets tier, idle draw 1 a tick) holds a shot. It looks for a target four times a
  second: the nearest live enemy tank without a respawn shield within 24 cells and in direct line of sight through
  open ground, the first in spawn order on a tie. Vision (fog of war) is output only, so the turret casts its own
  line, as links and coverage do, except that a base wall blocks it: a bullet stops there, though links pass. It fires a bullet (80 damage against a tank shell's 160) every 12 ticks (0.5 s);
  each shot spends 150 charge, which the network turns into heat in the ground beside it, so a lone turret firing
  warms its ground by about 4 degrees a shot. Above 60 degrees it fires more slowly, up to 4 times its interval at
  140 degrees; a relay nearby pumps the heat away. Its upgrades: **Rapid** (interval x0.7, then x0.5; each shot
  spends x1.2, then x1.4) and **Long** (+8 cells of range a level, damage x1.5, then x2; interval x1.25, then
  x1.5). Shots are `StepEventKind.TurretFired`. **Bots do not build turrets** (they build no machines at all), so
  turrets are a player's tool for now; a turret fires at bot tanks like any other enemy. The fifth build key is
  bit 31 of the encoded input, the last free bit (protocol 10). Balance is untuned: a turret beside a relay may
  still make raiding too hard; its cost, range and the link it needs are the levers.
- **Build keys.** E relay, R harvester, T pylon, Y generator and U turret (the last two in energy matches).

**Build order.** Capacitors, island balance and the local generator are implemented behind the energy rule.
Dark state, shield and no lives, per-machine upgrades and turrets follow behind their rules. Next come throughput
limits and pylon removal.

### 4.10 Challenges of a finite world *(Proposed; open)*

The simulation uses finite bodies (§4.1). These are the remaining design questions and the checks so far:

1. **The match's energy budget.** The pins were the only supply that never ran out. Now a match has a stock of heat
   in its rock, pockets and lumps, plus what tank actions inject. Hot rock starts at 100 °C and holds 128 energy per
   degree per cell, giving the largest core up to 2.52 million energy before generation cools it. How long a typical
   sector needs that stock to last still needs playtesting.
2. **Relays drain sources.** A relay takes finite heat from a pocket and the surrounding ground and air. A drained
   pocket cools to ambient without refilling from a pin.
3. **Thermal constraints.** The stock and its reach determine how much heat can be collected. The shared radiator
   limits how fast the base can dispose of the portion that does not become charge.
4. **Cost on a large map.** The solver still steps only changing tiles and the tiles exchanging heat with them.
   A 1500 × 750 standard map (seed 8181, two idle minutes) averaged 712 stepped tiles out of 1,128, up from 711
   before the change. See `TODO-gameplay.md` for the probe and the remaining load cases to measure.
5. **Halos.** Outside loss limits the halo and finite pockets fade below the radar heat threshold as they cool.
6. **Tuning is a stock problem.** A starting temperature no longer guarantees danger; danger is stored heat over time.
   Hot rock, lumps, frost rock and the radiator still need to be tuned together in play.
7. **Determinism, saves and multiplayer.** The stock lives in the existing temperature and reactor fields, which
   are already part of snapshots and sync hashes. The same seed gives the same starting stock. Simulation
   fingerprints change with the new rules.
8. **The ledger's baseline.** `SeedHeatedPixel` books the starting temperature and material change at generation,
   and the ledger also accounts for the heat exchanged with ambient during prewarming.
9. **Sectors freeze.** Preserve the stored heat in the frozen sector's snapshot (§7.3). Sector persistence is still proposed.
10. **Visuals and the radar.** Glow, frost tint and the radar's heat thresholds (`Radar.HeatThreshold`) already follow
    temperature. Plain emberite has its own ore return when it is below the heat threshold.
11. **The base interior.** The base's ordinary cells retain and exchange heat. Area cooling transfers it to the
    reservoir, and the radiator accounts for what leaves the simulation.

An opportunity: quenchable hot rock (§6.1) already wants rock that cools. Finite bodies make it consistent.

### 4.11 Open points for 4.9

- How long a rebuilt relay takes to relink and warm up, and what a cut should cost the defender.
- Whether priority tiers ever become player-set.
- The ore-to-charge rate (about 5 to 10k charge a unit is a guess) and the restart threshold.
- Whether a generator ever burns fuel in v1.


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
- **Death is cheap.** The tank respawns at base. The player loses the cargo and the time, not the progress. Today a
  tank has `Tweaks.Tank.MaxLives` (3) lives and a side with none left is out. *Direction (Proposed, §4.9):* no lives;
  respawns are unlimited, a starved base respawns more slowly, and only a destroyed base ends a side.
- **Bodies are solid.** A tank cannot drive through another tank or through another side's planted machines: it
  shoots them out of the way or goes around. Its own machines never block it, so a relay planted in a tunnel does
  not close the way home. Bodies that already overlap (a respawn onto an intruder) can part, and cannot pass through
  one another on the way.
- **The bot plays by the same rules.** It sees the whole of its hull, so it does not wedge in tunnels a little
  narrower than itself. It leaves its base through a door, goes home when it is hurt, hot or after a kill, by the
  way it came, and fires only aimed shots in an enemy's sight, and out of it only to dig itself through. A way home that stays shut (a door
  sealed by hot rock) is given up after a while, and the bot goes back to roaming.

### 5.3 What the tank needs

- Cargo capacity, as an upgradeable stat (in: the cargo hold upgrade, §4.8).
- A placement interface for structures. This is real work: the default match is 320×200 cells, and the game builds with single key
  presses today.

### 5.4 The screen *(Decided)*

The tank is what the player watches, so the screen is built around it.

- **The tank stays in the middle.** The camera keeps the tank centred everywhere, running past the edge of the map
  when it has to, so the tank never ends up under a panel at the edge of the screen.
- **The middle stays clear.** The bottom HUD is split into the two bottom corners: TEMP, SHIELD and MATERIALS on
  the left, BUILD STRUCTURES on the right. The gap between them is play area, and the mouse aims there. On a
  narrow screen the HUD shrinks before the gap closes. Panels that appear while docked, such as the upgrade list,
  sit to the side of the tank, clear of the base's entrances above and below it.
- **Tunnels read as ground.** Dug-out ground is a dim floor tinted by the rock it was cut through and darker
  towards the walls, not a black void. It stays quiet so tanks, shots and ore stand out on it.

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

**Emberite (Prototype).** The first mineral is an exotic ore, emberite, the only thing machines and upgrades are
made of (§6.4); dirt is worth nothing. Every ore cell glows and gives heat: plain ore starts at 9
degrees (a speck is a faint warm freckle, a pocket a warm patch), and the core of a lump, about a tenth of its cells,
starts at 600 degrees and holds about six times the heat per degree of stone, so each core cell is seeded with
9,600 degree-cells of energy. The ledger records that starting stock at generation. It spreads into the shell and
the surrounding ground as the core cools. Bigger lumps have more core cells, so they hold more heat and keep
the ground around them dangerous for longer. A map of 1500x750 holds about 2,500 units (a plain cell is 1, a core
cell 3), in three kinds of deposit, scaled with map area:

- specks of 1 to 3 cells scattered through the dirt, near the bases too (about 15% of the ore);
- pockets of 12 to 30 cells at some distance from the bases (about 35%);
- a few lumps of 80 to 150 cells with a warm core, far from the bases and biased toward the centre (about 50%).

On a smaller map the minimum distances from the bases and between lumps shrink with the square root of its area (to
no less than 0.4 of them), and every map gets at least three pockets and one lump when one fits, so the desktop's
default 320x200 match has a few hundred units rather than a few dozen. 1500x750 is unchanged.

Ore blocks movement; it is drawn as gold crystal that glows from within, with a soft gold halo on the ground
round every cell, and the cores brighter still.

**Cracking a lump.** A lump is the big prize, and it takes the tank, and a tank that digs into one has to be quick:
a fresh lump's shell is over 100 degrees for about the first 25 seconds and its core heat then drains into the ground and
the air of any tunnel beside it. A tank parked in a bay cut into a large lump (150 cells) stays above 100 degrees for
about 17 seconds and above 60 for the whole minute; beside a small lump (80 cells) for about 9 and 49 seconds. A harvester
at the lump's edge senses over 30 degrees and stalls (its limit) for about the first minute, and the radar reads the
whole lump as heat while it lasts. Harvesters never mine core cells; only the tank's torch does. The lump cools by itself,
and a relay beside it cools it faster, since the heat is finite; once it is cool, harvesters clear the shell, a
cell about every second (about a minute for a hundred cells, more counting trips to a pylon). A relay planted right
against an intact lump pumps enough heat that harvesters can already work its outer shell, stalling only within a
few cells of the core; the core itself still waits for the tank. *(Tuning open: whether an intact core should hold
its shell against a relay too.)*

The tank's ordinary dig clears plain ore, slower than dirt; a core cell needs the torch, like rock. A plain cell gives
1 unit and a core cell 3, into a small hold of 40 units. A full hold leaves ore in the ground (a core cell is taken only
if all 3 units fit), so mining never destroys ore. Docking at the own base banks the hold into the base's ore store
quickly (10 units a tick); inside the own network's coverage it unloads there too, slower (§6.4). The hold grows
with the cargo hold upgrade (§4.8). In the hold, each unit of emberite warms the tank by 0.1 degrees per second
before cooling. A full starting hold adds 4 degrees per second, and each unit unloaded or spent reduces that rate.
When the tank dies its hold spills back into the ground as plain ore in the nearest open or dirt cells around the wreck,
never under another tank or a machine (ore blocks links, so it would cut a relay); what does not fit within a few cells
is lost. Torching rock no longer yields anything. The MATERIALS panel shows a pile of Emberite growing inside the
hold and the exact ore count against capacity. Beneath the hold window it shows the base's ore store or, while the
hold empties, a blinking UNLOADING with down arrows. The base panel also shows the store.

### 6.4 Structures and the ore economy *(Prototype)*

Existing: harvester, relay, pylon. Planned: conductor lines, insulating walls, haulers, turrets, props, heat pumps,
generators. *Direction (Proposed, §4.9):* the pylon is dropped, and the generator and the turret are the next
machines.

**Ore is the only currency.** Digging dirt only clears it. A relay costs 4 ore, a pylon 10, a harvester 30; the
base store starts with 30. The tank's hold is small (40), so getting ore home is the work, and the network is how:

- **Who pays.** Docked at the own base, or inside the coverage of the own linked network (§4.6), the base store pays;
  if it is short, the hold does. Outside the network only the hold counts. The build panel shows each machine's ore
  cost and the stock that would pay (BASE or HOLD), or how far the tank is from affording it; a refused press says
  "not enough ore" apart from "would not link".
- **Unloading through the network.** Inside linked coverage the hold unloads into the base store at 4 units a
  second, a unit at a time, while the store has room; docking is faster. Relays and pylons are therefore also
  drop-off points: planting one near a dig site saves the trip home.
- **Harvesters mine ore.** A harvester works the plain ore within 4 cells of itself (6 actions a cell, every 150 ms), crawls
  along the shortest path to plain ore in its work area that it can get within reach of, clearing the dirt in its
  way, and pays charge per action as before. Rock, concrete and ore itself block its 5x5 body, so ore sealed in
  rock is out of its reach: it passes that over for ore it can get to, and with none says it is out of ore.
  It leaves a lump's core to the tank (§6.3), and the ore round the core while it is too hot to work (45 degrees):
  it goes for other ore instead, and with only that left it idles and the base panel says so ("at a warm core:
  torch it out with the tank"). It crawls about 6.7 cells a second. Off the network, or on ground too hot to work, it
  does not work but still crawls on toward ore, so a tunnel that bends out of its relay's sight does not leave it
  frozen. Its ore reaches the base only through the network, so it works only while it
  stands in the own linked coverage. With no ore it can reach, or with the store full (the panel says "waiting: the
  base ore store is full"), it idles and spends nothing.
- **The bot** does not prospect yet. As a stand-in, a base whose side the computer plays gets 0.5 ore a second.
  The bot builds and buys nothing today, so this only keeps its store from being empty once it does.

### 6.5 The rival tank *(Proposed)*

The opposing tank stops being the centre of the game. It becomes one possible sector twist: a competitor for the
same deposits.

### 6.6 Fog of war and radar *(Prototype)*

The player sees only what their side sees, so that learning the shape of the world takes travelling through it.

- **Three kinds of ground.** *In sight*: shown live. *Remembered*: seen before, shown as it was last seen, drained
  toward a cool blue-grey and dimmed, with no entities and no live heat. *Unknown*: never seen, black.
- **What sees.** The tank sees along open tunnel (about 45 cells) and a little through solid ground around itself.
  The base sees along open ground over its coverage circle (plus the Sensors upgrade, §4.8). A relay or pylon sees its
  whole coverage circle and 10 cells beyond it, plus the Sensors upgrade, through the ground, whether or not it is
  linked; every other machine sees a few cells around itself. The network is
  therefore the player's eyes as well as its cooling line (§4.6): a place stays lit, with its live heat, only while
  something of yours stands there. The edge of sight is a gradient, never a hard line.
- **Radar** *(Prototype)*. Every 4 seconds the tank sends a pulse: a faint ring that runs outward from where the
  tank stood, taking about 3 seconds to reach its full range of 150 cells, well past sight. Dirt and open ground let
  it through; rock, hot and frost rock, emberite, another side's base walls, machines and tanks reflect it and stop it.
  Each return is a single blip at the first cell that reflected, flaring the moment the ring reaches it (near
  things first) and fading over 10 seconds: green for rock, amber for metal, a small gold diamond with a pale heart
  for emberite, and a white-violet dot in a ring for something alive (today: the enemy tank; mobs later). What lies behind a reflector is in its
  shadow, so the outline of a thing is only learned by pinging it from several sides, and successive pulses hit
  slightly different points, so a shape fills in while you wait. When a later pulse passes an old blip it checks it
  again: a blip of an enemy that has driven off, or of ground that has changed, goes out; one hidden in a new
  shadow stays until it fades. A tunnel or cave behind dirt gives a weak blue return and lets the pulse on. The
  Different things show from different distances: a large rock formation from the pulse's full 150 cells, a thin
  wall only from about 60, machines from 75, a tank from 60, a cave from 50, emberite from 40 for a
  speck up to 110 for a deep deposit (counting only cool plain ore behind the face), and hot ground from 50 to 120 by
  how hot it is. The radar upgrade (§4.8) lengthens all of these and speeds the ring up to match, so a longer radar
  still pings every 4 seconds. A lump never shows as ore from afar: its warm core and the ground it heats return heat,
  and what it holds is found by going there. Beyond its range a thing still casts its shadow but leaves no blip, so the far picture is
  only the big shapes and the detail is a surprise kept for when you get close. The
  radar reports only news: nothing in sight, nothing in remembered ground unless it has changed since, and never
  your own machines. A blip goes the moment you see its cell.
- **Heat blinds the radar** *(Prototype)*. Hot ground absorbs the pulse: every hot cell a ray crosses takes some of
  it, more the hotter it is, and shows as a red dot whose opacity rises steeply with the heat, so it warns of danger: a trace for warm ground,
  nearly solid for hot rock. A ray has a fixed budget of heat it can cross (250 degree-cells: about 3 cells of hot rock, 25 cells of
  ground at 20 degrees), so the radar reaches less far the hotter the surroundings. The pulse gets through many warm
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

A frozen sector's snapshot holds its temperatures and energy stores until the simulation resumes.

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

On `master` (2026-11-17) the game has:

- An energy-exact heat simulation over terrain and air fields, with a ledger of every source and sink.
- A base with a finite reservoir and a radiator, and base and tank upgrades bought from its ore store (§4.8).
- Relays that pump heat from a radius into the base when linked by direct line of sight through open tunnel.
- The power loop (§4.5): the base engine turns relay heat into charge; pylons hand it to harvester batteries.
- Emberite ore as the only currency (§6.3, §6.4): specks, pockets and warm-cored lumps; a small tank hold that
  unloads at the base or through the network; machines paid for in ore.
- Harvesters that crawl to the plain ore near where they were planted and mine it into the base store through the
  network, refuelling at pylons; a relay keeps them cool, and a lump's core is left to the tank's torch.
- Network coverage as the tank's leash (§4.6), and tank temperature in a band around 0 (§4.7): docking settles it
  near 0, cold slows it, both extremes damage it.
- Hot rock pockets as finite heat stores and frost pockets as finite cold stores (a match has 8 and 5), with a
  larger hot core at the centre of the map to tap (§4.8).
- Fog of war and the tank's radar pulse (§6.6).
- Network matches for up to 8 players through a relay server, in deterministic lockstep, with bots in the empty
  slots (`docs/multiplayer-design.md`); locally, one bot opponent.
- A tutorial of next steps and event notifications, synthesised sound, and rendering at the display's own pixel
  density with the screen layout of §5.4.

Gaps between that and this design, using the constants in `Tweaks.cs` on `master` on 2026-11-17:

1. **Work makes little heat.** Digging adds 0.008 heat per pixel and a shot 0.25, against a safe limit of 100.
   What limits a dive today is the off-network clock (§4.6), not the work itself.
2. **Tanks carry little.** A tank's whole safe range is about 2,000 energy.
3. **Relays teleport heat.** There is no line capacity and no route to choose or cut.
4. **Heat leaks outside the loop** (see 4.3).
5. **There is one map, one generator and no world map, save format or sector state.**

The radiator vents 500 energy per tick against a 50,000 reservoir. Two relays delivering their full 300 each can
exceed that rate if too little heat becomes charge. The stock and its reach determine whether they can keep delivering.

## 12. Build order

### 12.1 First prototype

Three changes, to find out whether the step from ring 1 to ring 2 is fun:

1. A temperature gradient toward the centre of the map.
2. Machines that emit heat when they work.
3. A radiator tuned to be the real bottleneck.

Step 3 is in. Steps 1 and 2 are partly in: the map has one hot core at its centre rather than a gradient, and
harvesters' spent charge ends up as heat in the ground (§4.5), but relays and pylons make none of their own.

### 12.2 After that

Each step should be playable without the next.

1. Minerals with mineral build costs (one is in: emberite pays for everything); conductor and insulator materials.
2. Quenchable hot rock.
3. Cargo (in: the tank's ore hold) and a placement interface.
4. Steady-state detection and a sector quota.
5. One mob type and one nest; turrets.
6. World map, sector save, freeze and resume.
7. Heat pumps, structure failure by temperature, generators.

The power loop (§4.5) is in as a prototype: the engine, the charge store, pylons, and harvester batteries with the
return-to-pylon behaviour. Network coverage (§4.6) is in too. Their numbers are starting guesses to tune in play.
The next steps for power, deaths and machines are in §4.9 (capacitors, generator, dark base and shield, per-machine
upgrades, throughput, turrets, then removing the pylon).

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
| Everything useful makes heat; its collection depends on the reachable stock and its disposal shares the radiator | Proposed |
| Power is converted relay heat; machines run on charge and refuel at pylons | Prototype |
| The tank's range is set by network coverage and its heat; no second meter on the tank | Prototype |
| The sector goal is tapping the core; the ore store buys base and tank upgrades | Prototype |
| Emberite ore is the only currency; the network is where the hold unloads and the store pays | Prototype |
| The camera keeps the tank centred; the bottom HUD sits in the corners, leaving the middle clear | Decided |
| Heat-seeking mobs; threat scales with heat rejected | Proposed |
| Quenchable hot rock as the first hazard | Proposed |
| Alerts are caused by the player's actions; headroom buys time | Proposed |
| No skill tree across saves | Proposed |
| Rival tank becomes an optional sector twist | Proposed |
| Fog of war: the player sees what their tank, base and machines see; the rest is remembered or unknown | Prototype |
| Radar: pulses from the tank leave blips where they reflect, well beyond sight | Prototype |
| Hot ground absorbs the radar pulse; the cells it got through show as red dots by heat | Prototype |
| Maps grow well beyond 1500 by 750; a step's cost must follow activity, not map area | Decided |
| The radar finds emberite (a speck from near, a deep deposit from further); a lump shows only as heat | Prototype |
| Harvesters leave lump cores to the tank's torch; a hot core (600 degrees at the start, dangerous to a tank, and finite) keeps harvesters off its lump while its heat lasts | Prototype |
| Gold, a pickup left from the original game, is retired: emberite is the only resource on the map | Decided |
| Heat limits the tank, charge runs base and machines, ore is what is spent and burned (§4.9) | Proposed |
| The relay is the only network node; the pylon and the harvester battery are dropped (§4.9) | Proposed |
| Every machine has a capacitor, a rate, a priority tier and an idle draw; cut-off machines drain (§4.9) | Proposed |
| Local generators convert a temperature difference and dump waste heat locally (§4.9) | Proposed |
| A base's shield scales with charge and never blocks a hit entirely; a dark base restarts by itself or by hand (§4.9) | Proposed |
| No lives: only a destroyed base ends a side; death costs the cargo (§4.9) | Proposed |
| Relay throughput limits are upgradeable and lose heat; a hot relay throttles (§4.9) | Proposed |
| Machines are upgraded individually, next to the machine; turrets are a powered machine (§4.9) | Prototype |
| Turrets are player-only for now: bots build no machines; turrets shoot bot tanks like any enemy (§4.9) | Prototype |
| Sensors upgrade: relays and pylons see 10 cells past coverage, and 8 more per level (the base 8 more per level) | Prototype |
| Every heat body is finite: no pinned temperatures or permanent sources; the outside is a loss, not a cell | Decided |
| Stored heat is the match's supply; what keeps it up over a long match is open (§4.10) | Proposed |

## 14. Open questions

- How large is a sector, and how long should one take?
- What does a sector export, and how does it travel between sectors?
- Do conductor lines replace relays or sit alongside them? (Proposed: alongside, §4.9.)
- How does a heat line fail: by throughput, by temperature, or by being cut?
- How is the output drop under alert calculated?
- What exactly is at the core?
- Is there multiplayer, and if so is it cooperative or competitive?
- Should the bot (and later mobs) be limited to what it could see, or stay all-knowing?
- Should an intact lump core hold its ore against a relay planted beside it, so harvesters truly wait for the tank?
- What makes a step cheap on a very large map: bookkeeping that follows activity, or coarser simulation of ground
  nobody sees?
- With every body finite, what keeps a long match supplied with heat: new sources over time, or a stock that is simply spent (§4.10)?
