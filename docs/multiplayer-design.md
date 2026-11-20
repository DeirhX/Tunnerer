# Multiplayer for small groups: design

Status: built and in use, 2026-11-17 (the build order in §10 says what is done). Target for the first tranche: four people in one office,
each on their own machine, connecting out through the office firewall to a relay on the Starfish server.

What is still undecided is listed in [Open questions](#11-open-questions).

## 1. Summary

- **Deterministic lockstep, paced by the server.** Every client runs the whole simulation. Only inputs travel.
- **The server is a relay.** It owns the room, the clock and the input log. It does not run the game.
- **Outbound WebSocket over TLS on port 443.** Clients never accept a connection and never talk to each other, so
  the firewall sees ordinary HTTPS traffic to one host.
- **Core barely changes.** `World.Advance(Func<int, ControllerOutput>)` is already the lockstep interface. The
  work is a relay, a client session in Desktop, and removing the "player is tank 0, tank 1 is the bot"
  assumption.

## 2. Why lockstep

| Fact about the code today | Consequence |
|---|---|
| Fixed 24 Hz step; same seed and inputs give the same world on every OS (`DeterminismTests`, `ReplayTests`, `SimulationFingerprintTests`, `DeterministicMath`) | Clients can each simulate and stay identical. The hard part of lockstep is already paid for and guarded by tests. |
| A tick's whole input is one `ControllerOutput` per tank | About 4 bytes per player per tick on the wire. |
| The world is a grid of up to 1500 x 750 cells with heat, meant to grow | Sending state (snapshots, deltas) is expensive and gets worse with map size. Sending inputs costs the same on any map. |
| There is no save format or world serializer (DESIGN §11, gap 6) | Server-authoritative state sync and rollback both need one. Lockstep does not. |
| `VisionMap` is output only | Each client tracks fog for its own tank; nothing about vision crosses the network. |

Rejected for now:

- **Server-authoritative simulation with state replication.** Needs serialization, delta compression and
  interest management for the heat field. Far more work, and bandwidth grows with the map.
- **Rollback (GGPO style).** Needs cheap snapshot and restore of the world every tick. The heat grid makes that
  costly. Input delay over an office link is small enough not to need it.
- **Peer to peer.** Blocked by the firewall, by requirement.

What lockstep costs: every client holds the full world, so a modified client could see through fog. That is
acceptable among friends and is not defended against.

## 3. Topology

```
 office firewall (outbound 443 only)
 +--------------------------------+
 |  client A --+                  |
 |  client B --+-- wss://  -------+-----> Starfish: Emberward.Relay
 |  client C --+                  |         room "K7QD": roster, config, tick clock, input log
 |  client D --+                  |
 +--------------------------------+
```

One relay process hosts many rooms. A room holds up to 8 slots (`Tweaks.World.MaxPlayers`); the first tranche
is tuned and tested for 4.

## 4. The tick protocol

The server is the clock. Clients never decide which tick an input belongs to.

1. Each client sends one input for every tick it simulates: `Input { encoded }`. Tying sends to ticks received,
   not to the local clock, keeps every client sending at exactly the server's rate.
2. The server keeps a short FIFO per slot (cap 4). Every 1/24 s it closes a tick: it takes one input from each
   slot's queue, or repeats that slot's last input if the queue is empty, and broadcasts
   `Tick { n, inputs[slots] }` to everyone, including the sender.
3. A client steps tick `n` only when it holds `Tick n`, calling `World.Advance(i => bundle.inputs[i])`. The local
   player's own tank is driven by the bundle too, never by the keyboard directly.

Properties:

- **A slow client never stalls the others.** Its tank repeats its last input until its packets arrive, for
  half a second at most; then it idles (§5).
- **No key press is dropped.** Core detects key presses as edges between consecutive inputs
  (`TankBuildController`, `Tank` upgrades), so the server queues inputs instead of keeping only the newest.
- **A backlog drains.** A queue more than one deep means that player's inputs are played late, and at one input
  per tick it would never shrink. While a slot has a backlog the server skips an input whose keys equal the
  next one's (the match settings carry the mask that says which bits are keys), so only an intermediate aim is
  lost. If a queue still overflows, the relay drops an input whose keys the next one repeats, which loses no press or
  release, and only if there is none the oldest input, which it counts.
- **The tick rate is a match setting**, not a constant in the relay: changing the simulation rate needs no new
  relay.
- **Input delay** is uplink + the wait for the relay's next tick + the client's own step phase + downlink. The
  client keeps no bundle back (`BundleBuffer.TargetDepth` is 0), so an input is simulated at the first local step
  after its bundle arrives. Measured on localhost with `EMBERWARD_NET_LATENCY_LOG=1`: mean 44.6 ms, 91% of inputs
  one tick (a depth of 1 gave a mean of 56.5 ms). Entity interpolation already hides one step.
- **Pacing on the client.** `GameFrameCoordinator` today steps on the wall clock. In a net session it steps when
  a bundle is available, and uses the existing "at most 4 steps to catch up" path when bundles arrive in a burst.
  With no buffer, a bundle that arrives late stalls one step; a link with that much jitter may need
  `TargetDepth` back at 1. If the buffer keeps growing the client steps faster until it is level.

### Input encoding

`ControllerOutput` packs into 4 bytes:

| Field | Bits |
|---|---|
| `MoveSpeed` x, y (each -1, 0, 1) | 4 |
| `ShootPrimary`, `BuildPrimary`, `BuildSecondary`, `BuildTertiary` | 4 |
| `Upgrade` (0 to 4) | 3 |
| `AimDirection` as an angle, quantised to 16 bits | 16 |

The aim is quantised **before** anyone simulates with it, and decoded through `DeterministicMath`, so every
client, the sender included, feeds identical floats to Core. The codec lives beside `ControllerOutput` in Core
with a round-trip test, and the local single-player path should go through the same quantisation so replays and
net play agree.

### Messages

Binary frames, one message per WebSocket frame, `TCP_NODELAY` on.

| Direction | Message | Purpose |
|---|---|---|
| C to S | `Hello { protocol, buildId, name, room, config, secret, snapshots? }` | Join a room, or open it with `config` if it does not exist. The kind byte and `protocol` come first in every protocol version, so the relay can refuse an old client before reading the rest |
| S to C | `Welcome { slot, config }` | Slot assignment and the room's match settings. A snapshot-capable newcomer gets slot -1 until seated |
| S to C | `Roster { names[] }` | Who is in each slot; sent on every change |
| C to S | `Ready { worldHash }` | World built from the config; hash of tick 0 |
| C to S | `StartRequest` | From the host (lowest slot): start with the players who are here |
| S to C | `Start { humanMask }` | Everyone here is ready and agrees; the other slots are bots |
| C to S | `Input { encoded }` | One per tick simulated |
| S to C | `Tick { n, inputs[], humanMask }` | Inputs and seat ownership for this tick |
| C to S | `Hash { n, hash }` | Once a second |
| S to C | `Desync { n }` | Hashes differed at tick `n` |
| S to C | `Refusal { reason }` | Cannot join, or the match cannot start; the reason is for the player. The connection closes after it |
| S to C | `Rejoined { humanMask }`, `Log { firstTick, slots, inputs[], humanMasks[] }` | To a returning player: the match is on, and every tick so far in runs of 64 |
| S to C | `JoinHoldState { name }` | The match is held for this newcomer. An empty name releases the join hold |
| C to S | `SetPaused { paused }` | Hold the match for everyone, or let it run |
| S to C | `PauseState { paused, bySlot }` | The match is held or running, and who asked |
| C to S | `SetClosedToNewcomers { closed }` | The host closes the running match to newcomers, or opens it again (late-join-design.md) |
| S to C | `NewcomersState { closed }` | Whether the running match is closed to newcomers; sent on each change and to a returning player |
| both | `Ping { id }`, `Pong { id }` | Times the round trip, once a second |
| C to S | `MatchOver { n }` | By this client's simulation the match was decided at tick `n`; the room is finished |

Still to add: a lobby `Configure`.

Bandwidth: 4 players x 4 bytes x 24 Hz is under 0.5 kB/s down per client.

## 5. Session lifecycle

1. **Lobby.** The first player into a room opens it, and their seed, map size and tank count become the room's.
   Everyone sees the roster, slot by slot in tank colours. The host is the player in the lowest slot.
2. **Build.** On `Configure`, each client generates the level locally from the config and reports the hash of
   the initial world. Level generation in `--fast` mode produces a different map from the same seed, so
   `genMode` is part of the config, not a local flag.
3. **Start.** A full room starts by itself; the host can start earlier with Enter. Either way the relay waits
   until everyone present is ready with the same hash. Slots nobody took are played by bots, which every client
   runs for itself from the match seed. A hash mismatch here means different builds or a determinism bug and
   is reported before play begins.
4. **Play.** As §4.
5. **Pause.** Escape opens the local menu without stopping the simulation. P pauses for everyone: the relay
   stops closing ticks, and its clock does not count the pause, until anyone presses P again. On the pause menu the host's **C** closes the match to new players, or opens it again.
6. **End.** Core reports the outcome; clients show it and tell the relay (`MatchOver`), which does not simulate
   and would not know. The first report is believed, for a tick the relay has closed. A finished room closes
   ticks for 30 more seconds, for the result screens, and then its clock stands. It gives its name up: the next
   hello for that name opens a new room, and an empty finished room is deleted at once, with no grace.
7. **Again.** Enter on the result screen starts the game again with the same arguments. The first player to do
   so opens the room anew, with a new seed unless `--seed` fixed it, and is its host; the others join that
   lobby with Enter. A player still looking at the old result stays connected to the old room, which is frozen.

### Disconnect and rejoin

- **A silent client's tank idles.** A slot with nothing queued repeats its last input, but only for half a
  second (`MatchRoom.SilentSeconds`); then it plays the neutral input until the client sends again. A client
  that has fallen behind stops sending while it catches up, and without this limit its tank would drive and
  fire on with the keys it last held.
- A dropped client's slot keeps receiving neutral input (no move, no fire). The room continues. The relay finds a
  client that vanished without closing (the connection to Caddy stays up, so TCP says nothing) by WebSocket
  pings: after seven seconds of silence from it the relay pings, and with no answer within ten more it ends the
  connection, which frees the slot. The pings are WebSocket control frames, answered by the client's socket on
  its own background task, so a client whose game loop is busy for a long time (building a large world) is not
  dropped. The client does the same towards the relay and says "lost the connection to the relay" on screen.
- **A room nobody is in waits.** A started match with every player gone keeps its log and its place for five
  minutes (`RelayHub.EmptyRoomGrace`). Its clock does not run meanwhile, so bots do not play on alone and a
  returning player does not face minutes of catch-up; the next tick is the one after the last. After the grace
  the room is deleted. A room still in the lobby is deleted as soon as it is empty. The match time lives in the
  room (`MatchRoom.AdvanceClock`); the hub only measures wall time, so the pause and empty rules are testable
  without a timer.
- **Rejoin by replay.** The server holds the whole input log: about 1.4 MB per hour for four players. A
  player who dropped out starts the game again with the same room and name, receives the settings and the
  log, rebuilds the world from the seed and replays it, a frame's budget of ticks at a time, without sound and
  without sending input, until it reaches the live tick. (A name, not a token: among friends that is enough,
  and it survives restarting the game.) A step on the default map measured about
  10 ms on a development Mac (2026-11-17, mostly the heat solver), a quarter of a tick, so ten minutes of play
  replays in about two and a half. That is tolerable for a short match and too slow for a long one.
- **Coming back after a lost connection.** The client says "Disconnected ... Press Enter to reconnect"; Enter
  starts the game again with the same arguments, which rejoins as above. The relay may not have found the old
  connection dead yet, and the slot is taken until it has, so the returning game keeps trying for 40 seconds
  (`NetLaunch.RejoinPatience`) before it gives up. The window is closed meanwhile; the console says why. If
  the relay itself was restarted the room is gone, and the player opens a new lobby instead.
- **Late join** of a new player is the same mechanism plus a free slot, but a tank and base cannot appear
  mid-match in Core today. Not in the first tranche. [late-join-design.md](late-join-design.md) proposes taking
  over a bot's tank instead, with the world copied from a snapshot while the match holds for a moment.
- A world snapshot (which the sector save in DESIGN §12.2 needs anyway) later replaces the fast-forward.

## 6. Staying in sync

- **Build identity.** `buildId` is the Core assembly's version, which the SDK stamps with the git commit. The
  relay refuses to mix builds in a room. It does not see uncommitted changes; the hash each client reports
  before the first tick catches a world that differs anyway.
- **Periodic hash.** Each client sends a digest once a second; the server keeps the first one reported for each
  tick for the whole match (a few thousand numbers an hour) and compares every later report for that tick with
  it, so a client far behind, replaying to catch up, is checked too. A report for a tick the relay has not closed
  yet is ignored. The digest (`WorldStateHash.ComputeSync`) is the state hash the determinism tests use, plus
  what that hash does not read, in five parts: every cell's heat and air temperature, in cell order, and each
  base reactor's exact heat (where heat is, not only how much); every base's and battery's charge; every base's
  ore, upgrades and whether a bot plays it; the race to the core and the result (which decide the winner without
  passing through anything else); and latent state that shows elsewhere only later or on a rare event (random
  generators, timers, held keys, harvester memory, pending projectiles, which heat tiles are awake, the regrowth
  clock, scorch). [late-join-design.md](late-join-design.md) §10 says why each is there.
- **Its cost does not yet follow activity.** Both the state hash and the per-cell heat hash walk the whole grid
  once a second. The per-cell hash runs in four independent lanes and costs less than the energy total it replaced
  (0.39 ms against 1.32 ms on 1500 x 750, Release, development Mac). That is small on today's maps and will not be on much larger ones; summing per active tile, or
  hashing the terrain in turns, is the fix when maps grow.
- **On desync** the match stops and every client writes `artifacts/debug/desync_<room>_tick<n>_slot<k>.txt`:
  the digests it reported around the tick, split into state, heat, charge, economy, outcome and latent, where its tanks stand, and the
  match as a `MatchRecording` (settings, which slots were players, every tick's inputs). Comparing two
  players' files shows when and in what the worlds went apart; `MatchRecording.Load(...).Replay()` rebuilds the
  match tick by tick in a test. `EMBERWARD_NET_FAULT_TICK=n` makes one client diverge on purpose, to exercise
  this.

Determinism risks to check before trusting it:

- The parallel path (`TerrainGrid.HeatTiles`) is documented as deterministic; a four-instance soak test
  with mixed `--fast` settings should confirm it.
- Mixed CPU architectures (Apple Silicon and x64). .NET does not fuse multiply-add on its own and transcendentals
  go through `DeterministicMath`, so this should hold, but it has only been tested per OS, not per architecture
  in one match. The start hash and the periodic hash will show it immediately if not.

## 7. The relay

- **`src/Emberward.Protocol`**: the room's tick rule, message types and the binary codec. No dependencies
  (`ArchitectureGuardTests`). Referenced by the relay, Desktop and the tests.
- **`src/Emberward.Relay`**: a small ASP.NET Core (Kestrel) app with one WebSocket endpoint, `/play`, and
  `/health`. Rooms are in memory; a started room is deleted when it has been empty for five minutes, a lobby as soon as it is
  empty. Each started room has one timer loop, which only measures wall time for the room.
  It never references Core, so it never needs redeploying for a gameplay change, only for a protocol change.
- **Deployment on Starfish** (inspected 2026-11-17). Starfish is an Azure VM in North Europe: Standard_D2s_v3
  (2 vCPU, 8 GB, about 6.5 GB available), Ubuntu 24.04 x64, 7 GB of disk free. Caddy 2.11 owns ports 80 and
  443, gets certificates automatically and reverse-proxies each site in `/etc/caddy/conf.d/*.caddy` to a
  service on `127.0.0.1`. Services are systemd units, each with its own user, under `/srv/<name>/`. There is no
  Docker and no .NET runtime. The relay follows the same pattern and has been running there since 2026-11-17:
  - Binary: `/srv/emberward/relay/Emberward.Relay`, a self-contained single file.
  - Service: `emberward-relay.service`, user `emberward-relay`, `Restart=always`, on `127.0.0.1:8130`.
  - Site: `/etc/caddy/conf.d/crust.bokovka.cz.caddy`, `reverse_proxy 127.0.0.1:8130`. Caddy issued the
    certificate itself and proxies WebSockets with no extra configuration.
  - Clients connect to `wss://crust.bokovka.cz/play`; `https://crust.bokovka.cz/health` says it is up and how
    many rooms are open.
  - To update it (only needed when `Emberward.Protocol` or `Emberward.Relay` changes, with a higher
    `MessageCodec.ProtocolVersion` if the bytes changed):

    ```
    dotnet publish src/Emberward.Relay -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=none -o /tmp/relay-publish
    scp /tmp/relay-publish/Emberward.Relay Starfish-azureuser:/tmp/
    ssh Starfish-azureuser 'sudo install -o root -g emberward-relay -m 750 /tmp/Emberward.Relay /srv/emberward/relay/Emberward.Relay && sudo systemctl restart emberward-relay && rm /tmp/Emberward.Relay'
    ```

    A restart ends any match in progress.
  - Secret: `/etc/emberward/relay.env` (root, readable by the service's group) sets `EMBERWARD_RELAY_SECRET`,
    and the unit loads it with `EnvironmentFile=`. Read it with
    `ssh Starfish-azureuser 'sudo cat /etc/emberward/relay.env'`; to change it, edit the file and restart the
    service. Players set the same value in `EMBERWARD_RELAY_SECRET` or pass `--secret=`.
- **Capacity.** The relay's work is one timer and a few small sends per room per tick, so the VM is ample. One
  thing to settle first: `litestream` (the neride service's database replication) has averaged a full core
  since it started, leaving one of the two cores busy. The relay will still run, but a 24 Hz timer on a
  machine with a pegged core will show more jitter than it should.
- **If the office proxy blocks WebSocket upgrades**, the fallback is the same messages over HTTP long-polling;
  the transport is behind an interface on both ends to allow that.
- **Access.** When the relay's `EMBERWARD_RELAY_SECRET` is set, a client must send the same secret in `Hello`
  (`--secret=` or the same environment variable on the client). Unset, the relay is open. Room names are not
  security. A rejoin is by name only (no token): anyone who knows the room, the secret and a dropped player's
  name can take that slot, which among friends is accepted (see the open questions).
- **Limits.** What exists, and nothing else:
  - a message is at most 4096 bytes, and every length inside it is checked while decoding;
  - at most 1024 connections are open at once, greeted or not;
  - two players in a room never share a name: the second "Ann" is "Ann (2)", and comes back under that name;
  - names, room names and build ids are cut to 64 characters (a longer one in a hello is rejected), a refusal reason to 240, the shared secret to 256
    (the relay will not start with a longer one);
  - a match has 2 to 8 slots, maps of 64 to 4096 cells a side and 10 to 120 ticks a second, or the hello is
    rejected;
  - a connection may send at most 2000 messages a second (an honest client sends about 26, and a burst of 240
    while catching up) or it is ended;
  - a slot queues at most 4 inputs;
  - at most 64 rooms are open at once;
  - a client has 10 seconds to say hello, and a silent connection ends as described in §5;
  - a room nobody is in is kept 5 minutes once its match has started, and not at all once the match is over;
  - a finished match closes ticks for 30 more seconds.

Since Core has no platform dependencies, the relay could later run the simulation headless to arbitrate hashes
or serve snapshots. The first tranche does not need it.

## 8. Changes on the client

Desktop:

- `Net/RelayConnection`: `ClientWebSocket` on a background task, handing decoded messages to the game loop
  through a queue. No simulation state is touched off the main thread.
- `Net/NetSession`: lobby state, the bundle buffer, input sending, hash reporting, rejoin fast-forward.
- `GameSimulationStepper` takes an input source per slot instead of "slot 0 is the keyboard, the rest is the
  bot": local keyboard (sent to the relay), remote (from the bundle), or bot.
- A **local slot** replaces tank 0 wherever Desktop means "the player": camera, HUD, `TrackVision`,
  `TrackRadar`, audio listener, tutorial, `MatchOutcomeFor`. About a dozen call sites.
- Lobby screens with `HudTheme`: create or join, roster, host settings, ready state, connection quality.
- Name tags and distinct colours for four tanks; a latency and "waiting for server" indicator.
- Debug hotkeys that would change the simulation are off in a net session. Render-only toggles stay.

Browser (docs/browser.md, Network play):

- It runs the same `NetSession` and `SlotStepper` (shared through `Emberward.Rendering`) and keeps what the relay
  said in the protocol's `RoomClient`, which the desktop's `RelayConnection` also wraps. The page's WebSocket only
  carries bytes; `BrowserNetMatch` makes and reads the messages. A browser and a desktop of one commit share a build
  id and compute the same world, so they play in one room.

Core:

- The input codec (§4).
- `ComputeWorldStateHash` and the cheap per-second variant (§6).
- Spawn count from the match config. `Tweaks.LevelGen.MaxPlayers` is 2 today and `ToastGenerator.SelectSpawns`
  needs to place four bases with sensible spacing; on the default map the spawn-distance rule already struggles
  to fit two, so four players need a larger default map or a smaller distance.
- Match rules for more than two tanks (§9).
- Bots stay as they are: `BotTankAI` is seeded and runs identically on every client, so empty slots can be bots
  with no network traffic.

## 9. Game mode

The transport does not care, but Core's match rules do. `IsMatchOver` ends the match when **any** tank is out of
lives, and each tank has its own base and its own core-tap goal.

Decided for the first tranche: **free-for-all race**. Four tanks, four bases, first base to tap the core
wins, a tank out of lives is out. It needs only a spawn count and a small rewrite of `IsMatchOver` and
`MatchOutcomeFor`, and it tests the network under real play.

Co-op (two or four tanks sharing a base, radiator and network) fits DESIGN.md better, since the game is moving
away from the duel, but it needs a notion of *side* separate from *tank* across bases, links, coverage, vision
and the goal. It should be the second tranche, designed with DESIGN §14's open question on co-op against
competitive.

## 10. Build order

Each step is testable without the next.

1. **Core groundwork** (done). Input codec (`ControllerOutputCodec`), state hash in Core (`WorldStateHash`),
   per-slot input sources in the stepper, local slot in Desktop, bots that pick their enemy the same way on
   every machine (`BotTankAI.PickEnemy`). The recorded fingerprints are unchanged: the local player's aim is
   quantised in Desktop's stepper, which the fingerprint tests do not go through.
2. **Lockstep in process** (done). `Emberward.Protocol` holds what client and relay share: `LockstepRoom` (the
   relay's tick rule, input log and hash comparison), `TickBundle`, `BundleBuffer` (the client's buffer and
   pacing rule) and `ILockstepLink` (the connection as the game sees it). `NetSession` in Desktop steps a world
   from bundles. `NetSessionTests` runs four worlds through an in-memory relay with uneven delays, a client
   that stalls and bursts, and one that drops out, then checks the worlds are identical and that the relay's
   log rebuilds the same world. It needs no socket. Not yet wired into the game loop: that comes with the
   connection in step 3.
3. **Relay and transport** (done and deployed). The message codec, `Emberward.Relay` (`MatchRoom`,
   `RelayHub`), `RelayConnection` and the game loop's network path (`Game.Net.cs`). Started from the command
   line with `--relay`, `--room`, `--players` and `--name`; there is no lobby screen yet. Two game clients on
   one machine reported the same world hashes through a local relay and through the relay on Starfish.
   Playing from the office network is still to do.
4. **Four players** (done; the relay on Starfish runs protocol 2 since 2026-11-17). The level generator places any number of bases
   (`ToastGenerator.SelectSpawns`); `MatchReferee` decides a match of any size, and in a network match the
   first base to tap the core wins (`MatchRules.CoreTapWins`); a lobby plate, host start, bots in open slots, name
   tags over tanks in sight, and a warning when a machine falls behind. Joining is still by command line
   (`--net=ROOM`); a menu for it is not built. Protocol version 2.
5. **Robustness** (done; the relay on Starfish runs protocol 3 with a secret since 2026-11-17). The sync digest with heat and
   charge, the desync report and `MatchRecording`, rejoin by name with catch-up, pause for everyone, the
   round-trip time on screen, and the relay's shared secret. Protocol version 3.
6. **Review fixes** (done). A started room survives being empty; dead connections
   are found within about 20 seconds; the match result is fixed once decided (`World.MatchWinner`); the sync hash
   covers the whole match; a full input queue loses no key edge; the secret has its own limit; a client of
   another protocol is told to update; limits on match settings and message rate. Protocol version 4.
7. **Match end and silent clients** (done; the relay on Starfish runs protocol 4 and has to be updated to
   protocol 5 before clients of this build can join). Clients report the end of the match, a finished room gives
   its name up so Enter plays again in the same room, and a connected slot that sends nothing for half a second
   idles. Protocol version 5. With it: Enter reconnects after a lost connection, the sync digest covers base
   ore and upgrades, names in a room are unique, the round trip is timed to the answer's arrival and not to the
   next simulation step, and the relay caps its connections.
8. **Browser client** (done, 2026-11-18). The browser joins rooms from its welcome screen, with lobby, host start,
   pause for everyone and rejoin by name, and plays with desktop clients of the same commit. No protocol change.
9. **Late-join transport and seat handoff** (done; the relay on Starfish runs protocol 6 since 2026-11-19).
   Protocol 6 carries world snapshots and tick-by-tick seat ownership. The player-facing join flow is still pending;
   see [late-join-design.md](late-join-design.md). Later: co-op sides and play from outside the office.

10. **Fourth build key** (protocol 7 in the local-generator tranche). Bit 28 of encoded player input is a build
    key, and the match's input key mask includes it so build presses are never predicted as aim changes.
    The relay must be updated before clients of this build can join a public match.
11. **Machine upgrade key** (protocol 9 in the per-machine upgrade tranche). Bit 30 of encoded player input says
    the upgrade key (bits 8-10) is for the machine next to the tank, not the base; it is in the input key mask.
    The relay must be updated before clients of this build can join a public match.
12. **Fifth build key** (protocol 10 in the turret tranche). Bit 31 of encoded player input builds a turret in
    energy matches; it is in the input key mask. It is the last free bit: the next input needs a wider encoding
    or a reused bit. The energy rules' version in the rules hash changed with it. The relay must be updated before
    clients of this build can join a public match.

## 11. Open questions

- **Rejoin by name is not authenticated.** Anyone with the relay's secret who knows a dropped player's name can
  take their slot. A token handed out in `Welcome` and kept by the client would close it; not done, because
  among friends the name is enough and the token would have to survive restarting the game.
- **Does the office network pass WebSockets to Starfish?** One `wss://` echo test from an office machine
  answers it. Latency from the office to North Europe should be measured at the same time.
- **Are all four machines the same OS and architecture?** It decides how early the mixed-architecture check in
  §6 matters.
- **Map size for four.** The default is 480 by 300, chosen to be cheap: every player's machine must simulate
  the map in real time, and one that cannot falls steadily behind (it is told so on screen). The timings in
  this document come from a fast development Mac; the default needs checking on the slowest machine that
  will play.
