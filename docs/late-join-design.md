# Joining a match in progress: design

Status: proposed, 2026-11-17; reworked 2026-11-18 around snapshots, with no catch-up. Steps 1 to 4 of §13 are built
(2026-11-18): the join works in process, through the tests' in-memory relay; protocol 6 carries the snapshot messages and seat masks. Running rooms on the
relay server still admit returning players by name. Admission of a new player and the game screens are pending.
It extends [multiplayer-design.md](multiplayer-design.md) §5, which leaves "late join of a new player" out of the
first tranche.

## 1. Summary

- **A late joiner takes over a bot's tank.** Every slot nobody took at the start already has a tank and a base,
  driven by a bot that every client runs from the match seed. Late join needs no tank or base to appear
  mid-match. It changes who drives a tank that is already there.
- **The joiner never catches up.** Some machines can only just simulate the match in real time, so a client that
  starts behind may never draw level (§8). Instead, the match holds for a moment while a player already in it saves
  the world, the relay passes the bytes on without reading them, and the joiner loads them. When the match goes on,
  the joiner is at the live tick with everyone else.
- **The handover is part of the tick stream.** Every bundle says which slots players drive. The seat changes at the
  first tick after the hold, the same tick on every client.
- **The relay stays a relay.** It never reads a snapshot and never needs to know whether a tank is alive. The joiner
  picks a seat from the world it loaded; the relay only checks that a bot still holds it.
- **The sync hash cannot vouch for a snapshot.** It reads a sample of the state, chosen to catch a divergence
  spreading, not a field a serializer forgot. §10 says what it reads, what it does not, and the tests and run-time
  checks that make a snapshot safe.
- **Rejoin moves to the same path.** Today a returning player replays the match from tick 0, which a slow machine
  cannot do either.
- **Normal play costs no more.** A join may pause everyone for a moment, but nothing here adds to the simulation's
  cost while nobody is joining: no work per tick, no periodic saves, and a sync digest that is cheaper than before
  (§8.1).

## 2. What exists to build on

| Today | What late join gets from it |
|---|---|
| Open slots are played by bots, seeded by `MatchSetup.BotSeed(matchSeed, slot)` (`Seats.ForStart`) | A tank and a base in every seat nobody took. |
| Pause for everyone (`SetPaused`, `PauseState`): the relay stops closing ticks and its clock does not count the pause | The hold. A join is a short pause the relay starts and ends itself. |
| The relay keeps the first sync hash reported for every hash tick, for the whole match | The joiner can check the world it loaded against what the players reported for that tick. |
| `LockstepRoom.Disconnect`/`Reconnect`; bot slots are disconnected at the start and play the neutral input | Seating a joiner reconnects the slot's input queue. |
| The only Core state that depends on who drives a tank is `TankBase.IsBotControlled` (the bot's stand-in ore income) | Handing a seat over in Core is one flag, flipped at one tick. |
| The relay keeps the whole input log | Recordings, and the one case where a replay is still acceptable (§11). |

What blocks it today: there is no way to save and load a world (DESIGN §11, gap 6); the bots draw from
`System.Random`, whose state cannot be saved (§9.1); `MatchRoom.Join` on a running room accepts only a name from
the start roster; `Start.HumanMask` is fixed for the whole match, in the relay, `RelayConnection`, `NetSession` and
`MatchRecording`; `Game._localSlot` is read-only and set before the match starts; `World.SetBotControlled` cannot
be undone.

## 3. Seats

A seat is a slot as the room sees it. It is in one of three states:

| Seat | Who drives the tank | Its bit in the human mask |
|---|---|---|
| **Player** | A connected player, by inputs in the bundle | set |
| **Vacant** | Nobody: its player dropped out, and the tank plays the neutral input | set |
| **Bot** | The bot every client runs | clear |

- A late joiner can take a **bot** seat, and only that.
- A **vacant** seat stays its player's: they come back to it by name, as now. Handing a vacant seat to a stranger
  would make a dropped player's return depend on luck. (Letting a bot hold a vacant seat until its player
  returns is a later step, §14, and uses the same mechanism.)
- A room whose seats all have players has no room for a newcomer. Making a new tank and base mid-match is not
  part of this design (§12).
- So the host decides how many latecomers a match can take when it opens the room: `--players=4` with two people
  present starts with two bot seats, and two more people can join later. The lobby says so (§6.3).

## 4. The protocol

### 4.1 Flow

```
joiner                       relay                                   seated clients (A uploads, B verifies)
  | Hello{room, name} ---->   |  running, not over, late join on,
  |                           |  a bot seat free, no name match
  | <---- Welcome{slot=-1}    |
  | <---- InProgress          |
  |  builds the world from    |
  |  the settings (seconds;   |
  |  the match runs on)       |
  | ReadyToLoad ---------->   |  S = the ticks closed so far. Holds: no tick S yet
  |                           |  PauseState{held, joining: name} -------------------> all
  |                           |  SnapshotRequest{S, sendChunks} ------------------> A (true), B (false)
  |                           |                     both save at S
  |                           |  <--------------------------- chunks from A, SnapshotDone from A and B
  |                           |  the two digests agree
  | <---- SnapshotOffer{S, digest, syncHash(S)}, SnapshotChunk x N
  |  loads, checks (§10.3),   |
  |  picks seat 2             |
  | TakeSeat{2} ---------->   |  seat 2 is a bot seat: mask bit 2 set from tick S
  | <---- Seated{2, S}        |
  | <---- Roster -------------+-------------------------------------------------> all
  |                           |  PauseState{running} ------------------------------> all
  | <---- Tick{S, mask} ------+-------------------------------------------------> all
  | Input, Input, ... ---->   |
```

