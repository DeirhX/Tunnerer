# Finding and starting a network match in game: UX design

Status: proposal, 2026-11-17; the product owner decided every open question on 2026-11-18 (§11). Phase 1 of §10 is built; the
rest is not. It builds on `docs/multiplayer-design.md` (protocol, lobby,
start, end and rejoin rules, relay) and does not restate it.

Today a network match starts only from the command line (`--net=ROOM --name=NAME ...`, README "Network
matches"), with everyone typing the same room name. This document adds a way to see the rooms on the relay, join
one, create one and get back to the list, all from inside the game, for groups of 2 to 8 people.

## 1. Decisions in brief

- **Entry point: the pause menu** gets a "Network match" button (key N). No title screen: the game keeps starting
  straight into a local match, which stays its first impression (open question Q1).
- **Each change between a local and a network match restarts the process**, as "Enter plays again" already does
  (`Program.cs`: the renderer cannot be created twice in one process, and `Game` owns a `readonly World` built in
  its constructor). The menu gathers the choices and the game starts again with the matching arguments. The menu
  never holds a seat, so a restart cannot lose one. A single-process version is a later phase (§10, phase 4).
- **Room list from the relay**, through one new request that needs no hello. Polled every 2 seconds while the list
  is on screen.
- **Host-starts only, no ready toggle.** The lobby shows each player's real state ("building the map" or "ready"),
  which the relay already knows from `Ready`.
- **No chat, no kick, no per-room password, no changing settings in the lobby.** Each one is explained in §5.
- **Rules stay where they are.** The relay enforces who may join. The client's guess, used to grey out a row, comes
  from the same function in `Emberward.Protocol` (`RoomRules`). Core does not change.

## 2. What the current code does that the UX must deal with

Things to know first. The ones marked **(flag)** contradict the docs or are bugs from the player's point of view.

1. **A refusal at launch closes the game without a word (flag).** `Program.cs` joins before the window opens; a
   `RelayException` (room started, full, wrong secret, other build, relay unreachable) prints `[Net] ...` to the
   console and returns. Someone who started the game from a shortcut sees nothing. Once the menu restarts the
   process, this happens whenever the room changes in between, so it has to be fixed first (phase 1).
2. **`Configure` does not exist (flag).** `multiplayer-design.md` §5 step 2 says "On `Configure`, each client
   generates the level"; §4 lists it as "still to add". In the code each client builds as soon as `Welcome`
   arrives, and the room's `MatchConfig` cannot change after the room opens. This design keeps it that way (§5).
3. **"Host" means two things (flag).** README says the host is "the first player in" and that their settings
   decide. In the code the settings belong to the *room* (from the hello that opened it), and the host is
   whoever sits in the lowest occupied slot (`RoomRules.HostSlot`). When the creator leaves, the next player becomes
   host but the creator's settings stay. The lobby should say "settings by the room", not "by the host".
4. **The relay tells a newer client to update itself (flag).** `RelayHub.ServeAsync` answers any other protocol
   with "...Update the game.", even when the client is newer and it is the relay that is out of date. This is
   cheap to fix in the same protocol bump (§7).
5. **Hello means join or create.** A mistyped room name quietly opens an empty room with the typo's settings. Two
   people who both press "Create" with the same name end up in one room with the first one's settings.
6. **A finished room gives up its name** (`MatchRoom.IsOver`). The next hello for that name opens a new room with
   *that client's* proposed config. Joining a finished room from a list must therefore propose the old room's
   settings, or "play again" quietly changes the map size.
7. **Duplicate names get a suffix.** The relay turns a second "Ann" into "Ann (2)". Rejoining needs the suffixed
   name, and the player is never told what it is.
8. **No gamepad support** exists anywhere in Desktop. Menus are drawn on ImGui draw lists by `SessionOverlay` with
   its own press-armed `Button` (a click counts only when the press started on that button) and `HudTheme`.
   `SDL.StartTextInput` is always on, and text events reach ImGui, so `ImGui.InputText` works; nothing uses it yet.
9. **Room names are not case sensitive** on the relay (`OrdinalIgnoreCase`). The same goes for names when
   rejoining.

## 3. The player's flow

```
 local match --Esc--> PAUSED --N--> NETWORK MATCH (list) --Enter on row--------> [restart] -> LOBBY
                                        |   |  \--Q quick match: best open room or a new one -/   |
                                        |   +--C--> CREATE ROOM --Enter--------> [restart] -/    |
                                        +--Esc--> PAUSED                       (host Enter, or full)
                                                                                                  v
   NETWORK MATCH <--L-- RESULT --Enter--> [restart] -> LOBBY of the same room              MATCH
        ^                                                                                         |
        +--Esc-- DISCONNECTED (Enter: [restart] rejoin by name) <---- relay lost -----------------+
```

Every arrow marked `[restart]` uses the existing restart path: the new process gets arguments built by
`NetLaunchOptions.ToArguments()` (new) and the secret through `EMBERWARD_RELAY_SECRET` in its environment, never on
the command line, where `ps` would show it. A refused launch opens a local match with the network menu already
open and the reason on a red plate (item 1 of §2).

### 3.1 Pause menu (existing, one button added)

```
 +-------------- PAUSED --------------+
 |  [ Resume               Esc ]      |
 |  [ Network match          N ]      |   <- new; "Leave network match  L" while in one
 |  [ Controls              F1 ]      |
 |  [ Sound: on              M ]      |
 |  [ Volume  ======o----  70% ]      |
 |  [ Quit game              Q ]      |
 |  Map seed 1234 - replay it with ...|
 +------------------------------------+
```

The panel grows by one button height (`panelH` in `SessionOverlay.DrawPauseMenu`). In a network match the same
slot reads "Leave network match"; it asks for confirmation once the match has started, because the player's tank
will idle and could be lost.

### 3.2 Network match: room list (phase 2; phase 1 shows only the bottom form)

```
 +---------------------------- NETWORK MATCH --------------------------------+
 | You  [ Ann_______________ ]          relay crust.bokovka.cz   * 38 ms     |
 |---------------------------------------------------------------------------|
 |   ROOM            HOST     PLAYERS   MAP        STATE                     |
 | > friday          Bob      2 / 4     480x300    Open                      |
 |   lunch-duel      Cy       1 / 2     320x200    Open                      |
 |   k7qd            Dee      4 / 4     480x300    Playing 12:40   Ann away  |
 |   old-build       Eve      1 / 4     480x300    Other build (3a657b9)     |
 |   tuesday         Fay      -         480x300    Finished: join to replay  |
 |---------------------------------------------------------------------------|
 | Room [ friday____________ ]   [ Join  Enter ] [ Create  C ] [ Quick  Q ]  |
 | Up/Down choose   F5 refresh   Esc back             updated 1 s ago        |
 +---------------------------------------------------------------------------+
```

- **Row**: room name; host (the lowest occupied slot's name); players here / slots; map size; state. Seed and
  generator appear in a hover tooltip, not in the row. Few players care, and anyone who does can still check it.
- **States**, decided by `RoomRules.Describe(listing, myBuild, myName)` (§7):
  - *Open*: in the lobby, a slot free, my build. Joinable.
  - *Full*: in the lobby, no slot free. It starts within moments, so it is shown dimmed and cannot be joined.
  - *Playing mm:ss*: started. Shows "Ann away" for each starting player who is not connected. If one of those
    names is mine, the row is lit and the button reads **Rejoin as Ann**. Otherwise the row is dimmed with "started:
    only a player who dropped out can come back".
  - *Paused*: like Playing, with "paused".
  - *Finished*: the room has given its name up. Joining opens a new lobby **with the old room's settings** and a
    new seed (§2 item 6). It is listed only for the 30 seconds the finished room still exists.
  - *Other build (abc1234)*: dimmed, cannot be joined, tooltip "Everyone in a room must run the same build. This
    room runs abc1234; you run def5678." The short form is the commit part of `NetLaunch.BuildId`.
- **Order**: my build's open rooms first, most players first (where the people are); then playing, finished and
  other builds.
- **Refresh**: one browsing connection while the screen is open, a `ListRooms` every 2 seconds, and F5 at once.
  "updated n s ago" turns amber after 6 s without an answer. Selection follows the room name across refreshes, so
  a row that moves does not change what Enter joins.
- **The room field** is filled from the selected row and can be typed into. Join with a typed name that is not in
  the list asks for confirmation ("No room called 'firday'. Create it?"), which catches typos (§2 item 5).
- **Quick match** joins the open room of my build with the most players. When there is none, it creates
  "&lt;name&gt;'s game" with the defaults. The choice is made on the client from the list; two players racing for
  the last slot are handled by the relay's normal "room is full".

### 3.3 Create room

```
 +------------------------- CREATE ROOM -------------------------+
 | Name      [ Ann's game_______ ]                               |
 | Tanks     <  4  >          bots drive the slots nobody takes  |
 | Map       <  Medium 480x300  >                                |
 | Seed      ( ) new map each match   ( ) fixed [ _______ ]      |
 |                                                               |
 | Every machine must simulate the whole map in real time.       |
 |             [ Create and join  Enter ]   [ Back  Esc ]        |
 +---------------------------------------------------------------+
```

| Setting | Choice | Default | Why |
|---|---|---|---|
| Name | text, 1 to 64 characters | "&lt;name&gt;'s game" | Readable in a list. A typed name that already exists is refused (§7 `HelloIntent`) |
| Tanks | 2 to 8 (`Tweaks.World.MaxPlayers`, `MessageCodec.MaxSlots`) | 4 (`NetworkTweaks.DefaultPlayers`) | The slot count *is* the bot setting: every tank nobody takes is a bot |
| Map | Small 320x200, Medium 480x300, Large 1500x750 | Small for 2 tanks, otherwise Medium (the rule `Program.cs` uses today) | Presets, not free numbers. Large shows an amber warning, because a slow machine falls behind |
| Seed | new each match / fixed | new | Fixed is for replaying a map someone liked |

Not offered: **speed** (`TicksPerSecond` is the simulation rate, a game constant, not a taste setting), **fast
generation** (`GenMode`, a development option; `--fast` still works), **password** (§5), **"bots on/off"** (fewer
tanks is the same thing). The chosen values are remembered for the next time.

### 3.4 Lobby (the existing plate, extended)

```
 +------------- NETWORK MATCH    room friday ---------------+
 | [#] Ann  (you, host)                         ready       |
 | [#] Bob                                      building... |
 | [#] open: a bot plays if nobody joins                    |
 | [#] open: a bot plays if nobody joins                    |
 |----------------------------------------------------------|
 | 4 tanks, 480x300, new map             relay 38 ms        |
 | Press Enter to start with 2 players; bots take the open  |
 | slots. (Starts when Bob's map is built.)                 |
 | Esc menu   L leave                                       |
 +----------------------------------------------------------+
```

- **Swatches** are the slot colours, as today (`SlotColor`). Open slots are dimmed text. Bots get no name until the
  match starts, when the name tags read "Bot", as today.
- **Ready state** comes from the relay (§7 `Roster.ReadyMask`): "building..." until that client has sent `Ready`.
  It answers the only real question in a lobby ("why hasn't it started?") without asking anyone to press anything.
- **"you are Ann (2)"** when the relay changed the name, with "use this name to come back" (§2 item 7). The
  assigned name is saved as the rejoin name for that room (§6).
- **Host handover**: when the host slot changes, a 4-second line reads "Ann left. Bob is now the host and can
  start." The settings line does not change (§2 item 3).
- **Leaving**: L, or the pause menu's "Leave network match", closes the connection and restarts into a local match
  with the room list open. A lobby emptied this way is deleted by the relay, as today.

### 3.5 Result, disconnect and failures

The result screen keeps "Enter plays again in the same room" (`MatchOverlay`) and adds "L room list". Every failure
uses the existing red `##NetStatus` plate, with a second line that says what the player can do:

| Situation | Where it shows | Text and keys |
|---|---|---|
| Relay unreachable | list screen, in place of the rows | "Cannot reach crust.bokovka.cz. Trying again every 5 s." Join is still possible |
| Secret missing or wrong | list screen | a masked **Secret** field appears with the relay's reason; Enter retries |
| Relay protocol older or newer | list screen | "This game speaks protocol 6, the relay 5: the relay needs updating" (or "...update the game") |
| Relay too old to list rooms | list screen | the form without the list, and the line "This relay cannot list rooms." |
| Room full or started (refused at launch) | local match, menu open | the relay's reason; the list is refreshed |
| Room exists (Create) / does not exist (Join) | the same | "A room called 'friday' already exists. Join it?" / "...Create it?" |
| Other build | the same | the relay's reason, plus the short form of both build ids |
| Lost the relay in the lobby | lobby plate | "Lost the relay. Enter: join again. L: room list" |
| Lost the relay in a match | existing plate | unchanged: "Disconnected ... Press Enter to reconnect" (40 s of retries) |
| Desync | existing plate | unchanged; adds "L room list" |

## 4. Room list details that matter

- A **started room is listed** so that a player who dropped out finds their way back without remembering the room
  name. Strangers see that it exists and who is away. That is a little more than they could learn before; see Q8.
- **Rooms of other builds are listed** (dimmed) rather than hidden. "Why can't I see Bob's room?" is the
  commonest question when one person has not pulled the latest build. Seeing it marked "other build" answers that.
- The list covers the whole relay (at most 64 rooms, `RelayHub.MaxRooms`). There is no paging and no search; an
  office has a handful of rooms.

## 5. Decided against, and why

- **Ready toggle.** In a lobby of friends in one room, "press ready" is a step that someone forgets. The host
  starts the match, and a full room starts by itself. The technical ready state (§3.4) covers "is everyone
  loaded".
- **Changing settings in the lobby.** It needs a `Configure` message, every client rebuilding its world, and the
  ready hashes reset. The host who wants other settings presses L and creates another room; everyone else sees it
  appear in their list within 2 seconds.
- **Kick.** It needs an authority the relay does not have (the host is just the lowest slot) and a rule for who may
  kick the host. An idle player cannot block a start, because the host starts without them. Revisit only if a
  stranger with the secret ever becomes a problem.
- **Chat.** The target is people within earshot or on a call. A text field in game also competes with WASD for
  the keyboard. Instead, the lobby shows names and states, and a room name is a free-text message already.
- **Per-room passwords.** The relay's shared secret already keeps outsiders out. Rooms among colleagues have
  nothing to hide from each other.

## 6. Identity

- **Name field** at the top of the network screen, defaulting to `Environment.UserName` (as `Program.cs` does).
  The field is checked as the relay checks it: trimmed, 1 to 64 characters. `--name=` still overrides it.
- **Persistence**: a small `PlayerSettings` file, `settings.ini` under
  `Environment.SpecialFolder.ApplicationData/Emberward`. It holds the name, relay URL, secret, the last create
  settings and **the last match** (relay, room, the name the relay assigned, when). Volume and mute can move there
  later. There is no settings file today; this is the first one.
- **Secret**: asked for only when the relay refuses without it. It is masked in the field and stored in the settings
  file in plain text, which is the same exposure as the `EMBERWARD_RELAY_SECRET` variable people use now. It is
  passed to a restarted process through the environment.
- **Rejoin by name, in the UI**: the list lights "Rejoin as X" when X is the player's name or the assigned name
  saved in "last match". The security note in `multiplayer-design.md` §7 and §11 stands: anyone with the secret who
  knows an away player's name can take their slot. The list now *shows* away names to everyone with the secret,
  which makes that easier (Q8). The token fix described there would plug into "last match" without changing
  this UI.

## 7. Relay and protocol additions (one bump, protocol 6)

The protocol changes are bundled so that the relay on Starfish is redeployed once.

| Message | Direction | Content |
|---|---|---|
| `ListRooms { protocol, secret }` | C to S | Allowed as the **first** message instead of `Hello`. It starts with the kind byte and protocol number, as `Hello` does, so the relay can refuse an old client the same way (`TryReadHelloProtocol` becomes `TryReadGreetingProtocol`). Allowed again on the same connection at most once a second |
| `RoomListing { room, buildId, config, state, names[], away[], ticksClosed }` | S to C | One per room. `state` is lobby, running, paused or finished; `names` is the current roster; `away` lists the starting names that are not connected. One room per message keeps each well under the 4096-byte limit (8 names of at most 192 bytes each, plus room and build) |
| `RoomListEnd { count }` | S to C | Ends one answer, so the client knows a room that is missing has gone |
| `Hello` + `intent` | C to S | `JoinOrCreate` (the command line, unchanged behaviour), `JoinOnly`, `CreateOnly`. Refused with "No room called X" / "A room called X already exists" |
| `Roster` + `readyMask` | S to C | Bit n set when slot n has sent `Ready`, so the lobby can show "building..." |

Relay changes (`src/Emberward.Relay`, protocol only, no Core):

- `RelayHub.ServeAsync`: if the first frame is `ListRooms`, check the secret (same fixed-time compare, same
  refusal), then answer it from a snapshot of `_rooms` taken under the hub lock, and keep answering further
  requests. A browsing connection that sends nothing for 10 s (`HelloTimeout`) is closed. Browsing connections
  count towards the 1024-connection cap; that is enough.
- `MatchRoom.Listing()`: a snapshot under the room lock. It never includes the secret, peers or the input log.
- `RoomRules.CanJoin(state, buildId, slotsFree, away, name)` and `RoomRules.Describe(...)` in
  `Emberward.Protocol`. `MatchRoom.Join` uses `CanJoin` for its decision (the refusal texts stay in the relay), so
  the client's grey rows and the relay's refusals cannot disagree. `RoomRules` is already where "rules both sides
  must agree on" live.
- The protocol-mismatch refusal says which side is older (§2 item 4).
- `/health` is unchanged.

Compatibility: protocol 6 clients and a protocol 5 relay refuse each other at the hello, as every bump does. A 6
client sending `ListRooms` to a 5 relay gets the connection closed without an answer (the 5 relay fails to decode
it). The client reads that as "relay too old to list rooms" and still offers the room-name form, which then fails
with the old relay's protocol refusal. Only a mixed rollout ever sees this, and the order is the usual one: relay
first, then clients. Command-line use (`--net=ROOM`) keeps working and maps to `JoinOrCreate`.

## 8. Input and where the code lives

- **Keyboard first, mouse everywhere.** Every action has a key shown on its button as a keycap (`HudTheme.DrawButton`
  already draws one): Enter join or create, C create, Q quick match, F5 refresh, Up/Down select a row, Tab moves
  between fields, Esc goes back one screen, L leaves. Mouse: click a row to select it, double-click to join, and
  buttons use `SessionOverlay`'s press-armed rule, so a held fire button never presses a menu button.
- **Typing must silence hotkeys.** While `ImGui.GetIO().WantTextInput` is true, `HandleSessionEvent` and
  `HandleEvent` must not act on M, Q, P, N, digits or WASD. Otherwise typing "Quinn" quits the game. This needs a
  test.
- **Gamepad**: none, because the game has none. Each button is also reachable by Up/Down/Enter, so adding a pad
  later means mapping its d-pad and buttons to those keys.

| Where | What |
|---|---|
| `Desktop/Gui/NetMenu.cs` (new) | Draws the list, form, create and confirm screens with `HudTheme`, in the style of `SessionOverlay` |
| `Desktop/Net/NetMenuModel.cs` (new) | The menu as a state machine with no ImGui: screens, selection, validation, what Enter means, which launch it produces. Fully unit-testable |
| `Desktop/Net/RoomBrowser.cs` (new) | The browsing connection: a background task like `RelayConnection`, a 2 s poll, rows handed to the game thread through `Pump()` |
| `Desktop/Net/PlayerSettings.cs` (new) | Load and save `settings.ini`; tests use a temporary directory |
| `Desktop/Net/NetLaunch.cs` | `NetLaunchOptions.ToArguments()` / `Parse`, plus `Intent`. The command-line parsing in `Program.cs` moves into a testable `LaunchArguments.Parse` |
| `Desktop/Game.Net.cs` | Lobby plate: ready marks, assigned name, handover line, L to leave |
| `Desktop/Gui/SessionOverlay.cs` | One button and one action (`SessionOverlayAction.NetworkMatch` / `LeaveNetworkMatch`) |
| `Desktop/Program.cs` | A refused launch opens a local match with the menu and the reason; restart passes the secret in the environment |
| `Protocol/Messages.cs`, `MessageCodec.cs`, `RoomRules.cs` | §7 |
| `Relay/RelayHub.cs`, `MatchRoom.cs` | §7 |
| Core | nothing |

Keep each file under the 700-line cap (`SourceGuardTests`). `Game.Net.cs` is at 289 lines and `SessionOverlay.cs`
at 268, so the menu goes in files of its own.

## 9. The browser client

The browser build has no network play (`docs/browser.md`: "Network multiplayer ... not included"), so none of this
ships there until browser net play is designed. Here is what would carry over and what would differ:

- **Carries over**: the protocol and `RoomRules` (plain .NET, no references); `NetMenuModel` and the
  list ordering and state texts, if they move from `Desktop/Net` to a project both hosts share, as
  `Emberward.Rendering` shares files today; the screen flow of §3.
- **Differs**: the screens are HTML in `wwwroot/main.js`, not ImGui. `ClientWebSocket` works in WebAssembly, but the
  relay must accept the page's origin. There is no process to restart; the page can rebuild its world in place,
  which is the single-process flow desktop only gets in phase 4. Settings go in `localStorage`, and **never in the
  URL**: a shared link may carry the room name (`#room=friday`) but not a name or secret.
- **Cross-play** between browser and desktop is blocked by the build-id check unless both are built from the same
  commit. It also depends on WebAssembly floating point matching native exactly, which nobody has tested yet. The
  start hash would catch a mismatch before the first tick. Until that is shown, the list marks browser rooms as
  another build.

## 10. Phases

Each phase is useful alone. Tests follow the existing style (xUnit, names that state the behaviour). Socket tests
go in `TimedTestGroup` and are marked `Slow` where they wait.

**Phase 1: a menu, no list, no protocol change, no relay deploy.**
The pause menu's "Network match" screen holds a name field, a room field, Join and Create (with the §3.3
settings), and both restart into `--net`. A refused launch comes back to the menu with the reason. The name and
last settings persist. The secret field appears after a secret refusal. The lobby plate gains L to leave and "you
are Ann (2)".
Tests: `LaunchArgumentsTests` (every option parses as today; `ToArguments` then `Parse` round-trips;
the secret is never in the arguments); `NetMenuModelTests` (name and room validation against `MessageCodec`
limits, map presets and the 2-tank default, which launch Enter produces on each screen, Esc goes back one level);
`PlayerSettingsTests` (round trip, a missing or corrupt file gives the defaults, unknown keys are kept);
`TextInputSuppressesHotkeysTests` (with text input active, Q and M do nothing); an extension of
`RelayOverSocketsTests` showing that a refused join yields a `RelayException` with the relay's reason (the launch
code turns it into the menu notice).

