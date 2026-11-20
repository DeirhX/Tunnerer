# Emberflow

A 2D tunnelling tank game. You dig through the ground, fight an enemy tank, and manage heat: digging and firing
heat your tank and the rock around it, your base is the heat sink, and relays pipe heat back to the base.

Written in C# on .NET 10. The simulation (`Emberward.Core`) has no platform dependencies; the desktop client
(`Emberward.Desktop`) uses SDL3 for the window, input and sound, SDL_GPU for rendering and Dear ImGui for the
HUD. The sound effects are synthesised in code when the game starts; there are no sound files.

The browser client (`Emberward.Browser`) runs the same Core simulation in WebAssembly, with the desktop's shaders
and shared rendering code drawn through WebGL 2,
keyboard/mouse and touch controls, and offline installation as a progressive web app. It currently supports
a local match against a bot. See [Browser play and deployment](docs/browser.md).

### Play in a browser

```sh
python3 tools/browser/publish.py --restore-workloads
python3 tools/browser/serve.py
```

Open [localhost:8703](http://localhost:8703). Once the header says **SAVED FOR OFFLINE**, the complete game is cached.
Use **Install game** in Chrome/Edge, or your browser's install / Add to Home Screen menu. Building requires
.NET 10 and Python 3 (`python` or `py` on Windows) and works on Windows, macOS and Linux; players need only a
modern browser. Upload `artifacts/browser/wwwroot` to an HTTPS static
host to share the game; the publish also creates `artifacts/browser/emberward-browser.zip`.

## Requirements

- .NET 10 SDK
- Windows 10 or later, or macOS (tested on Apple silicon). Linux should work but is untested.
Everything comes from NuGet, including the shader compiler (SDL_shadercross).

### Renderer

Rendering goes through SDL_GPU: Metal on macOS, Direct3D 12 or Vulkan on Windows, Vulkan on Linux. The HLSL in
`src/Emberward.Desktop/Rendering/Shaders` is compiled to DXIL, SPIR-V and MSL at build time.

## Build, run, test

```
dotnet build Emberward.slnx
dotnet run --project src/Emberward.Desktop
dotnet test Emberward.slnx
```

The tests cover `Emberward.Core`, the Desktop logic that needs no window, the protocol and the relay, and need neither a GPU nor the shader compiler when run per project:
`dotnet test tests/Emberward.Tests`.

Tests that take more than about five seconds carry `[Trait("Category", "Slow")]`. For a quick run while
working, skip them with `dotnet test Emberward.slnx --filter "Category!=Slow"`; CI and the pre-commit run use the
full suite.

## Controls

| Key | Action |
|-----|--------|
| W A S D | Drive and dig |
| Mouse | Aim the turret |
| Left click / Space | Fire |
| E | Build a relay (4 ore); press again to plant it |
| R | Build a harvester (30 ore); press again to plant it |
| Y | Build a local generator (18 ore) in energy matches; plant it next to hot ground |
| U | Build a turret (24 ore) in energy matches; plant it in your network's coverage, where it fires at enemy tanks within 24 cells |
| 1-7 | Docked at your base: buy an upgrade (1-5 base track, 6-7 tank track) |
| M | Mute / unmute the sound |
| - / = | Volume down / up |
| F9 | Thermal view |
| G | Charge labels over machines: charge / capacity and flow in and out a second (only with `--energy-network`; on by default) |
| F, then 1-3 | Beside one of your relays or turrets: open its upgrade panel, then buy Tap, Sensor or Conduit (relay) or Rapid or Long (turret) from the base ore store (only with `--energy-network`); F again closes it |
| X | Off the network: burn a unit of emberite from the hold for 5 s more reserve (only with `--energy-network`; hold to burn 4 a second). Once the reserve has run out and the hold is empty: give up and respawn |
| Esc / P | Pause menu (resume, controls, sound on/off, volume slider, quit) |
| F1 | Controls screen |
| F10 | Screenshot (`screenshots/` next to the executable) |
| F11 | Renderer debug overlay; 1-7 then toggle shader passes |
| F12 | Start / stop recording input for a scripted replay |

The game also pauses when its window loses focus.

## Command line

Run `Emberward.Desktop --help` for the full list. The common ones:

| Option | Effect |
|--------|--------|
| `--seed=N` | Play the map with seed N. Without it every match gets a new map; the pause menu shows the seed. |
| `--windowed[=WxH]` | Resizable window instead of borderless fullscreen |
| `--display=N` | Open on display N (or `secondary`) |
| `--size=WxH`, `--large` | Terrain size |
| `--no-ai` | No enemy tank |
| `--no-fog` | No fog of war: the whole map is shown live |
| `--energy-network` | Local matches run the energy network (DESIGN.md §4.9, off by default): machines hold charge, local generators supply their islands, labels show charge and production (G hides them), relays take per-machine upgrades (F beside one), and the off-network clock becomes the tank's reserve (X burns carried emberite; running out goes through warning, degraded and stranded before heat death). A network match keeps the rules its room agreed on. In the browser, add `?energy` to the address |
| `--net[=ROOM]` | Join ROOM (default `lobby`) on the shared relay; the first player in opens it and their settings apply |
| `--relay=URL` | Use the relay at URL instead (`ws://` or `wss://`, ending in `/play`) |
| `--room=NAME` | The room to join, when `--relay` is given without `--net` |
| `--players=N` | Tanks in the match when opening the room, 2 to 8 (default 4); bots take the slots nobody joins |
| `--name=NAME` | Your name in the room. If you drop out, starting again with the same room and name puts you back in |
| `--secret=TEXT` | The relay's shared secret, if it has one (up to 256 characters); `EMBERWARD_RELAY_SECRET` does the same |
| `--volume=N` | Master volume, 0 to 100 (default 70); `EMBERWARD_VOLUME` does the same |
| `--mute` | Start with the sound off; `EMBERWARD_MUTE=1` does the same |
| `--hidden` | Never show the window (automated runs) |
| `--terrain-test` | Static terrain patterns for renderer work |
| `--perf` | Capture frame times and exit |
| `--energy-audit` | Print the heat energy balance with the simulation profile (it sums the whole map, so it is off by default) |
| `--dump-sounds=DIR` | Write every sound effect to a WAV file in DIR, print its length and levels, and exit |

## Network matches

Players connect out to a relay server; nobody needs an open port. Everyone starts the game with the same room
name:

```
dotnet run --project src/Emberward.Desktop -- --net=friday --name=Ann
```

Without arguments, press Esc in the game and then N (or click "Network match") for the network menu: your name, the
room, and Join or Create (F2: number of tanks, map size, seed). The game then starts again in that match. The menu
remembers your choices in `settings.ini` under the user's application data folder (`EMBERWARD_SETTINGS_FILE` names another file);
if the relay refuses you (room full or started, wrong secret, other build, not reachable) you come back to the menu
with the reason on screen. A relay's secret is asked for only when it is needed, and kept in that file in plain text. In a
network match the same pause-menu button reads "Leave network match" (L, also in the lobby).

The public relay is open, so no secret is needed. A relay that asks for one: set `EMBERWARD_RELAY_SECRET` (or pass
`--secret=`) to the value whoever runs the relay gives you. The lobby shows who is in. The match starts when the room is full, or when the host (the first player in)
presses Enter; bots play the tanks nobody took. It is a race: the first base to tap the core wins, and a tank out
of lives is out. The first player's `--seed`, `--size`, `--fast` and `--players` decide the match for everyone.
All players must run the same build, and every machine must be fast enough to simulate the map in real time.
P pauses and resumes the match for everyone; Escape only opens your own menu. When the match is over, Enter
plays again in the same room: the first player to press it opens the room, the others join. If you lose the
connection mid-match, Enter reconnects: the game restarts, rejoins under your name and replays the match up to
the present.

A player whose connection dies is dropped by the relay after about 20 seconds and can come back under the same name. A
player who loses the relay is told so on screen. A started match with nobody in it waits five minutes for players
to return, and its clock does not run meanwhile.

To run your own relay instead of the shared one:

```
dotnet run --project src/Emberward.Relay -- --urls http://127.0.0.1:8130
dotnet run --project src/Emberward.Desktop -- --relay=ws://127.0.0.1:8130/play --room=friday
```

The design and what is still missing are in `docs/multiplayer-design.md`.

## Automated runs

These environment variables drive the game without a person, for replays and screenshots. Any of them (and
`--perf`, `--hidden`, `--terrain-test`) makes the run *automated*: the map seed is fixed at 42 unless `--seed` is
given, Escape quits at once, the game never pauses on its own, and it makes no sound. If a command script pauses the game, script
frames keep counting while it is paused, so later commands and screenshots still run.

| Variable | Meaning |
|----------|---------|
| `EMBERWARD_SCRIPTED_INPUT` | Player input per simulation frame (record one with F12) |
| `EMBERWARD_COMMAND_SCRIPT` | `frame:Command` pairs, e.g. `30:TogglePause,60:RequestScreenshot,90:TogglePause`; names are the `GameCommand` enum. Script frames also count in a network lobby, so `150:StartNetMatch` starts a match as host |
| `EMBERWARD_SCRIPT_SCREENSHOT_FRAMES` | Simulation frames at which to save a final-frame screenshot |
| `EMBERWARD_RECORD_INPUT_PATH`, `EMBERWARD_RECORD_INPUT_AUTOSTART` | Where to save a recording, and whether to start recording at once |
| `EMBERWARD_DISPLAY`, `EMBERWARD_WINDOWED`, `EMBERWARD_HIDDEN` | Same as the command-line options |
| `EMBERWARD_SCREENSHOT_DIR` | Where screenshots go (default `screenshots/` next to the executable) |
| `EMBERWARD_GPU_DEBUG` | `1` turns on SDL_GPU's validation layer |
| `EMBERWARD_AUDIO_CAPTURE` | Path of a WAV file to write the game's sound to, one simulation step of sound per step, instead of playing it. Works in automated runs (which are otherwise silent), logs every effect as `[Audio] step N ...`, and rewrites the file every two seconds of game time, so a run that is killed still leaves it |
| `EMBERWARD_AUDIO_LOG` | `1` logs every effect as it starts in a normal game too |
| `EMBERWARD_NET_FAULT_TICK` | In a network match, makes this client's world differ from that tick on, to see a desync found and reported |
| `EMBERWARD_INTERPOLATE` | `1` displays frames between simulation steps in an automated run too (a normal game always does); screenshots are then no longer tied to an exact step |

## Layout

```
src/Emberward.Core          simulation: terrain, heat, tanks, projectiles, machines, links, level generation, bot AI
src/Emberward.Desktop       window, input, renderer (SDL_GPU), HUD, sound, game loop
src/Emberward.Protocol      what a network match's clients and relay share: messages and the relay's tick rule
src/Emberward.Relay         the relay server for network matches
tests/Emberward.Tests       xUnit tests for Core, plus source rules for the renderers
tools/Emberward.ShaderCompiler  build step: HLSL to DXIL, SPIR-V and MSL for SDL_GPU
```

See `ARCHITECTURE.md` for how the pieces fit, and `src/*/ARCHITECTURE.md` for detail.