`S` counts ticks, as a hash's tick does (`Hash.AtTick`, `World.AdvanceCount`): the snapshot is the world after `S`
ticks, the relay has closed bundles 0 to `S - 1`, and the first bundle after the hold is numbered `S` and carries the
new mask. (An earlier draft called the last bundle closed `S` and the next `S + 1`; it is the same moment.) Nothing
moves during the hold, so the joiner's world after `S` ticks is everyone's world when the match goes on. The seat the
joiner picked cannot change under it during the hold.

### 4.2 The human mask travels with every tick

`TickBundle` gains `HumanMask` (one byte while a match has at most 8 slots): the slots players drive at that
tick. A client compares it with the previous bundle's before it steps, and applies each change first:

- **bit set** (bot to player): drop that slot's bot and clear the base's bot flag (§7). From this tick on the
  slot's input comes from the bundle.
- **bit cleared** (player to bot): not sent by this design's relay. A client handles it anyway, by making a bot
  seeded from the match seed, the slot and the tick, so the mechanism in §14 needs no new protocol.

Why a mask on every tick and not a "seat changed" event:

- The log and a `MatchRecording` carry the seats by themselves. Replays and desync reports need no second list
  kept in step with the ticks.
- Order cannot go wrong. An event beside the ticks would have to arrive and be applied before its tick on every
  path (live, `Log`, recording). A field in the bundle is applied with it.
- It costs 24 bytes a second per client, and 64 bytes in a full `Log` chunk.

`Start.HumanMask` stays: a client makes its bots when the match starts, before the first bundle arrives.

### 4.3 Messages

New or changed, protocol version 6:

| Direction | Message | Purpose |
|---|---|---|
| S to C | `Welcome { slot, config }` | `slot` is -1 for a watcher: in the room, driving nothing |
| S to C | `InProgress` (replaces `Rejoined`) | The match is on: build the world from the settings and say when it is built |
| C to S | `ReadyToLoad` | My world is built; hold the match and get me a snapshot |
| S to C | `PauseState { paused, bySlot, joining }` | As before; `joining` names the player the match holds for, empty for a pause a player asked for |
| S to C | `SnapshotRequest { tick, sendChunks }` | Both clients save and hash at this tick. Only the designated uploader sends chunks |
| both | `SnapshotChunk { tick, index, bytes }` | 32 KiB each, with a possibly shorter final chunk |
| C to S | `SnapshotDone { tick, length, digest, syncHash }` | Snapshot report. `length` is the compressed bytes sent (zero for the verifier), `digest` a 64-bit hash of the uncompressed bytes (the first eight bytes of SHA-256; never of the compressed ones, which Brotli may write differently in another .NET patch version), `syncHash` the sender's sync digest at `tick` |
| S to C | `SnapshotOffer { tick, length, digest, syncHash }` | To a joiner: a snapshot follows, with the agreed sync digest for its tick |
| C to S | `TakeSeat { slot }` | From a joiner with the world loaded: let me drive this bot seat |
| C to S | `JoinFailed { reason }` | The snapshot failed its checks, or no seat is worth taking; let the match go on |
| S to C | `Seated { slot, atTick }` | Granted: your inputs count from tick `atTick` |
| S to C | `SeatRefused { reason }` | Not granted (another joiner took it); the client may ask for another |
| S to C | `Tick { n, humanMask, inputs[] }` | As before, with the mask (§4.2) |
| S to C | `Log { firstTick, slots, masks[], inputs[] }` | As before, with a mask per tick; now only for §11's empty room |
| S to C | `Refusal { reason }` | As before, with new reasons: "every tank in this match has a player", "this match is closed to newcomers", "the match is over", "the match could not be copied to you" |

`MatchConfig` gains `LateJoin` (on by default; `--late-join=off` from the host closes the match to newcomers). The
relay reads it, as it reads `TicksPerSecond`. It is in the settings every client builds from, so the hash clients
compare before a match covers it.

## 5. The relay

`MatchRoom`:

- **Watchers.** A list of peers without a slot: joiners building their world or loading it, at most as many as
  there are bot seats. They get `Roster` and `PauseState`, and `Tick` only once seated. A watcher does not keep the
  match clock running.
- **`Join` on a running room**, in this order:
  1. The name matches a vacant seat's start name: a rejoin, by the same snapshot path, into that seat.
  2. The match is over, closed to newcomers, has no bot seat left, or already has as many watchers as bot seats:
     refuse, with the reason that applies.
  3. Otherwise: a watcher. `Welcome { slot: -1 }`, `InProgress`.
  The name must still be unique: it is checked against the players present and against the start names of vacant
  seats, so a newcomer cannot be numbered into a dropped player's name.
- **The hold.** On `ReadyToLoad` the relay closes ticks up to the one due, calls the last one `S`, and holds: no
  more ticks until the join ends. It requests an upload and, when available, independent verification at `S` (§9.2).
  Joiners that send `ReadyToLoad` during the hold share its snapshot. The hold ends when every joiner in it has been seated or has
  failed, or after `JoinHoldSeconds` (15): then the relay goes on without them and refuses them with "the match
  could not be copied to you". A player's own pause and a hold are separate; the match runs when neither is on.
  Hold time is not match time, like a pause.
- **`TakeSeat { slot }`.** Granted when the slot is a bot seat. The seat changes at bundle `S`:
  `LockstepRoom.Seat(slot)` reconnects its queue and sets its mask bit from the next bundle, the watcher moves into
  `_peers[slot]`, its name goes into `_names` and `_startNames` (so it can come back by name like anyone who
  started), and everyone gets the roster. Two joiners asking for one seat: the first is seated, the second gets
  `SeatRefused` and asks for another.

`LockstepRoom` keeps the current mask, stamps it on every bundle it closes, and gets `Seat(slot)`.

The relay's other limits apply to watchers unchanged: the connection cap, the message rate, the hello timeout,
dead-connection pings. Snapshot chunks have limits of their own (§9.3).

