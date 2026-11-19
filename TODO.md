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
- **Another session's work:** emberite finite-ore changes are commit `a6e43f36` on `codex/tank-cargo-heat` (PR #72). That
  commit also holds the finiteness text in the main checkout's `DESIGN.md` (§4.1, §4.3, §4.4, §4.7, §6.3, §11 and the
  eleven challenges in §4.10). It is not in this branch until it merges.
- **Every item below is unstarted.** Do them in the order given; each one builds on the last.

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
- **Islands are keyed by base.** `LinkedIsland` walks upstream links to the base, and membership is looked at again only
  when `LinkMap.PowerVersion` changes. A network with no base is **not** an island yet, and a group cut off from every
  base shares no charge (each machine drains alone). Items 2 and 5 must generalise this.
- **Per tick, per island, tier by tier:** pay members' idle draw from the base store, then top up non-full capacitors by
  at most their rate. A tier the store cannot pay is starved and fed again once the store holds 24 ticks of its idle
  draw. Everything is in the economy tier today.
- **Settling:** a capacitor is brought up to date only when used, when it changes island or tier, or every 5 s, and idle
  draw reaches the ground as heat then. Idle charge paid but not yet settled is `EnergyNetwork.PendingCharge`, counted in
  `World.SumSystemCharge`. **Anything that changes `Machine.Charge` or `ChargeCapacity` must go through
  `EnergyNetwork.Spend` / `Settle`.** A fed capacitor refills only after a spend or an island or tier change.
- **Failing gently:** a relay still pumps heat with an empty capacitor; only the harvester stops when empty.
- **Output-only queries for the HUD:** `World.MachinePowerOf(machine)` (charge, capacity, in and out per second, fed) and
  `World.BasePowerOf(base)` (store, capacity, production, island draw). They return null while the flag is off.
  `PowerLabels.Of` holds all label wording and colour rules; extend it when a new state appears.
- **Known gaps:** "charge passed on to others" is always 0 until item 5; no test reaches the tier-ordering branch until
  item 6 adds a second tier.

## 1. Finite heat sources (heat bodies finite; no pinned temperatures)