**Phase 2: the room list (protocol 6, relay redeploy).**
`ListRooms`, `RoomListing`, `RoomListEnd`, `Hello.intent`, `Roster.readyMask`, `RoomRules.CanJoin/Describe`,
`RoomBrowser`, the list screen, quick match, "Rejoin as", and finished rooms joined with their own settings.
Tests: `MessageCodecTests` (round trip of each new message; the largest possible `RoomListing` fits in 4096 bytes;
oversized lists of names are rejected); `RoomRulesTests` (the table of state x build x free slots x away names ->
joinable and text); `MatchRoomTests` (the listing follows lobby -> running -> paused -> over; away names appear on
leaving and go on rejoining; it holds no secret); `MatchRoomTests` for the intents (`CreateOnly` on an existing
room and `JoinOnly` on a missing one are refused; `JoinOnly` on a finished room is refused; `JoinOrCreate`
unchanged); `RelayOverSocketsTests` (a list over a real socket; a wrong secret is refused before any room is
shown; a browsing connection holds no seat and an idle one is closed; a `ListRooms` of another protocol gets the
update refusal naming the older side); `RoomListViewTests` (sort order, selection kept across refreshes,
"updated n s ago" turning amber).

**Phase 3: lobby polish.**
"building..." and "ready" marks, the handover line, the settings line, "Starts when Bob's map is built", L on the
result screen.
Tests: `LobbyTextTests`, extending the static `LobbyHint`/`NetNotice` pattern into a pure `LobbyView`: hints for
host and guest, a full room, a ready mask with someone building, and a host change.