## 6. The client

### 6.1 `NetSession`

- `Step(world, bots, bundle)` applies mask changes before stepping (§4.2). The bots array becomes the session's
  own state, changed by the bundles, not set once from `Start`. (Built as `Seats`: the bots, the current mask and
  the match seed, with `Step`; `MatchRecording.Replay` steps through it too.)
- A session can start from a loaded world at tick `S`, not only from an unstepped one (today's constructor
  refuses a stepped world). `Played` then starts at bundle `S`.
- **Picking a seat.** From the loaded world: the bot seat whose tank has the most lives left, and among those the
  lowest slot. A seat whose tank is out of lives is never picked. If none is left, `JoinFailed` with that reason,
  and the client says so.
- **Seated.** From tick `atTick` the session sends one input per tick simulated, as any seated client.

### 6.2 `MatchRecording`

The `humanMask=` header stays for tick 0. In the `ticks:` section a line `mask=<hex>` says the mask changed
before the next tick. Old recordings, which have no such line, load as before. A joiner's recording starts from
its snapshot: the recording holds it in its header (`snapshot=`, compressed, in base 64, with `firstTick=`), so a
desync report, which is a recording, carries it too, and `Replay` loads it before stepping. The `humanMask=` header
is then the mask before the first recorded tick.

### 6.3 `Game`

- **The local slot can change.** `_localSlot` (17 uses, in `Game` and `GameSimulationStepper`) becomes the local
  seat, which is unset until the joiner is seated. While it is unset the game draws a "Joining" plate and the map,
  and hides the HUD panels that read the local tank.
- **After loading, everything drawn is rebuilt.** The world was built from the settings and then overwritten, so
  the renderer uploads the whole terrain once, as at the start of a match, instead of the cells a step changed.
- **Vision and radar start at the seat.** `World.TrackVision` and `TrackRadar` are asked for when the seat is
  granted, and the terrain view switches from live to fogged then. The fog's memory of explored ground starts
  empty: the joiner arrives with a fresh map, not the bot's. (Vision is output only and not in the snapshot.)
- **Names follow the roster.** `_slotNames` is fixed at `BeginNetMatch` today. It is rebuilt whenever the roster
  or the mask changes, so "Bot" becomes the joiner's name on every screen at the tick of the handover.
- **Screens.** Everyone, during a hold: "Ann is joining" where the pause notice goes. The joiner: "Joining:
  building the map", then "Joining: copying the match". Once seated: "You have taken over the bot in slot 3", in
  the slot's colour. The lobby's open slot reads "open: a bot plays until someone joins".
- **Joining is the command line people already use**, `--net=ROOM` with a name. Whether it is a new match, a
  rejoin or a late join is the relay's decision, not a new flag.

## 7. Core

- `World.SetBotControlled(int spawnIndex)` becomes `World.SetBotControlled(int spawnIndex, bool botControlled)`,
  and clearing the flag clears the base's ore credit with it. Every client calls it at the same tick, from the
  bundle, so it is simulation state like any input.
- The sync digest hashes each base's `IsBotControlled` with its ore and upgrades (and more, §10.3).
- No `MatchRules` flag: late join adds no system to the step. A match that never changes seats steps exactly as
  today.
- Edge-detected keys need nothing. Core reads a key press as a change between a tank's consecutive inputs; at
  the handover those are the bot's last input and the player's first, the same on every client.
- Save and load (§9).

## 8. Why the match holds

A client that is behind simulates at `r` ticks per tick of wall time while the match goes on at one, so a backlog
of `B` takes `B / (r - 1)` to clear. That is fine on a fast machine and hopeless on one that only just keeps up:

| `r` | Replay a 10-minute match from tick 0 | Clear a 2-second gap after loading a snapshot |
|---|---|---|
| 4 | 3 min 20 s | under a second |
| 2 | 10 min | 2 s |
| 1.1 | 100 min | 20 s, and every hitch meanwhile adds to it |
| 1.02 | 8 hours | 100 s |

(`r` is lower than step cost suggests today: a catching-up client replays for at most 25 ms of each 41.7 ms tick,
`NetworkTweaks.CatchUpBudgetMs`, and draws for the rest.)

So even a snapshot is not enough on its own: the time to save, move and load it is a gap the joiner would have to
close. Holding the match while clients save, transfer and load the snapshot removes that gap.

How long a hold lasts, estimated and to be measured:

| Step | Default map (480 x 300) | 1500 x 750 |
|---|---|---|
| Build the world from the settings | before the hold, not in it | before the hold |
| Save: copy the state at `S` | milliseconds | tens of ms |
| Compress (off the main thread) | tens of ms | a few hundred ms |
| Upload, relay, download | well under a second on an office line | one to a few seconds |
| Decompress, load, check (§10.3) | about a tenth of a second | under a second |
| Round trips (ready, request, offer, seat) | about four, 40 to 80 ms each | the same |
| **In all** | **about half a second to two** | **two to six seconds** |

`JoinHoldSeconds` (15) is the limit after which the relay gives up on a join rather than hold everyone longer.

### 8.1 What normal play pays

Some machines can only just keep up with the match, so this design adds nothing to the simulation's cost while
nobody is joining. All the work of a join happens in the hold, when nobody is simulating, or on the joiner.

| While nobody joins | Cost to every player |
|---|---|
| Each tick | One more byte in each `Tick` (the human mask) and one byte compared before stepping. Nothing else. |
| Each second | The sync digest. Its new parts replaced the energy total with a per-cell hash in four independent lanes, which is cheaper than the sum was. Measured 2026-11-18 on the development Mac, beyond the state hash: 0.19 ms before and 0.06 ms after on 480 x 300, 1.32 ms and 0.39 ms on 1500 x 750 (Release); 4.88 ms and 2.47 ms on 1500 x 750 in Debug. |
| Snapshots | None. The saver reads the state only when asked; it keeps no dirty flags, shadow copies or logs during play. |
| Bots | `FastRandom` in place of `System.Random`: half the cost per draw. Bot state is in the digest, under 1 µs for two bots. |

