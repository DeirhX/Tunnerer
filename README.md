# Tunnerer

A 2D tunnelling tank game. You dig through the ground, fight an enemy tank, and manage heat: digging and firing
heat your tank and the rock around it, your base is the heat sink, and relays pipe heat back to the base.

Written in C# on .NET 10. The simulation (`Tunnerer.Core`) has no platform dependencies; the desktop client
(`Tunnerer.Desktop`) uses SDL2 for the window and input, Direct3D 11 for rendering and Dear ImGui for the HUD.

## Requirements

- Windows 10 or later with a Direct3D 11 GPU
- .NET 10 SDK
- `fxc.exe` from the Windows SDK, to compile the HLSL shaders at build time. The project looks for Windows SDK
  10.0.26100.0 and otherwise for `fxc.exe` on `PATH`; pass `-p:Dx11ShaderCompiler=<path>` to point at another copy.

## Build, run, test

```
dotnet build Tunnerer.slnx
dotnet run --project src/Tunnerer.Desktop
dotnet test Tunnerer.slnx
```

The tests cover `Tunnerer.Core` only and need neither a GPU nor the shader compiler when run per project:
`dotnet test tests/Tunnerer.Tests`.

## Controls

| Key | Action |
|-----|--------|
| W A S D | Drive and dig |
| Mouse | Aim the turret |
| Left click / Space | Fire |
| E | Build a relay (150 dirt); press again to plant it |
| F9 | Thermal view |
| Esc / P | Pause menu |
| F1 | Controls screen |
| F10 | Screenshot (`screenshots/` next to the executable) |
| F11 | Renderer debug overlay; 1-7 then toggle shader passes |
| F12 | Start / stop recording input for a scripted replay |

The game also pauses when its window loses focus.

## Command line

Run `Tunnerer.Desktop --help` for the full list. The common ones:

| Option | Effect |
|--------|--------|
| `--seed=N` | Play the map with seed N. Without it every match gets a new map; the pause menu shows the seed. |
| `--windowed[=WxH]` | Resizable window instead of borderless fullscreen |
| `--display=N` | Open on display N (or `secondary`) |
| `--size=WxH`, `--large` | Terrain size |
| `--no-ai` | No enemy tank |
| `--hidden` | Never show the window (automated runs) |
| `--terrain-test` | Static terrain patterns for renderer work |
| `--perf` | Capture frame times and exit |

## Automated runs

These environment variables drive the game without a person, for replays and screenshots. Any of them (and
`--perf`, `--hidden`, `--terrain-test`) makes the run *automated*: the map seed is fixed at 42 unless `--seed` is
given, Escape quits at once, and the game never pauses on its own.

| Variable | Meaning |
|----------|---------|
| `TUNNERER_SCRIPTED_INPUT` | Player input per simulation frame (record one with F12) |
| `TUNNERER_COMMAND_SCRIPT` | `frame:Command` pairs, e.g. `30:TogglePause,60:RequestScreenshot`; names are the `GameCommand` enum |
| `TUNNERER_SCRIPT_SCREENSHOT_FRAMES` | Simulation frames at which to save a final-frame screenshot |
| `TUNNERER_RECORD_INPUT_PATH`, `TUNNERER_RECORD_INPUT_AUTOSTART` | Where to save a recording, and whether to start recording at once |
| `TUNNERER_DISPLAY`, `TUNNERER_WINDOWED`, `TUNNERER_HIDDEN` | Same as the command-line options |

## Layout

```
src/Tunnerer.Core          simulation: terrain, heat, tanks, projectiles, machines, links, level generation, bot AI
src/Tunnerer.Desktop       window, input, DX11 renderer, HUD, game loop
src/Tunnerer.Presentation  legacy software-rendered GUI, kept only for its layout tests
tests/Tunnerer.Tests       xUnit tests for Core
```

See `ARCHITECTURE.md` for how the pieces fit, and `src/*/ARCHITECTURE.md` for detail.
