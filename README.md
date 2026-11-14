# Tunnerer

A 2D tunnelling tank game. You dig through the ground, fight an enemy tank, and manage heat: digging and firing
heat your tank and the rock around it, your base is the heat sink, and relays pipe heat back to the base.

Written in C# on .NET 10. The simulation (`Tunnerer.Core`) has no platform dependencies; the desktop client
(`Tunnerer.Desktop`) uses SDL3 for the window and input, SDL_GPU for rendering and Dear ImGui for the
HUD.

## Requirements

- .NET 10 SDK
- Windows 10 or later, or macOS (tested on Apple silicon). Linux should work but is untested.
Everything comes from NuGet, including the shader compiler (SDL_shadercross).

### Renderer

Rendering goes through SDL_GPU: Metal on macOS, Direct3D 12 or Vulkan on Windows, Vulkan on Linux. The HLSL in
`src/Tunnerer.Desktop/Rendering/Shaders` is compiled to DXIL, SPIR-V and MSL at build time.

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
given, Escape quits at once, and the game never pauses on its own. If a command script pauses the game, script
frames keep counting while it is paused, so later commands and screenshots still run.

| Variable | Meaning |
|----------|---------|
| `TUNNERER_SCRIPTED_INPUT` | Player input per simulation frame (record one with F12) |
| `TUNNERER_COMMAND_SCRIPT` | `frame:Command` pairs, e.g. `30:TogglePause,60:RequestScreenshot,90:TogglePause`; names are the `GameCommand` enum |
| `TUNNERER_SCRIPT_SCREENSHOT_FRAMES` | Simulation frames at which to save a final-frame screenshot |
| `TUNNERER_RECORD_INPUT_PATH`, `TUNNERER_RECORD_INPUT_AUTOSTART` | Where to save a recording, and whether to start recording at once |
| `TUNNERER_DISPLAY`, `TUNNERER_WINDOWED`, `TUNNERER_HIDDEN` | Same as the command-line options |
| `TUNNERER_SCREENSHOT_DIR` | Where screenshots go (default `screenshots/` next to the executable) |
| `TUNNERER_GPU_DEBUG` | `1` turns on SDL_GPU's validation layer |
| `TUNNERER_INTERPOLATE` | `1` displays frames between simulation steps in an automated run too (a normal game always does); screenshots are then no longer tied to an exact step |

## Layout

```
src/Tunnerer.Core          simulation: terrain, heat, tanks, projectiles, machines, links, level generation, bot AI
src/Tunnerer.Desktop       window, input, renderer (SDL_GPU), HUD, game loop
tests/Tunnerer.Tests       xUnit tests for Core, plus source rules for the renderers
tools/Tunnerer.ShaderCompiler  build step: HLSL to DXIL, SPIR-V and MSL for SDL_GPU
```

See `ARCHITECTURE.md` for how the pieces fit, and `src/*/ARCHITECTURE.md` for detail.