| During a join | Who pays |
|---|---|
| The hold | Everyone waits; nobody simulates. |
| Saving and uploading the snapshot | Up to two players save at the held tick. One compresses and uploads. |
| Building the map, loading, checking | The joiner. |

## 9. Snapshots

The relay never reads the game: to it, a snapshot is bytes one client uploads and another downloads. This section
is the design; §10 is about whether it can be trusted.

### 9.1 What it holds

The joiner first builds the world from the match settings, exactly as today, then overwrites its state from the
snapshot. Everything level generation fixes for the whole match (rock pockets, ore deposits, the material tables,
the settings, the systems a match runs) is therefore never sent. What is sent is every value that changes during
play and changes later results:

| Owner | What it saves |
|---|---|
| Terrain | Per cell: the pixel (1 byte), scorch intensity (1), heat temperature (4), air temperature (4). Raw bits. |
| Heat solver | The active tiles and the disturbance count of every tile (`ThermalActivityTracker`); the scheduler's two frame counters (`ThermalScheduler`). Which tiles are awake decides which cells are stepped, so it changes temperatures. |
| Regrowth | The appointment wheel as it stands: its arrays, free list and per-tile records (`RegrowthSchedule`), and the tick. Due cells are handled in wheel order, and a cell that settles into dirt changes what its neighbours see in the same tick, so rebuilding the wheel from the terrain would give different ticks and a different order. |
| Link map | Points and links in list order, with ids, the next point index, the relink clock and the power-spread flags. |
| Machines | In list order (it is the action order). Every field the inventory found: action accumulator, harvester anchor, heading, returning flag, ore work, charge, cold-ground stamp. References become indexes: owner base, link point id, the machine being charged. |
| Projectiles | Live and pending, in order, with life, owner and heat scale; the list's random generator. |
| Tanks | Position and the hashed fields, plus the turret cooldown, the digger's random generator and move credit, unload credit, the held-key flags of the build controller and the upgrade key, respawn timer and state, spawn protection, the off-network floor and flag, the carried machine (as an index). |
| Bases | Reactor heat remainder, charge, ore store, upgrades, the bot flag and its ore credit. |
| Goal and result | Every base's core tap (tap rate, hold time, tapped), the first to tap, and whether the result is decided. |
| Ledger | The thermal ledger's totals, so the balance checks and the fingerprint go on matching. |
| Bots | Each `BotTankAI` with its navigator, gunner, trail and profile cycle. They live in Desktop's session, not in `World`, so the session saves them beside the world. |

Not saved, because nothing in the simulation reads it or it is rebuilt from what is saved: vision and radar
(output only), step events, the renderer's change list, machine buckets and link tiles (rebuilt on load), the
world's context closures and system list, the settings and material tables, the heat solver's scratch buffers, and
wall-clock diagnostics (`SimulationProfile`), which would make two honest uploaders' bytes differ.
`SnapshotCoverageTests` lists every one with its reason.

**How it is written.** Two ways, with opposite defaults:

- *Each system writes its own save and load code.* Explicit, and as fast as it gets. A field nobody remembered is
  silently left out, so the coverage test (§10.3) has to catch every omission.
- *One general saver walks the objects.* It writes every field of every object reachable from the world, number
  arrays in bulk and references between objects as ids, and skips only fields marked as not saved, each with its
  reason. A new field is saved without anyone doing anything; the risk becomes saving something that must not
  be, which fails loudly. Its speed is set by the big arrays, not by the walk.

The general saver is what was built (`Core/Snapshots`, step 3): it turns "did we forget anything" from a review
question into a default. How it treats what is not a plain field:

- **Lists are saved by count and items, through their public API.** An earlier draft of this section said to copy
  the internals of `List` and `Dictionary` so that their order came back exactly. Those internals belong to the
  runtime, which is the same problem as `System.Random` below, and a dictionary keyed by strings is laid out
  differently in every process (string hashes are randomised per process). The saver refuses dictionaries, sets
  and delegates; state is held in fields, arrays and lists.
- **Keyed lookups are rebuilt on load** (`ISnapshotLoaded`), and each was checked to be order-free: the machine
  buckets (`MachineList`; every caller that does not sort what it gets only asks whether anything is there) and
  the link tiles (`LinkMap`; rechecking a link does not depend on the order links are met in).
- **Loading fills the built world's objects in place** wherever it holds one of the same type at the same spot (the
  world, its systems, the tanks, the terrain's arrays), so that the settings and the closures over the world stay
  wired to it. Objects made during play (machines, links, projectiles) are made new.
- **Shared constants** (`MachineBehaviors.All`, one per machine type) are written as their index, so a loaded
  machine points at the same behaviour as a built one.
- **Structs are written field by field**, never as raw memory, because padding would make the bytes non-canonical.
  Arrays of numbers and enums are copied as raw bytes.

**`System.Random` has to go.** The bots draw from it, and its state cannot be saved: .NET offers no way to read or
set where a `Random` is in its sequence. Reading its private fields by reflection would tie the snapshot to the
runtime's internals, which can differ between players' .NET installs (the game is not published with its own
runtime). Counting draws and redrawing them on load works, but needs a wrapper that counts samples, not calls. The
bots instead move to the `FastRandom` Core already uses (one `uint`), held in a small shared object because the
navigator and the gunner draw from the same stream. That changes how bots play, so the bot-against-bot constant in
`SimulationFingerprintTests` changes once, said so in that commit.