**Phase 4 (only if the restart flicker annoys people): one process.**
Split `Game` into a window shell (renderer, audio, ImGui, menus) and a match (world, stepper, views) that can be
disposed and rebuilt, so the lobby and the result go back to the list without closing the window. This is a real
refactor of `Game.cs`, `TerrainAuxUploader` and the fog layers, which are sized per world. It changes no protocol.
Tests: building two matches of different sizes in one shell leaves no state behind (a world hash the same as a
fresh process), plus the existing determinism tests.

## 11. Open questions for the product owner

All decided on 2026-11-18: every recommendation was accepted.

| # | Question | Decision |
|---|---|---|
| Q1 | A start menu at launch (Play / Network match / Quit), or only the pause menu entry? | Decided 2026-11-18: pause menu only for now. The game opening straight into a match is part of its feel. Add a start menu if office players mostly launch to play together |
| Q2 | Ready toggle? | Decided 2026-11-18: no. Show the technical "building / ready" state instead (§5) |
| Q3 | Host kick? | Decided 2026-11-18: no (§5) |
| Q4 | Chat? | Decided 2026-11-18: no (§5) |
| Q5 | Per-room passwords? | Decided 2026-11-18: no; the relay secret is enough for office play |
| Q6 | Store the relay secret in the settings file? | Decided 2026-11-18: yes, masked in the UI, plain in the file, the same as the environment variable today |
| Q7 | Accept a process restart on every local/network switch? | Decided 2026-11-18: yes for phases 1 to 3; phase 4 only if it bothers people |
| Q8 | Show away players' names in the list, which makes rejoin-by-name theft easier? | Decided 2026-11-18: yes. Among people who share the secret the convenience wins. Revisit together with the rejoin token if the relay ever leaves the office |
| Q9 | List rooms of other builds? | Decided 2026-11-18: yes, dimmed with the build id. It explains "where is Bob's room" |
| Q10 | Offer the Large map in Create? | Decided 2026-11-18: yes, with the warning. Nobody has measured the slowest office machine (`multiplayer-design.md` §11) |
| Q11 | Default room for quick match when nothing is open: "&lt;name&gt;'s game" or the old shared `lobby`? | Decided 2026-11-18: "&lt;name&gt;'s game". It shows who opened it, and `--net` without a room still means `lobby` |
