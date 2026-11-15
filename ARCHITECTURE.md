# Architecture

Tunnerer is a C# / .NET 10 game that began as a port of a C++ engine (kept on the `cplusplus-legacy` branch) and has
since grown its own thermal simulation and a GPU renderer. The simulation is platform-independent
(`Tunnerer.Core`); everything that touches a window, a GPU or an input device lives in `Tunnerer.Desktop`.

For build and run instructions, controls and command-line options see `README.md`.

## Solution structure

```
Tunnerer.slnx
src/
  Tunnerer.Core/           Simulation. No SDL, ImGui or Direct3D references (enforced by ArchitectureGuardTests).
    ARCHITECTURE.md        Detailed design
  Tunnerer.Desktop/        SDL3 window, input and sound, renderer (SDL_GPU), ImGui HUD, game loop.
    ARCHITECTURE.md        Rendering and sound design
tests/
  Tunnerer.Tests/          xUnit tests for Core, plus source rules for the renderers
tools/
  Tunnerer.ShaderCompiler/ Build step: compiles the HLSL to DXIL, SPIR-V and MSL for SDL_GPU
```

## Design principles

- **Core has no platform dependencies.** It takes a `ControllerOutput` per tank per tick and exposes state through
  plain data (`TerrainGrid`, render snapshots such as `TankRenderState`). It knows nothing about windows or GPUs.
- **Fixed-step, deterministic simulation.** `World.Advance` runs at `Tweaks.Perf.TargetFps` ticks per second. With
  a seed, level generation, the simulation and the bot are reproducible, which is what replays, the determinism
  tests and scripted screenshot runs rely on.
- **Heat is conserved and audited.** Thermal exchange is energy-exact; `ThermalLedger` records every transfer in or
  out of the system, and the profile output reports the residual, which should stay near zero.
- **Tunables live in one place.** Gameplay and simulation constants are in `Tunnerer.Core/Config/Tweaks.cs`;
  `SimulationSettings` wraps them so tests can inject other values. Window, render and lighting constants that only
  the desktop client reads are in `Tunnerer.Desktop/Config/DesktopTweaks.cs`.

## Frame loop

`Program.cs` parses the command line and constructs `Game`. `Game.Run` hands a set of callbacks to
`GameFrameCoordinator`, which owns the loop:

```
while running:
  1. SdlRenderer.PollEvents            -> Game.HandleEvent (session keys, ImGui, debug hotkeys)
  2. wait until a simulation step or a display frame is due (sleeps instead of spinning)
  3. update camera / view state
  4. capture mouse state
  5. for each due step (at most 4 to catch up; none while paused):
       GameAudio.BeforeStep              (notes the gold lying near each tank)
       GameSimulationStepper.AdvanceOneStep
         scripted commands -> keyboard / scripted input for player 1, BotTankAI for player 2 -> World.Advance
       EntityLayerBuilder.CaptureStep    (tank, projectile, machine and link snapshot for the entity layer)
       GameAudio.AfterStep               (compares the world with the previous step, starts sound effects)
       TerrainGrid.DrawChangesToSurface  (incremental update of the world pixel buffer)
  6. if a step was due: WorldCompositeRenderer.Compose   marker sprites over a copy of the terrain
  7. Game.RenderImGuiFrame             terrain data + entity quads to the GPU, HUD and session overlay via ImGui
```

The simulation always advances in fixed steps. Display frames are not tied to them: the loop draws as often as
the display allows (vsync, capped by `DesktopScreenTweaks.MaxRenderFps`) and passes each frame its *step
progress*, the fraction of a step elapsed since the last one. `EntityLayerBuilder` draws tanks, projectiles, machines and links
at that point between the previous and the latest step, and the camera follows the interpolated player, so
motion is smooth at the display rate while the terrain only changes on steps. The cost is that what is shown
trails the simulation by up to one step.

Automated runs are step-locked instead: one frame per step, showing exactly that step, so screenshots and timings
are reproducible. `TUNNERER_INTERPOLATE=1` turns the in-between frames on for them.

Session behaviour (pause, controls screen, quit, map seed) is in `Game.Session.cs` and `Gui/SessionOverlay.cs`.
A run counts as *automated* when it is scripted, hidden, a perf capture or the terrain test; automated runs keep a
fixed seed and never pause on their own.

## Simulation order (`World.Advance`)

1. **Collision** - rebuild the sector lookup for tanks and machines
2. **Thermal** - `TerrainGrid.CoolDown`: heat diffusion between cells (sparse by active tile, parallel when large)
3. **Regrowth** - dirt recovery by cellular automaton
4. **Projectiles** - movement, impacts, explosions
5. **Tanks** - input, turret, movement, digging, shooting, building, heat exchange with terrain and base
6. **Machines** - relays pump heat to their base
7. **Sprites** - lifetime
8. **Bases** - reactor cooling, repair
9. **LinkMap** - which machines are connected to a powered base (line of sight, range)

## Rendering pipeline

```
TerrainGrid
   | DrawChangesToSurface (change list)
worldPixels[w*h]
   | copy + draw marker sprites (WorldCompositeRenderer)
compositePixels[w*h]                  terrain aux data (heat, solid field, material, scorch), material coverage
   |                                     |
   +------------- IGameRenderBackend.UploadGamePixels -------------+
                         |
              terrain + post-process shaders (SDL_GPU)  ->  game texture
                         |
              entity layer: link, machine, placement preview, tank and projectile quads (EntityLayerBuilder -> EntityPs)
                         |
              ImGui: viewport image, HUD, overlays  ->  window
```

The composite buffer keeps entity drawing from corrupting the terrain surface. It holds only the cell-resolution
entities; links, relays, harvesters, tanks and projectiles are drawn procedurally at screen resolution by the entity layer. Shader passes and backend detail are
in `src/Tunnerer.Desktop/ARCHITECTURE.md`.

## Sound

`Tunnerer.Desktop/Audio` turns the simulation into sound without the simulation knowing. `GameAudio` runs once
around every simulation step, never per displayed frame, so an effect cannot repeat on the frames shown between
steps. It compares what it reads from the world with what it saw after the previous step (a tank's shot count, hit
flash, death, carried machine, position; projectiles that died during the step; cells the step emptied next to a
tank) and starts the matching effect, placed in stereo and attenuated by distance from the player's tank. The one
thing Core provides for it is `Tank.ShotsFired`, a counter nothing in the simulation reads. All effects are
synthesised at start-up (`SoundBank`), mixed by `SoundMixer` and played through an SDL audio device. Automated runs
open no device. Detail is in `src/Tunnerer.Desktop/ARCHITECTURE.md`.

## Tests

`dotnet test Tunnerer.slnx`. The suite covers terrain and pixels, collision, level generation, tank movement and
heat, relays, hot rock, coolant, the thermal ledger and whole-world thermal behaviour, determinism and replays, and
the bot. Desktop code (loop, input, HUD, renderer, sound) has no unit tests; it is checked with scripted hidden runs
that save final-frame screenshots and, for sound, a WAV capture and an event log (see "Automated runs" in
`README.md`).
