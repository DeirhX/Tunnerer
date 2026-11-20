# TODO

Open work, written so an agent can pick up one item without the conversation that produced it. Read `AGENTS.md` first
(commands, the rules tests enforce, the checklists), then `DESIGN.md` §4.9 (the design of record for the energy work, all
*Proposed*) and §4.11 (its open points). Where this file and `DESIGN.md` disagree, `DESIGN.md` wins; fix this file.

## Where things stand

- **Done, in PR #75** (`claude/upgradables-review-8a3ab6`, not yet merged):
  - **Sensors upgrade** (base-track key 5; Cargo hold and Radar moved to keys 6 and 7): +8 cells of sight per level for
    relays, pylons and the base. A relay sees 10 cells past its coverage circle before any upgrade
    (`Tweaks.Vision.NodeSightBonus`). Presentation only.
  - **Tranche 1, capacitors and island balance**, `src/Emberward.Core/Network/EnergyNetwork.cs`, behind
    `MatchRules.EnergyNetwork` (**off by default, including in `MatchRules.Standard`**, so fingerprints and the rules
    hash are unchanged).
  - **Tranche 1b, charge and flow labels** (key G, `PowerLabels`, `HudPalette`), shared by desktop and browser.
    `--energy-network` (desktop) and `?energy` (browser) turn the network on for local matches only.