**The format is the build's own.** The relay refuses to mix builds, so a snapshot is only ever read by the build
that wrote it. It needs no version number and no migration. (The sector save of DESIGN §12.2 will need both; this
is not that.) It is canonical: the same world always gives the same bytes, with no timestamps. Little-endian,
floats and doubles as raw bits, `TimeSpan` as ticks, then compressed (`BrotliStream`, in the base library).

**Size.** About 10 bytes a cell before compression; entities add little. Measured (step 3, §13): 1.6 MiB raw and
300 KiB compressed on the default 480 x 300 map, 11 MiB raw and 1.3 MiB compressed at 1500 x 750, and 12.4 MiB raw
but still 1.3 MiB compressed with 400 machines planted. Settled ground repeats, so Brotli takes it down five to nine
times. Sending only what differs from the freshly built world, which the joiner has anyway, would shrink it further
on a large map where most ground is untouched (§14).

### 9.2 Uploader and verifier

- **Who.** Choose the seated client with the latest reported tick to upload. If another is available, choose the
  next closest to verify independently (§10.3). A lone player can supply the snapshot.
- **When.** `S` is wherever the clock stands when the joiner is ready, not a hash tick, so the relay has no sync
  hash for it. Both clients report it with the raw snapshot digest in `SnapshotDone`. The relay waits for all requested
  reports and chunks before offering the snapshot.
- **Cost.** Both clients step to `S`, save once and hash the raw bytes. Only the uploader compresses and sends
  the snapshot. At the measured 1500 x 750 size (§13), a second full upload would cost about 1.3 MiB.
  The hold still includes one upload and one download. Its duration needs a socket measurement (§8).

### 9.3 Limits

- Chunks are the only messages over 4 KiB, and are accepted only from a client asked for that tick. They count
  against a byte budget, not the 2000-a-second message limit, which an 11 MB upload in 4 KiB messages would break.
- A snapshot is at most 32 MiB. The relay keeps at most one compressed payload per hold and 256 MiB in all.
  It drops a snapshot when its hold ends.
- **An uploader that leaves** mid-upload: the relay asks another seated client for the same tick (nothing has
  moved). If there is none, the join fails and the match goes on.
- **If either hash differs**, the relay announces a desync at `S` and withholds the offer.

### 9.4 The joiner

1. Build the world from the settings, then send `ReadyToLoad`. Building takes seconds; doing it first keeps it out
   of the hold.
2. Decompress what arrived, check the digest of the uncompressed bytes, load the snapshot into the world.
3. Check it (§10.3). Any failure: `JoinFailed`, and the client says the join failed.
4. Pick a seat (§6.1) and send `TakeSeat`. On `Seated`, step from bundle `S` with everyone else.

## 10. Is a snapshot safe? What the hash sees

### 10.1 What the sync digest reads

`WorldStateHash.ComputeSync` is what clients compare once a second. It reads:

| Exactly | Only as a total | Not at all |
|---|---|---|
| Every terrain pixel | The heat field: one double, the energy of terrain, air, tanks and bases summed | Scorch intensity |
| Tanks: position, direction, lives, reactor heat and health (whole numbers, so nothing is lost), ore held, heat (float bits), dead or alive, barrel tip | Stored charge: one double over bases and batteries | Which heat tiles are awake; the heat scheduler's counters |
| Machines: position, type, state, owner colour, alive, reactor | | Regrowth appointments |
| Link points and links: ids, position, type, enabled, powered, alive | | Projectile life, owner, heat scale; pending projectiles (their count only) |
| Live projectiles: position, velocity, type | | Every random generator: projectiles, each tank's digger, bots |
| Counts of projectiles, machines, points, links; the tick | | Tank timers and flags: turret cooldown, respawn, spawn protection, held keys, unload credit, off-network floor |
| Each base's ore store and upgrade levels | | Harvester memory: action phase, anchor, heading, returning, ore work |
| | | Core tap progress, the first to tap, the decided result |
| | | All bot state |

### 10.2 Is it safe not to hash everything?

**For lockstep as it runs today, mostly yes.** The digest is a detector, not the state. Every client computes all
of the state from the same inputs with the same code; what makes them differ is a determinism bug, which starts
somewhere and spreads. Almost everything in the right-hand column reaches a hashed value soon: a wrong cooldown
fires a shot on another tick, a wrong regrowth appointment grows dirt in another place, a wrong heat tile drifts
into a tank's float heat or a melted pixel. The cost of hashing less is latency, and a desync report that points
at the symptom rather than the cause. State that never reaches anything hashed and changes nothing else (a base's
reactor health, which nothing reads) cannot matter. Hashing everything every second would cost a walk over all
state, which is what the digest was kept small to avoid.

Two gaps are real even today:

- **The outcome is not hashed.** Core tap progress and the first to tap decide the winner without passing through
  any hashed value. Two clients could disagree about who won, and the first `MatchOver` the relay believes would
  hide it.
- **Totals cannot tell where.** Heat moved between two cells of the same material, or charge moved from a base to
  a battery, leaves the totals the same, or different only by rounding luck. The difference shows only when it
  reaches a tank, a pixel or a machine's behaviour.

**For a snapshot, no.** A serializer bug is not a random divergence that spreads. It drops or misplaces one
particular field, the same way on every load. The digest is a sample chosen to catch spreading, and the dropped
field is as likely as not to be one it does not read. From the inventory, a field that is wrong after a load can
stay invisible:

| For how long | Examples |
|---|---|
| Until a rare event | The projectile and digger generators (the next explosion, the next rock torched); spawn protection (only if a shot lands in the window); a held-key flag (only if the key is held across the load) |
| Until a machine changes what it is doing | A harvester's action phase, anchor, heading or returning flag |
| Until the end of the match, unseen even then | Core tap hold time: the joiner shows a different winner |
| A row off in the heat grid | The heat total may match, or differ only by rounding |