**Why.** `DESIGN.md` §4.1 (in the other session's branch) says every body is finite: no pinned temperatures, no permanent
sources or sinks; the outside is a loss (§4.3), not a pinned cell. This matters for energy: the base engine's efficiency
uses the temperature above the base's **pinned** 0, and a generator's output depends on its source not running out.

**Scope.** Remove the remaining pins: hot rock (100 degrees), frost rock (-60), and the base and `ConstantEnergy` sinks (0
degrees), in `Tweaks.World.ThermalMaterials` and the pocket placement code. Read §4.1, §4.3, §4.4, §4.7, §6.3, §11 and
§4.10 (the eleven challenges) in `E:\Projects\active\apps\tunnerer\DESIGN.md` (main checkout, branch
`codex/tank-cargo-heat`) before starting. Report which of the eleven are solved and which remain open.

**Start from the ore commit.** `a6e43f36` (PR #72) already removed the ore pins and the plain-ore floor and made lump
cores finite stores (`EmberiteCore`, starting at 600 degrees). Start after it merges, or from a branch that includes it.
Its author asked that this item's fingerprint update come **after** that commit, so the two do not fight over
`ExpectedRelayPylonHarvesterNetwork`. Do not edit the ore files that work changed (`OreDeposits.cs`, `TerrainPixel.cs`,
`TerrainGrid.Ore.cs`, `TerrainGrid.HotRock.cs`, `OreMaterial.hlsli`, `Constants.hlsli`, the ore tests) beyond what a merge
needs; check with the user first.

**Watch for.** The engine's `eta = clamp(dT / (dT + K), 0, max)` with `dT` above the base's temperature, and the base's
cold sink (the dimensional link) must now come from a finite body or an explicit loss. Hot rock that stops being an
infinite supply caps relay and generator output; retune `Tweaks.Base` and `Tweaks.Energy` and say so. Update the
`SimulationFingerprintTests` constants and say so in the commit message.

## 2. Local generator (a new machine)

**Design (`DESIGN.md` §4.9).** Heat only, first. It converts the temperature difference between its intake and its own
surroundings, so equalised ground yields nothing. Efficiency capped near 0.35 (the base engine's cap is 0.5). Hard output
cap per generator. Waste heat goes into the ground around it (through the heat writers, which wake the tile and so the
relays), so output falls as the ground warms and packed generators choke each other. Conversion is booked as
`ConvertedToCharge` in the ledger. It is a producer in its island: output fills the island pool and capacitors.
Emberite fuel (fed from the tank's hold) is a later step; do not build it now.

**Needs.**
- **Islands without a base.** Tranche 1 islands are keyed by base. A generator makes an island that can live without
  one; membership, the pool and the starved-tier logic must handle a producer other than the base engine, and charge
  sharing inside a cut-off group.
- **The machine checklist in `AGENTS.md`:** `MachineType`, a `MachineBehavior` in `MachineBehaviors.All` (action, interval,
  colour, cost, build key, traits, capacitor), `Tweaks.cs`, a `MachinePresentation`, a `MachineSprite` (shader shape; a new
  shape needs a branch in `EntityPs.hlsl` and `Layout.hlsli`, see `EntityLayoutGuardTests`), `StepEventSounds.BuildSound`.
- **A build key.** Only three exist (`BuildKey`): relay, harvester, pylon. The pylon goes in item 7. Decide, and say which:
  add a fourth key now or temporarily reuse. Item 6 (turret) needs a fourth key in any case.
- **Guard tests:** a closed loop never gains charge; the tank torching beside a generator gains no more charge than the
  cap allows (the tank's torch creates heat from outside the system); packed generators produce less than spaced ones.
- **Labels:** show production in `PowerLabels.Of` (a producer colour) and in the base row.

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

## 4. Per-machine upgrades

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

## 5. Relay throughput limits and the Conduit upgrade

**Design.** Each relay has a charge-transfer limit; a chain is as fast as its slowest node. Compute a tree from the
producers (reuse `LinkMap`'s power search; recompute only when links or demand change): demand is added up from the
leaves, each node caps what passes through it, supply is handed back down by tier. Charge passing through loses a few
percent as heat **at the relay**, booked through the heat writers so the ledger balances; a hot relay throttles, and
takes damage further up. The Conduit upgrade (item 4) multiplies the limit about x1.5 then x2, with a hard ceiling.
Conductors (`DESIGN.md` §4.2) sit alongside relays: a conductor is a heat trunk, Conduit carries charge.

**Watch for.** A machine at rest must still cost nothing per tick. "Charge passed on" in `MachinePower` becomes real here
(update the queries and `PowerLabels`: a throttled and an overheated relay colour). Tests: a slow node limits a chain; an
upgrade on one relay changes nothing until the whole trunk is upgraded; the loss heats the relay and the ledger
balances; throttling above the temperature.

## 6. Turrets

**Design.** A powered machine: it works only while powered and linked, draws charge to fire (the Turrets tier, between
base defence and economy), and makes heat in its own cell when it fires (heat writers; overheating slows its fire). It
fires at enemy tanks in range and line of sight; the range is short, so it defends a zone. Targeting must be
deterministic: a stable tie-break and no unordered iteration; use `MachineList.CollectNear` and vision. Bots must be able
to build and use them, or turrets are player-only; decide and say so.

**Needs.** The full machine checklist (as in item 2), a fourth build key (change `BuildKey`; the keys, the README, F1
screen, tutorial and `KeyboardController`/`BrowserApi`/`input.js` all follow), a sprite and shader branch, build and shot
sounds (shots through `StepEventKind`), the tier-ordering test that item 3 and tranche 1 lacked. Upgrades with item 4:
**Rapid** (faster fire, more heat) and **Long** (more range and damage, slower fire); a Hardened option later.
Balance risk: a turret beside a relay makes raiding impossible. The power link and a short range and a high cost help;
tune in play.

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

## 8. The tank's reserve and exploring

**Design.** The reserve is the existing off-network clock (`DESIGN.md` §4.6), **not** a second meter. Burning a carried unit
of emberite extends it and takes that unit's warmth (0.1 degrees a second a unit, see §6.3) out of the tank, so a big
load is range and risk together. Running out is a staged state: warning, degraded (slower dig, shorter vision, weaker
shots), stranded (crawl and dig only), then heat death as today; each has a counter. Relays and generators push the safe
zone out; far lumps pay for the risk.

**Open.** I read the tank-heat code only through `Tweaks`, not the code itself. Before building, read the tank
temperature code (`Tank.cs`, `TankTemperatureTests`) to see how a burn adjusts the floor. Needs a burn action (input
bit or reuse of the hold-dump), the staged effects, HUD tags (the "OFF NETWORK" tag exists), and tests.

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
2. **World-space overlays in the shared renderer.** Charge and flow labels, gauges above machines and fog-aware markers
   belong with the entity layer, which both platforms already share through the HLSL-to-GLSL shaders. Add small text
   quads there (font atlas or SDF) instead of drawing them twice.
3. **Panels and menus stay per-platform** (ImGui and DOM). A shared widget toolkit would need text layout, hit-testing,
   focus, scrolling and touch, and the browser would lose selectable text, page zoom, IME and accessibility.

**Not now: dropping ImGui.** Revisit if a third platform appears, if one panel change routinely needs edits in both
clients, or if the HUD turns out to be mostly read-only gauges.

**First step.** Count HUD code per platform, split into read-only and interactive, to see how much a shared renderer
would actually replace.