- **Merged from PR #72:** finite emberite and the associated design notes (§4.1, §4.3, §4.4, §4.7, §4.10, §6.3, §11).
- **Items 1 to 4 are implemented** (item 3 in PR #78, item 4 on `codex/tranche-4-machine-upgrades`, stacked on it).
  Item 6 (turrets) is on `codex/tranche-6-turrets`, cut from item 4's branch. Continue at item 5. **Item 8 is
  implemented** on `codex/tranche-8-reserve`, cut from item 4's branch.

## Rules for every item

- **Do not commit or push** unless the user asks. Leave changes in the working tree.
- **Run before reporting:** `dotnet build Emberward.slnx`, `dotnet test Emberward.slnx` (about 90 s; `--filter
  "Category!=Slow"` while iterating), `dotnet format Emberward.slnx --verify-no-changes`. All must be clean. Keep files at
  CRLF (a tool once flipped two to LF; `dotnet format` catches it).
- **Determinism and energy:** no clocks, `Random.Shared` or unordered collections in Core; `DeterministicMath` instead of
  `Math` transcendentals; any energy created or destroyed is booked in `ThermalLedger`, and `ThermalLedgerTests` must
  balance every tick.
- **Cost follows change:** no pass over every machine or every cell each tick. Use `MachineList.CollectNear`; do not
  loop over `MachineList.Machines` in an action.
- **New switchable systems get a `MatchRules` flag** and cost nothing while it is off. State lives in the system, not on
  `Tank` or `TankBase`, and is in the snapshot (`SnapshotCoverageTests`, `SnapshotRoundTripTests`) and in
  `WorldStateHash`.
- **Changing the simulation under the energy flag, or turning the flag on in `Standard`, changes the fingerprints**
  (`SimulationFingerprintTests`) **and the rules hash.** Say so in the commit message, and plan a protocol version bump
  before network play (`docs/multiplayer-design.md`; grep for the protocol version constant, and see the Starfish protocol
  note in the git log).
- **Desktop asks Core:** add a Core query, do not recompute a rule in the HUD. New HUD elements go through a shared
  view-model (see the last item here).
- **Report:** files changed, what you built, tests added, results, any deviation from `DESIGN.md` §4.9 (and edit §4.9 to
  match), and anything the next item needs to know.

## What tranche 1 left behind (read before items 2 to 7)

- **Capacitor traits** are on `MachineBehavior` (`Capacitor`: capacity, transfer rate per tick, `PowerTier` of
  BaseDefence / Turrets / Economy, idle draw per tick). The old `BatteryCapacity` is derived from it; a machine marked
  `RunsOnCharge` (only the harvester) keeps its capacitor as the battery when the flag is off.
- **Numbers (in `Tweaks.Machine` and `Tweaks.Energy`):** relay 3600 capacity, 60/tick in, 1.5/tick idle; pylon 6000, 100,
  2.5; harvester 120k, 400 in, 4 idle. A cut-off relay or pylon lasts about 100 s.
- **Islands.** The energy network regroups when `LinkMap.PowerVersion` changes. A node whose power route ends at a
  base (`LinkPoint.IsPoweredBy`) uses that base's island, the same rule harvester coverage uses. A disconnected component pools charge if it has a generator. The first generator
  names that island and each producer carries its own reserve across splits. A disconnected group without a base
  or generator drains separately. Item 5 can reuse these memberships for the throughput tree.
- **Per tick, per island, tier by tier:** pay members' idle draw from generator reserves and the base store, then top up non-full capacitors by
  at most their rate. A tier the store cannot pay is starved and fed again once the store holds 24 ticks of its idle
  draw. Everything is in the economy tier today.
- **Settling:** a capacitor is brought up to date only when used, when it changes island or tier, or every 5 s, and idle
  draw reaches the ground as heat then. `EnergyNetwork.StoredCharge` counts idle charge yet to settle and generator reserves in
  `World.SumSystemCharge`. **Anything that changes `Machine.Charge` or `ChargeCapacity` must go through
  `EnergyNetwork.Spend` / `Settle`.** A fed capacitor refills only after a spend or an island or tier change.
- **Failing gently:** a relay still pumps heat with an empty capacitor; only the harvester stops when empty.
- **Output-only queries for the HUD:** `World.MachinePowerOf(machine)` (charge, capacity, in and out per second, fed) and
  `World.BasePowerOf(base)` (store, capacity, production, island draw). They return null while the flag is off.
  `PowerLabels.Of` holds all label wording and colour rules; extend it when a new state appears.
- **Known gaps:** "charge passed on to others" became real with item 5 (`MachinePower.PassedPerSecond`). Item 6's
  turrets are the second tier, and `TurretTests` reaches the tier-ordering branch.

## 1. Finite heat sources (implemented)

Hot and frost rock start at 100 °C and −60 °C, respectively, without a pin. Relays draw from finite hot rock. The base
walls and core store heat like ordinary terrain, and the reservoir receives the base's area cooling. The engine's
cold side is the reactor, which warms as it fills. The radiator vents up to its capacity, including fractional energy.
See `TODO-gameplay.md` for the remaining tuning and sector persistence questions and `DESIGN.md` §4.10 for the design.

The simulation fingerprints changed after PR #72 was included. Mention that when committing this item. Network peers
need the same build. The energy network's flag and capacitor tuning are unchanged.

## 2. Local generator (implemented)

In energy matches Y builds a generator for 18 ore. Heat over the exhaust temperature is drawn from the intake
and converted at up to 35% efficiency and 65 charge a tick. Waste heat returns to the exhaust. A generator can
supply a local island without a base. Each generator carries a 4,800-charge reserve that its island can draw, and
connected generators fill the base store first. The charge overlay shows output at each generator and includes
connected output in the base row.

The fourth build key uses input bit 28 and advances the relay protocol to 7. Deploy that relay version before
network clients use this build. The opt-in rules fingerprint changed. Standard matches keep their recorded
simulation fingerprints. Heat stock, layout and sustained output still need playtesting. Emberite fuel remains
a later item.

## 3. Dark base, shield, no lives

**Dark.** The base store is empty. It ends by itself once income beats drain, with a restart threshold so it does not
flicker (use the latching pattern of the existing warnings). While dark: no shield, no base regen
(`Tweaks.Base.HealthRegen`) and no docked-tank heal, turrets off, harvesters strand, vision shrinks, respawn slowest
(about 15 s against 3 s, scaled by how little charge there is), and the base takes extra damage.

**Shield.** Blocks a share of every hit: about 80% at a full store, falling smoothly with charge to 0. It never blocks a
hit entirely. Apply it where the base takes damage; find that path first (see below).

**Restart by hand.** Docking while dark empties the tank's hold, never the base ore store, into the engine until the
store passes the threshold (about 15 ore, a pocket). It is automatic, so no new input. Ore is booked as a ledger source
(one new line). The ore-to-charge rate (about 5 to 10k charge a unit) is a guess. After a restart the shield rebuilds over
about 30 s, the window in which the base is still very exposed.

**No lives.** Unlimited respawns; death costs the cargo, which stays on the ground (`DeathDropRadius`) to be fetched.
Remove `Tweaks.Tank.MaxLives`, `Tank.LivesLeft` (and its `WorldStateHash` and snapshot entries),
`TankLifecycle`'s life counting, `MatchReferee` / `MatchOutcome` (`IsOutOfLives`, `LastTankStanding`) and the HUD lives
display (`git grep -n -i "lives\|IsOutOfLives\|MaxLives"`; hits include `Tank.cs`, `OreDrop.cs`, `MatchOverlay.cs`,
`SessionOverlay.cs`, `Net/LateJoin.cs`, `MatchOutcomeTests.cs`, `OreMiningTests.cs`). A stranded player may give up and respawn at once.

**Only a destroyed base ends a side.** Base health is 2000 (`Tweaks.Base`) and regenerates, but **I did not find what ends
the match when it reaches zero, nor how shots reach it.** Trace that first; the path may need building. The tapped core
still wins (`MatchWinReason.TappedTheCore`); replace `LastTankStanding` with last base standing. Check the bots, which
must follow the same rules.

**Also.** Turn the energy flag on by default here if it is ready (this changes the fingerprints and the rules hash: bump
the protocol before network play). Update `PowerLabels` (a dark base colour) and `BasePower` (dark state, warm-up).
Tests: dark entry and exit with hysteresis, shield value at several charges, restart by hold, match end by base
destruction, a snapshot taken while dark.

## 4. Per-machine upgrades (implemented)

Built behind `MatchRules.MachineUpgrades` (off by default; local matches turn it on with `--energy-network` or
`?energy`), in `MachineUpgradeSystem`. F (or a tap on the lit machine) opens the panel of the nearest own relay within
12 cells; 1-3 then buy Tap, Sensor or Conduit for 8 and 16 ore from the base store. The input is bit 30 of the encoded
input, so the protocol is 9: deploy that relay version before network clients use this build. Standard and
energy-rule fingerprints are unchanged; the opt-in rules hash changes only with the new flag on. The highlight and
panel are one view-model (`Gui/MachineUpgradeView.cs`, shared through `Emberward.Rendering`) drawn with ImGui and in
the DOM. **For item 5:** Conduit only grows the capacitor so far; its throughput multiplier belongs with the
throughput limits (read the Conduit level with `World.MachineUpgradeOf`). Not yet seen in a browser.

**Design.** Two levels per machine, bought next to the machine with a key; the cost comes from the base store ore (so
harvesters still feed everything). The nearest machine in reach is highlighted (amber when affordable, dim when not,
nothing when maxed or out of reach) and shows level pips; a panel anchored to it shows level, choices, cost and effect.
Closing: moving out of reach, pressing the key again, or buying. A relay chooses **Tap** (more intake and charge, hotter),
**Sensor** (more sight) or **Conduit** (throughput and a bigger capacitor; its throughput effect arrives with item 5).
Global upgrades (`BaseUpgrades`) stay as the baseline.

**Needs.**
- **Its own system with a `MatchRules` flag**, keyed by machine; `World` answers for it; in the snapshot and the hash.
- **A new input bit.** `ControllerOutputCodec` has no bit for "upgrade the machine next to me" (bits 0 to 3 move, 4 to 7
  shoot and build, 8 to 10 the global upgrade key, 11 aim present, 12 and up aim index). Add one, and bump the protocol.
  Mirror the key in `KeyboardController`, `BrowserApi`, `input.js`, `README` controls, the F1 controls screen and the
  tutorial.
- **Effects** go through the existing paths: charge through `EnergyNetwork`, heat through the heat writers (wake the
  tile), sight through `SideSensing` (a `VisionSource` per machine already exists; a level change recasts that one eye).
- **Highlight and panel** as a shared view-model (see the last item) drawn on desktop (ImGui) and browser (DOM; tap a lit
  machine on touch). On desktop, click may conflict with shooting; prefer a key and add click only if it does not.
- **Tests:** buying, refusal (maxed, not enough ore, out of reach), nearest-machine choice with a stable tie-break,
  snapshot round trip, each relay path's effect.

## 5. Relay throughput limits and the Conduit upgrade (implemented)

Built behind `MatchRules.EnergyNetwork`, in `Network/EnergyNetwork.Throughput.cs`; numbers in `Tweaks.Throughput`
(`Config/Tweaks.Throughput.cs`). Each island is a tree: a node's parent is the next node of its island up
`LinkPoint.UpstreamLink` (the route `IsPoweredBy` follows), or the node a local island's search reached it from; a
harvester hangs off the node that covers it (`MachineContext.CoverageOf` now returns the `TankCoverage`, with its
point). Every link node passes on at most 400 a tick (Conduit 600, 800; ceiling 800), its own top-up not counted. The
tree is worked out only when the island's filling set, members, links or a node's limit change, and only over the
filling capacitors and their ancestors. Each node loses 3% of what it passes on into its pending charge, which settling
puts in the ground at the node (heat writers, ledger `RecordFromCharge`). At its settling a node that ran at its limit
(saturated: asked for all it can pass on, at its last tree) is throttled from 150 °C (half; recovers under 140 °C) and
overheated from 250 °C (a quarter, and 100 of its 1000 health each check it ran at its limit, recorded as
`MachineDamaged` / `MachineWrecked`); a hot node stays throttled until its ground cools, and a cool node below its
limit is not looked at, so hot ground alone never harms a relay that is not saturated (decided: a lightly loaded relay
beside a lump core is safe; a saturated one there throttles and can be wrecked).
`MachinePower` gained `PassedPerSecond`, `LimitPerSecond` and `Heat`; `PowerLabels` shows `>passed/s` and the new
`HudPalette.Throttled` / `Overheated` colours (a `>` glyph was added to `PowerLabelQuads`). Standard fingerprints are
unchanged (no recorded fingerprint runs the energy rule); energy-rule play changes (chains now lose 3% a hop and can be
limited), with no new flag, so the rules hash is the same: **bump the protocol before network play** (item 6 bumps it
anyway). Not changed: idle draw is still paid at the root, not through the tree; generator output reaches the island's
store directly; the limits apply to top-ups, not to idle draw. Tune the limit, loss and temperatures in play (a node in
open air with nothing pumping warms fast: air holds 0.8 a degree a cell).

**Design (as planned).** Each relay has a charge-transfer limit; a chain is as fast as its slowest node. Compute a tree from the
producers (reuse `LinkMap`'s power search; recompute only when links or demand change): demand is added up from the
leaves, each node caps what passes through it, supply is handed back down by tier. Charge passing through loses a few
percent as heat **at the relay**, booked through the heat writers so the ledger balances; a hot relay throttles, and
takes damage further up. The Conduit upgrade (item 4) multiplies the limit about x1.5 then x2, with a hard ceiling.
Conductors (`DESIGN.md` §4.2) sit alongside relays: a conductor is a heat trunk, Conduit carries charge.

**Watch for.** A machine at rest must still cost nothing per tick. "Charge passed on" in `MachinePower` becomes real here
(update the queries and `PowerLabels`: a throttled and an overheated relay colour). Tests: a slow node limits a chain; an
upgrade on one relay changes nothing until the whole trunk is upgraded; the loss heats the relay and the ledger
balances; throttling above the temperature.

## 6. Turrets (implemented)

In energy matches U builds a turret for 24 ore (`TurretAction`, `Tweaks.Turret` in `Config/Tweaks.Turrets.cs`). It is
no link node: it joins the island whose coverage it stands in, as a harvester does, and fires only while it is on an
island, its base is not dark and its capacitor (2,400, the Turrets tier) holds a shot. Four times a second it looks
for the nearest live, unshielded enemy tank within 24 cells in line of sight (its own raycast: `VisionMap` is output
only), the first in spawn order on a tie, and fires a bullet of 80 damage every 12 ticks. A shot spends 150 charge
through `EnergyNetwork.Spend`, which puts it as heat beside the turret; above 60 degrees it slows, up to 4 times its
interval at 140. Shots are `StepEventKind.TurretFired` (sound `TurretFire`). Upgrades: Rapid (faster, dearer shots)
and Long (`MachineUpgradePath.LongRange`: range, damage, slower). Bots build no machines, so turrets are
player-only; they shoot bot tanks. The fifth build key is bit 31, the last free bit of the encoded input
(protocol 10; deploy that relay version first). The energy rules' version in the rules hash changed; Standard and
energy-rule simulation fingerprints are unchanged (no fingerprint scenario builds a turret). The tier-ordering test
is `TurretTests.AStoreThatCannotPayEveryTier_FeedsTurretsBeforeTheEconomy`. **Not tuned in play:** cost, range,
heat per shot and the overheat curve; a turret beside a relay may still stop raiding. Not yet seen in a browser.

## 7. Remove the pylon and the harvester battery

**Why (`DESIGN.md` §4.9).** The pylon shares everything with a relay except charging harvester batteries, and a harvester
only works inside linked coverage anyway. Every camp ends up as "relay plus pylon". After this the relay is the only
network node, and it is **one circle** for pumping and coverage (sight stays wider on purpose, Sensors).

**How.** Follow "Removing a system" in `AGENTS.md`: `git grep -n -i pylon` over the whole repository (shaders and docs
included), then its types, `Tweaks` constants (`PylonCost`, `PylonRange`, `PylonChargePerAction`, `PylonIntervalMs`, the
colour), step events, the tutorial's pylon step, sounds, the sprite, HUD hints, the `MatchRules` path that keeps the old
refuel behaviour, and the harvester's return-to-pylon trips and `HarvesterResumeAbove`. The harvester draws charge from
the network through its capacitor (tranche 1 already does this with the flag on). Update `SimulationFingerprintTests` and
say so. Rename the relay (for example "tap") is optional: it collides with the multiplayer relay server's name.

## 8. The tank's reserve and exploring (implemented)

Built behind `MatchRules.TankReserve` (off by default, also in `Standard`; local matches turn it on with
`--energy-network` or `?energy`), in `TankReserveSystem` (`Entities/Tanks`), constants in `Config/Tweaks.Reserve.cs`.
`DESIGN.md` §4.9 (Death and exploring) has the rules and numbers. What the next items need to know:

- **The burn is the recall key (X, bit 29); no input bit was added.** There is no hold-dump input in the code. Off the
  network with emberite aboard and the floor at least a unit's worth up, X burns (`TankReserveSystem.CanBurn`) and
  does not recall; recall still works once the hold is empty. `World.RecallOffered` is what the HUD asks.
- **The burn lowers `TankThermals.OffNetworkFloor` and nothing else.** The tank sheds down to the lower floor through
  the ground, so no energy is made or destroyed and the ledger is untouched (`Burns_LeaveTheLedgerBalanced`).
- **Stages** go by `Tank.OffNetworkSecondsToOverheat` (warning 30 s, degraded 15, stranded 5, heat death 0) and are
  settled once a tick in `Tank.Advance`, right after coverage; `TankReserveEffects` masks the gun, tells
  `TankBuildController` whether a press may build a new machine (a carried one plants either way, and the keys stay
  tracked so a held key is no press when the stage lifts), and feeds the move factor, the dig credit (`ReadyToDig`,
  capped at one dig and spent only by a dig that clears ground; stranded has no dig factor, the crawl slows the dig),
  the gun delay and vision (`SideSensing`). A dead tank's reserve is cleared (`TankReserveSystem.Clear`). The
  dig, sight and HUD read the stage `Advance` stored, not the clock, so all agree with that tick's effects.
- **Generators hold the clock** within 20 cells in line of sight (`HoldsClock`, a `CollectNear` per off-network tank),
  and pay for it: 8 charge a tick from the generator's capacitor (`Tweaks.Reserve.GeneratorHoldChargePerTick`), through
  `EnergyNetwork.Settle` and `Spend`, so it becomes heat in the ground and the ledger balances
  (`HoldingTheClock_LeavesTheLedgerBalanced`). The first generator in list order that can pay holds; one that makes
  nothing holds a lone tank only until its capacitor is spent (about 20 s), so a dead generator is no permanent safe
  zone. `EnergyNetwork` itself is unchanged.
- **Deviation:** "weaker shots" is a slower gun (twice the delay), not less damage, to keep out of the projectile code
  item 6 changes. Damage a shot could follow once turrets have landed.
- **HUD:** `ReserveTags` (shared view-model) gives the stage tag beside OFF NETWORK and the burn prompt (shown whenever X would burn, a quiet hint before the
  warning, so X never spends cargo unannounced); desktop draws
  it in `HeatGauge` and `MatchOverlay`, the browser in `strip.js` and the recall button (`BrowserFrame.Reserve`).
- **Not done:** no tutorial or toast for the stages, no browser check in a real WebGL browser, and network matches
  keep `Standard`, so no protocol bump was needed.

## 9. Smaller follow-ups

- **Emberite fuel for generators** (after item 2's numbers are tuned): fed from the tank's hold, like the dark restart.
- **Optional burn while healthy** (ore to charge for a store that is low but not dark): decide with item 3.
- **Forward radiator** (later, expensive): a second place to vent heat that restarts the hop falloff; it could be a
  generator's cold sink.
- **What a cut costs the defender.** Relays cost 4 ore, so a cut is easily rebuilt; decide a relink and warm-up time and
  dearer links (`DESIGN.md` §4.11).
- **Priority tiers player-set:** only if fixed tiers prove too rigid.
- **Browser check:** the power labels were never seen in the browser (the pane had no WebGL 2, and `smoke.mjs` was not
  run). Check them in a real browser with `?energy`.

## HUD: share more between desktop and browser

**Today.** The desktop HUD is ImGui (`HudTheme`, `GameHud.*`); the browser HUD is DOM and CSS in `src/Emberward.Browser/wwwroot`,
fed by `BrowserFrameBuilder`. Each panel is built twice. As the HUD grows (energy labels, per-machine upgrade panels,
generator and turret readouts) the cost of keeping the two in step grows with it.

**Plan, in order.**

1. **One view-model per panel.** Rows, text, colour levels, enabled states and which machines get a label are computed
   once, in code that needs no window, and tested once. Each platform only maps the record to widgets. Do this for every
   new HUD element from now on (`PowerLabels` is the first example).
2. **World-space overlays in the shared renderer.** Charge and flow labels now use bitmap glyphs and fill bars in
   the shared entity layer (`PowerLabelQuads`). Keep new gauges above machines and fog-aware markers in that layer.
3. **Panels and menus stay per-platform** (ImGui and DOM). A shared widget toolkit would need text layout, hit-testing,
   focus, scrolling and touch, and the browser would lose selectable text, page zoom, IME and accessibility.

**Not now: dropping ImGui.** Revisit if a third platform appears, if one panel change routinely needs edits in both
clients, or if the HUD turns out to be mostly read-only gauges.

**First step.** Count HUD code per platform, split into read-only and interactive, to see how much a shared renderer
would actually replace.