And unlike a lockstep divergence, the bad value is in every joiner's world, not in one unlucky client's.

### 10.3 What makes it safe

Each check covers a different failure:

1. **Coverage guard test** (catches a field the snapshot forgets). `SnapshotCoverageTests` walks every type
   reachable from `World` and the bot types (field types, element types, and every Core type that can stand in a
   field of an abstract or interface type) and fails on any saved field the saver cannot write (a dictionary, a
   set, a delegate, another `System` class). It also lists every `[NotSaved]` mark with its reason, the same
   ratchet as `SourceGuardTests`: a new mark fails until it is added, and a removed one until its entry is deleted.
   That list is what keeps snapshots safe after today: a new system's state is saved, or someone has said why not
   and a reviewer has read it.
2. **Round-trip tests** (catch a field saved but restored wrongly, and a rebuild that does not match). For every
   fingerprint scenario, saved halfway, and for scenarios built to reach the late-showing fields above (an
   explosion after the load, a harvester on its way back, a tank under spawn protection, a build key held across
   the load, a machine carried across it, a shot fired on the save tick, regrowth and scorch busy across it, a link
   cut after it, a core tap mid-hold, bots mid-manoeuvre): run, save, load into a freshly built world, run both on
   with the same inputs, and compare the sync digest every tick (it hashes every cell's temperature bits), the
   fingerprint test's hash (adding the ledger) every 20 ticks and at the end, and a fresh save at the end. Also
   `save(load(bytes)) == bytes`.

   **What they cannot see.** A field wrongly marked not saved shows up only when its live value differs from a
   freshly built world's. Leaving out the turret cooldown passed every scenario, because the cooldown was back to
   0 at every save, as it is in a built world. Leaving out a random generator, the carried machine or the bot's
   mode failed at once. That is why item 1 lists the marks: a mark is a decision a reviewer reads, not something
   the round trip proves.
3. **Independent verification** (catch a world that had already diverged at `S`). The relay compares the two digests. Two
   correct clients write the same bytes, so a difference is a desync. This sees all of the state, which the sync
   digest does not.
4. **The joiner checks what it loaded** (catches corruption, and a reader that does not match the writer): saving
   the loaded world gives back the bytes it received, and its sync digest equals the reported value for `S`. Any
   failure fails the join; the match goes on without the joiner.
5. **A stronger sync digest** (worth doing now, snapshots or not). New parts beside `Economy`, so `Compute` and the
   recorded fingerprints stay as they are: the outcome (every core tap's hold time and tapped flag, the first to
   tap, decided); charge per holder instead of one total; the random generators' states; tank timers and held
   keys. All cost what the entities cost. Heat placement: mix each cell's temperature bits with its index instead
   of summing energy. That is the same walk over the grid the total already makes once a second, so it costs
   nothing new (and stays on the list of things to make follow activity when maps grow).
6. **Snapshots in desync reports.** On a desync the relay stops closing ticks (today it goes on), every client
   steps to the last tick it was sent, and saves a snapshot beside its report. Two players' snapshots of one tick,
   compared field by field, name the field that went apart instead of the symptom it caused minutes later.

With 1 and 2 in the test suite and 3 and 4 at run time, a snapshot is checked more thoroughly than lockstep
itself is today.

## 11. Edge cases

| Case | What happens |
|---|---|
| Two joiners at once | Both are served from one snapshot in one hold. The first `TakeSeat` for a seat wins; the second is refused and picks another from the same world. |
| A joiner arrives while a hold is on but its world is not built yet | It sends `ReadyToLoad` when built, after this hold ends, and gets a hold of its own. |
| A player presses P during a hold | Both are on; the match goes on only when the pause and the hold are both off. |
| The joiner leaves, or fails its checks, during the hold | The hold ends at once without it. |
| An uploader leaves during the hold | Another seated client is asked for the same tick. With none left, the join fails. |
| The hold runs past `JoinHoldSeconds` | The match goes on; the joiner is refused and can try again. |
| A late joiner drops out | Same as anyone who started: their seat is vacant, their tank idles, and they come back by name. |
| A late joiner's name equals a vacant seat's start name | That is a rejoin into the vacant seat (rule 1 in §5). A name is not authentication, as today. |
| The match ends while a joiner builds its world | The relay refuses the join when it asks; the joiner is told the match is over. |
| **A player returns to a room nobody is in** | There is nobody to copy the match from. A last player who leaves by quitting saves and uploads a snapshot on the way out, a cost at exit and none during play; the match clock stands from then, so a returning player loads it and replays nothing. A last player whose connection was lost leaves nothing: the returning player replays from tick 0 with the clock standing, which takes `t / r`, not `t / (r - 1)`, and so always finishes, though on a slow machine only after a long wait that only they bear. |
| A relay restart | The room is gone, as for everyone in it. |

## 12. Rejected alternatives

- **Catch-up by replay**, from tick 0 or from an older snapshot. A client that can only just keep up never draws
  level (§8). Even the gap after loading a fresh snapshot takes ten times its length to close at `r = 1.1`.
- **Slowing the match while someone catches up.** Clears a backlog in `B / (r - s)` at speed `s`: at `r = 1.1` and
  three-quarters speed, a 2-second gap takes 6 seconds of everyone in slow motion. A hold of the same 2 seconds is
  simpler and shorter.
- **A new tank and base mid-match** for a room with no free seat. The level generator places every spawn at
  tick 0 for `Slots` tanks; adding one later means finding ground for a base in a dug, heated, built-on map, and
  touches spawns, coverage, the goal and the referee. The host reserving seats with `--players` gets the same
  result for no Core work.
- **The relay assigns the seat at hello.** The relay does not simulate and cannot tell a live bot from one out of
  lives, so it would sometimes seat a joiner in a dead tank.
- **A separate "seat changed" event** beside the ticks. See §4.2.
- **The relay runs the simulation and serves snapshots itself.** It would need Core, and a redeploy for every
  gameplay change, which the relay was built to avoid; and a CPU per room on Starfish.
- **Periodic snapshots** from every client. Costs every player a save and an upload all match long, for joins that
  may never come. On request costs one save per join, inside the hold.
- **Saving `System.Random` by reflection.** Ties the snapshot to the runtime's private fields (§9.1).
- **Trusting the sync hash to check a loaded snapshot.** See §10.2.

## 13. Build order

Each step is testable without the next.

1. **Core groundwork** (done, 2026-11-18). Reversible `SetBotControlled`; the stronger sync digest (§10.3,
   item 5), with `IsBotControlled` in it. The energy total in the digest became a per-cell hash of both
   temperature fields, in the same single walk over the grid, hashed in four lanes so that it costs less than the
   sum did (§8.1). Bots are not in it yet: they live in Desktop's session, and join the digest with step 2. No
   fingerprint changes; the protocol is unchanged, since clients of one build compare the same digest. Worth
   having for desync detection whether or not the rest follows.
2. **Bots on `FastRandom`** (done, 2026-11-18). `BotRandom` wraps one `FastRandom` (seed scrambled with
   `FastRandom.Hash32`) for the bot, its navigator and its gunner. The bot-against-bot fingerprint changed. Every
   bot's state, its stream included, is a seventh digest part (`SyncDigest.Bots`, from
   `WorldStateHash.ComputeSync(world, bots)`). Measured against step 1 on the development Mac (Release): a draw
   costs 1.5 ns against 3.0 ns; over 16 bot-against-bot matches of 2400 ticks the bots took 2.43 against 2.32
   µs a tick and a whole step 0.133 against 0.127 ms, a gap well inside the spread between matches (1.6 to 6.4
   µs), which comes from the matches playing out differently; the digest is unchanged within noise (about 100 µs
   on 480 x 300, the bots adding under 1 µs).
3. **Snapshots in Core** (done, 2026-11-18). `WorldSnapshot.Save`, `Load`, `Compress` and `Decompress`
   (`Core/Snapshots`): the general saver of §9.1, with the bots beside the world, 22 `[NotSaved]` marks, the
   coverage guard test and the round-trip tests (§10.3, items 1 and 2). Mutation checks by hand: marking one field
   of each owner `NotSaved` in turn (the regrowth clock, the carried machine, pending shots, a held build key,
   spawn protection, the bots' random stream and mode, the relink clock, a machine's action clock, a base's
   reactor remainder and charge, a core tap's hold time) fails the list of marks every time and at least one
   round-trip scenario every time; the core tap scenario had to be changed to be mid-hold at the save to catch the
   last. No simulation change: the fingerprints are as they were.

   Measured in Release on the development Mac, two bots after 1200 ticks (the busy world: 400 machines grown by
   `BrowserStress`, 600 ticks). Times are medians of nine runs.

   | | 480 x 300 | 1500 x 750 | 1500 x 750, 400 machines |
   |---|---|---|---|
   | Raw | 1.58 MiB | 11.0 MiB | 12.4 MiB |
   | Brotli `Optimal` / `Fastest` | 301 / 320 KiB | 1314 / 1340 KiB | 1270 / 1298 KiB |
   | Save | 0.8 ms | 2.9 ms | 19 ms |
   | Load (building the world first) | 0.6 ms (107 ms) | 0.8 ms (434 ms) | 19 ms (431 ms) |
   | Compress `Optimal` / `Fastest` | 3.6 / 1.2 ms | 16.7 / 5.0 ms | 17.7 / 5.9 ms |
   | Decompress | 2.1 ms | 10.5 ms | 11 ms |

   `SmallestSize` gives 6 to 10 % less for 300 times the time (5 s at 1500 x 750), so `Optimal` stays. Saving and
   loading follow the objects, not the map: the bulk arrays copy in a few milliseconds, and the reflection walk
   over machines and links is what grows. On a machine four times slower the busy large world takes about 80 ms to
   save and 50 ms to compress, well inside a hold; building the world the joiner loads into costs more than
   loading. What follows:
   - **The §9.3 limits hold** to about 3.2 million cells (32 MiB at about 10.3 bytes a cell, roughly 2500 x 1300).
     The browser's larger maps (`?map=`, up to 4096 each way) cannot be joined until snapshots are sent as a
     difference (§14), or the cap follows the map size.
   - **The difference from a built world (§14) is not needed sooner**: 1.3 MiB at 1500 x 750 is about a second at
     10 Mbit/s.
4. **Joining, in process** (done, 2026-11-18). `TickBundle.HumanMask`, stamped by `LockstepRoom`, which gained
   `Seat` and `SeatBot` (the relay seats a bot where it used to disconnect an empty slot) and
   `SubmitSnapshotDigest` (two that differ are a desync, like two hashes). `Seats` in Desktop applies mask changes
   before each step. `NetSession` owns it (`StartSeats` replaces setting `Bots` at the start), starts from a loaded
   world (`FromSnapshot`), and answers `SnapshotRequest` once its world is at the tick. The uploader sends chunks of up to
   32 KiB. Both clients report `SnapshotDone` with `WorldSnapshot.Digest` of the raw bytes (the first eight bytes of SHA-256) and the
   sync digest. `LateJoin` is the joiner of §9.4 with the checks of §10.3 item 4 and the seat rule of §6.1. The
   join messages of §4.3 are records in `Tunnerer.Protocol`, with `SnapshotAssembly` (chunks in order, within a
   size) and `SnapshotHold` (one upload, an optional verifier, the offer) for step 5's relay to reuse;
   `ILockstepLink` gained `SendJoin` and `TryReceiveJoin`. `MatchRecording` writes `mask=` lines and a starting
   snapshot (§6.2). The tests' `InMemoryRelay` plays the relay's part: watchers, the hold at the ticks closed when
   `ReadyToLoad` arrives, one upload, the offer once both reports agree, `TakeSeat`.

   Tests: `NetSessionJoinTests` (Slow, a class of its own, about 8 s alone) runs two players and two bots in four
   slots (the "four worlds" of the plan read as four tanks: two clients, with the bots on both), a fifth client
   joins at tick 2400, takes slot 2 and plays 240 ticks; every world saves to the same bytes and the joiner's
   recording rebuilds them. In `NetSessionTests`: the same join at tick 48 with both recordings rebuilt (a player's
   from tick 0, with its `mask=` line); two snapshots that differ only in the thermal ledger, which the sync digest
   does not read, are a desync and the joiner is offered nothing; each of the joiner's checks refuses a broken
   offer; the seat rule and `SeatRefused`. No simulation change: the fingerprints are as they were.

   Notes for what follows:
   - **Ticks are counted as hashes count them** (§4.1): `S` is the ticks closed when the hold starts, the snapshot
     is the world after them, and `Seated` and the new mask are at bundle `S`.
   - **`SeatRefused` names no slot**: the joiner asks for one seat at a time and knows which.
   - **The uploader compresses on the game thread**, inside the hold (17 ms at 1500 x 750, §13 step 3); step 6
     moves compression off the game thread.
   - **Which players**: the in-memory relay asks the first connected player to upload and the second to verify. Step 5
     tracks which slot reported each hash so the relay can choose the players nearest the live tick (§9.2).
   - **A recording holds its snapshot inline**, in base 64 (about 400 KiB of text at 480 x 300), so a desync
     report stays one file that `MatchRecording.Load` reads.
5. **The relay and the codec** (in progress). Protocol 6 carries the human mask in each live and logged tick.
   The join tests now encode and decode the request, snapshot chunks, reports and seat handover. Only a chunk
   may exceed the ordinary 4 KiB frame limit. Rejoining by name continues to use the recorded ticks.

   The snapshot reader resolves type names against the locally reachable types, checks references against the
   receiving field, and charges collection counts against the raw input size before allocating. It also caps
   graph overhead at 262,144 objects and 1,048,576 non-bulk collection elements. These limits apply to loads
   from recordings as well. Before enabling peer uploads on the relay, validate the loaded world's game
   invariants before rebuilding indexes and stepping it. A lone uploader supplies its own hashes.

   Measure the time from `ReadyToLoad` to the first resumed tick with a throttled connection. Exercise the timeout
   and an uploader disconnect before expanding to multiple joiners and rejoin by snapshot. If the uploader leaves,
   discard its partial chunks and request a fresh upload at the same tick, within the original hold deadline.

   Protocol 6 covers watchers, the hold, snapshot transfer and its limits, `TakeSeat`, refusals,
   the snapshot a last player leaves when quitting, and rejoin by snapshot. `MatchRoomTests`: the join order in §5, two
   joiners in one hold, an uploader leaving, the hold timeout, a pause during a hold, a seated late joiner who
   rejoins by name. A socket test in `RelayOverSocketsTests` (marked `Slow`). Deploy the relay to Starfish. The
   relay's 32 MiB cap stays in step with `WorldSnapshot.MaxBytes`. From step 4 it has `LockstepRoom.Seat`,
   `SnapshotHold` and `SnapshotAssembly` to build on, and `InMemoryRelay` as a sketch of the flow. Watcher admission, the
   room clock and transfer budgets belong to the real relay.
6. **The game.** The local seat that can change, the full terrain upload after loading, vision and radar from the
   seat, names from the roster, the screens. Check by hand with three clients on one machine through a local
   relay: start with two in a four-slot room, join with the third ten minutes in. After loading, the client
   rebuilds what Core leaves to it: vision and radar cast from scratch (`SideSensing` is not saved), the whole
   terrain uploaded to the renderer (the change list is not saved), and no step events read from the load tick.
   For browser players to join, check a trimmed WebAssembly build: the local snapshot schema depends on
   reflected types and fields that trimming can remove; a source generator could replace the
   walk, and the coverage test stays as it is.
7. **Docs.** multiplayer-design.md §4 (messages), §5 (late join, and rejoin by snapshot) and §10, and the open
   questions.

## 14. Later

- **Spectating.** A watcher that loads a snapshot and never takes a seat is a spectator. It needs a camera that is
  not tied to a tank, a rule for what fog a spectator sees, and a key to take a seat by choice.
- **A bot holds a vacant seat.** After a player has been gone a while, the relay clears the seat's mask bit and a
  bot drives the tank until the player returns by name, when the bit is set again. The protocol in §4 carries it
  already; it needs the relay's timer and a decision on how long to wait.
- **Snapshots as a difference** from the freshly built world, if large maps make holds too long.
- **Holding for a client that falls behind.** A machine at `r` close to 1 that stalls (a long garbage collection,
  another program) can fall behind in an ordinary match and never draw level, the same problem as §8. The relay
  could hold the match for it, or it could reload a snapshot.

## 15. Open questions

- **How long is a hold in practice?** Measure the steps in §8 on the office line and the slowest machine. If it is
  long on large maps, the save can be spread over ticks or sent as a difference.
- **Order-free or saved?** With hand-written save code, some indexes could be rebuilt on load (the link map's
  per-tile lists, the machine buckets) if nothing depends on their order. With the general saver the question goes
  away.
- **Which seat should a joiner get?** "Most lives, then lowest slot" is a start. Ore, machines and how close the
  bot's base is to tapping the core matter too; a player may rather choose, and the hold gives them a moment to.
- **Should newcomers be allowed at all in a competitive match?** Among friends, yes. A host who disagrees closes
  it with `--late-join=off`.
