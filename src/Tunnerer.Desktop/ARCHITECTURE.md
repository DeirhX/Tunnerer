# Tunnerer.Desktop — Rendering and Sound Architecture

This document describes the current rendering model used by the desktop client, including frame flow, post-processing, and profiling hooks. Sound has its own section at the end.

## High-Level Layout

`Tunnerer.Desktop` draws through one backend, `Tunnerer.Desktop.Rendering.SdlGpu.SdlGpuBackend`: SDL_GPU, which uses
Metal on macOS, Direct3D 12 or Vulkan on Windows and Vulkan on Linux. `IGameRenderBackend` and `ITextureLoader` are
the contract `Game` talks to.

Core orchestration lives in `Game.cs`; the SDL3 window, events and mouse state live in `Rendering/SdlRenderer.cs`.
Code the backend shares with the game sits directly in `Rendering/`: the HLSL (`Rendering/Shaders`), the `PostParams` constant
block (`PostParamsBlock.cs`), ImGui context and input (`ImGuiPlatform.cs`) and screenshot files
(`ScreenshotFiles.cs`). `RenderingSourceGuardTests` keeps SDL_GPU calls inside `Rendering/SdlGpu`.

## Startup

`Game` creates the SDL3 window (`SdlRenderer`), then the `SdlGpuBackend` and `SdlGpuTextureLoader` for that window.
SDL picks the GPU API from the shader formats the game supplies.
It then builds the world through `WorldFactory` (match level or terrain test patterns) and creates the
`TerrainAuxUploader` that owns the CPU copies of the terrain aux and material coverage textures.

## Shaders

One HLSL source in `Rendering/Shaders`. `tools/Tunnerer.ShaderCompiler` runs before every build that touches a shader (target
  `CompileSdlGpuShaders` in `Tunnerer.Desktop.csproj`). It uses SDL_shadercross (DXC and SPIRV-Cross, from NuGet) to
  write `<name>.dxil`, `.spv` and `.msl` plus a `.json` with the resource counts `SDL_CreateGPUShader` needs, into
  `Shaders/` next to the executable. It compiles all three formats on every OS, so a shader one API cannot take
  fails the build where it is edited. It also fails when a texture has no sampler at its index.

SDL_GPU wants fragment textures and samplers in space2, fragment uniforms in space3 and vertex uniforms in space1, so every resource is declared through the macros in
`PostPs/Common/Registers.hlsli`. Each texture has its own sampler at the same index (`s0`-`s3`); on the CPU all are
the same linear-clamp sampler.

## SDL_GPU Backend

One command buffer per frame:

1. `UploadGamePixels`: stages the aux and material coverage dirty rects, the native source texture and the entity
   vertices in transfer buffers, copies them in one copy pass, then records the terrain pass (`TerrainPs` into
   `scene`), the post pass (`PostPs` into `post`) and the entity layer (premultiplied alpha onto `post`).
2. `Render`: uploads ImGui's vertices, then composes the frame into the offscreen `final` texture (clear, blit of
   the game texture sized to the scene, ImGui), downloads it if a screenshot is pending, blits it to the swapchain
   and submits. Composing offscreen keeps screenshots working in hidden windows, where the swapchain may hand out
   no texture.

`GameTextureId` and ImGui texture IDs are `SDL_GPUTexture` pointers. `TUNNERER_GPU_DEBUG=1` turns on SDL_GPU's
validation layer.

## Backend Interface

Shared contract (`IGameRenderBackend`):

- `ProcessEvent(in RenderInputEvent ev)`
- `UploadGamePixels(in GamePixelsUpload upload)`
- `ClearFrame(Size viewportSize, Color clearColor)`
- `NewFrame(int windowW, int windowH, float deltaTime)`
- `SetHeatOrb(in HeatOrbState state)` (the HUD heat gauge's orb, drawn by `HeatOrbPs` where ImGui uses
  `HeatOrbEffect.TextureId`)
- `Render()`
- `RequestScreenshot(string? label)`
- `GameTextureId` / `GameTextureSize` (used by HUD viewport `ImGui.Image`)
- `SupportsUi` (if false, HUD/ImGui path is skipped)

Upload payload (`GamePixelsUpload`) includes:

- camera/view metadata (`RenderView`)
- the composed world image the terrain pass samples (`NativeContinuousUpload`)
- tank glow array
- terrain auxiliary mask texture data + dirty rect, and the material coverage texture data + dirty rect
- the fog of war texture data + dirty rect (`FogUpload`; null data means no fog)

## Fog of war

In a match (unless `--no-fog`) `Game` asks Core for the player's `VisionMap` (`World.TrackVision`) and draws from it.
Core decides what is seen; the renderer only shows it.

- **Remembered terrain.** `TerrainView` says where the renderer reads terrain from. With fog its pixels are
  `VisionMap.Known`, the terrain as the player last saw it, so the world pixel buffer, the solid field, the aux
  material channel and the material coverage all show last-seen ground with no special cases. After a step `Game`
  feeds them `VisionMap.KnownChanges` in place of the terrain's change list. Heat and scorch come from the live
  grid, so `TerrainAuxBuilder` writes them only for cells in sight; ground that comes into sight gets them through
  the `sightChanged` rect of `TerrainAuxUploader.Prepare`.
- **Fog texture.** `FogLayer` keeps one RGBA texel per cell (R = visibility level, G = explored) and repacks the
  rectangle in which vision changed. It is the post pass's fourth texture (`fogTex`, t3).
- **Fog pass.** `Passes/FogPass.hlsli` runs last in `PostPs`: cells in sight are left alone, explored cells out of
  sight are drained toward a cool blue-grey and dimmed, cells never seen are near black. A small blur softens the
  cell steps. `PostFogEnabled` turns it off when there is no fog data.
- **Entities.** The entity layer is drawn after the post pass, so the emitters leave out what is out of sight
  (`Rendering/Entities/Sight.cs`): tanks, machines, shots, and links with neither end in sight. The player's own
  tank and machines always show, because each sees the ground it stands on. A tank out of sight casts no heat glow.
- **Radar blips.** `Game` also asks for the player's `Radar` (`World.TrackRadar`). `RadarQuads` draws one small
  quad per contact (`KindRadarBlip`, `RadarBlip` in `Shaders/Entity/Overlays.hlsli`), first in the entity layer so
  everything else is on top: green for rock and walls, amber for metal, dim blue-green for a cave. A blip flares
  for a few ticks when the beam finds it, which is what shows the sweep, then fades over the contact's life. A blip
  whose cell has come into sight is not drawn.

## Per-Frame Flow (`Game.RenderPipeline.cs`)

The loop around it (polling, fixed simulation steps, composing) is described in the root `ARCHITECTURE.md`,
"Frame loop". Each displayed frame, `Game.RenderImGuiFrame`:

1. Sizes the game view (`RenderViewState`; a match leaves room for the HUD panels) and moves the camera to the
   interpolated player.
2. Builds the entity layer quads at the frame's step progress (`EntityLayerBuilder.Build`).
   With fog of war, `FogLayer.Prepare` then repacks the fog texture where the player's vision changed.
3. Asks `TerrainAuxUploader.Prepare` which part of the terrain aux and material coverage textures to repack:
   - on the first frame, everything;
   - after that, the cells the simulation changed (and growth fades that ended) plus the strip the camera
     revealed, in all four channels (`TerrainAuxBuilder.BuildGpuTerrainAuxRect`, padded by the blur margin);
   - on every `DesktopScreenTweaks.HeatAuxUpdateIntervalFrames`-th frame that follows a step, the terrain's heat
     dirty rect, heat channel only, minus what was already repacked.
4. Builds the tank glow array (`TerrainAuxBuilder.BuildGpuTankHeatGlowData`).
5. Uploads everything in one `GamePixelsUpload` (`UploadGamePixels`): the composed world image (only when a step
   changed it), the aux and coverage dirty rects, tank glow and entity vertices. `TerrainAuxUploader.Commit` then
   clears the terrain's heat dirty rect if it was packed.
6. Adjusts the native-continuous sample count to the time steps 3 and 5 took.
7. `ClearFrame`; if the backend supports UI, `NewFrame` and the HUD: `GameHud.Draw` and the session overlay in a
   match, `GameHud.DrawTerrainTest` in the terrain test.
8. `Render`.

## Profiling Output

`DrawProfile` sums section times and logs their averages every 100 frames:

- `[Draw]`: `terrain` (`DrawChangesToSurface` after each step), `objects` (composing), `screen` (steps 3-6 above)
  and `total` (the whole loop iteration).
- `[Screen]`: `hiresTerrain` (step 3), `hiresEntities` (step 4), `upload` (step 5), `ui` (step 7) and
  `imguiRender` (step 8).
- `[Screen+]`: `backendUpload`, `auxBuild` (the first full pack), `auxTerrain` and `auxHeat` (the partial packs),
  `tankGlowBuild`, `qualityAdjust`, `clear`, `newFrame` and `hud`.

`--perf` (`PerfCaptureSession`) measures whole frame times separately and writes them to the console or a CSV file.

## Display options (CLI)

| Argument | Effect |
|----------|--------|
| `--hidden` | Creates the window hidden (never shown) and borderless at the largest display's resolution (or at `--windowed=WxH` if given), so automated runs and performance measurement render at full size without showing anything (`TUNNERER_HIDDEN` does the same). |
| `--windowed` / `--windowed=WxH` | Uses a normal bordered, resizable window (default 1280×720) instead of borderless fullscreen, so test runs do not cover other windows (`TUNNERER_WINDOWED` does the same). |
| `--display=N` / `--display=secondary` | Opens the (borderless fullscreen) window on SDL display N instead of the primary one (`TUNNERER_DISPLAY` does the same). The game logs each display's bounds as `[Display] i: WxH at (x,y)`. Use it to keep test runs off the monitor you are working on. |

Without these flags the window is borderless fullscreen on the primary display.

## Sound

Everything is in `Audio/`. Sound only observes the simulation: it reads the step's events and world state and never
writes either.

| File | Role |
|------|------|
| `GameAudio.cs` | Decides what to play. Called after every simulation step. |
| `StepEventSounds.cs` | Which one-shot effect each simulation event plays, and how loud (tested without a device). |
| `SoundBank.cs` | Synthesises every effect at start-up (filtered noise, sine partials, envelopes) as mono 48 kHz samples. No sound files; the noise is seeded per effect, so the bank is the same on every run. |
| `SoundMixer.cs` | Mixes one-shot voices and continuous loops to stereo, applies master volume, pause and a soft limiter. |
| `SdlAudioOutput.cs` | The SDL audio device. SDL calls back on its own thread for samples and the mixer fills the buffer (512 samples, about 11 ms). |
| `WavFile.cs` | WAV writer for captures and `--dump-sounds`. |

### Detecting events

Sound does not work out what happened by comparing the world between steps. Core records it: `World.StepEvents`
lists what happened during the last step (see `src/Tunnerer.Core/ARCHITECTURE.md`), and `Game` calls
`GameAudio.AfterStep` after every step, so when one display frame runs several steps each step's events are played
in turn before the next step clears them. Triggering on steps rather than displayed frames is what keeps an effect
from repeating on the frames drawn between steps.

| Effect | Trigger (`StepEventKind`) |
|--------|---------|
| Fire | `ShotFired` |
| Impact | `BulletImpact` (terrain, tank or machine). At most 2 per step. |
| Debris | 3 or more `ShrapnelSpent` within earshot in the step: one rattle at their centre, at most every 6 steps, however many there were |
| Tank hit | `TankHit` |
| Tank death, respawn | `TankDied`, `TankRespawned` |
| Build relay / harvester / pylon, plant | `MachineBuilt` (the machine's own build sound), `MachinePlanted` (a tank that dies drops its machine silently) |
| Plant refused | `PlantRefused` (player only) |
| Pickup | `GoldCollected`, `OreCollected`. At most every 3 steps; a run of pickups climbs in pitch. |
| Dock | `Docked` (player only; a respawn is not a docking) |
| Warnings | Player only, most urgent first like the HUD banner: overheating, low shield, running hot. The thresholds are shared with the HUD through `PlayerWarnings`. Each repeats at its own interval while it lasts. These follow the tank's state, not events. |
| Engine loop | Per tank, while alive: idle, or louder and higher on steps with `TankMoved` |
| Dig loop | Per tank: `TankDug` |
| Torch loop | Per tank: `TankDug` with `Torch` set |
| Recharge loop | Player, docked, and `BaseServiced` (the base is repairing, cooling or unloading it) |

Loops are held for a few steps after their last trigger, because a digging tank only clears cells on some steps,
and the mixer glides loop volume, pan and pitch so nothing clicks.

### Position and loudness

The listener is the player's tank. Loudness falls with the square of distance (about half at 80 cells, silent
beyond about 320) and effects pan by horizontal offset. One effect has at most 4 voices at once; a further one
fades out the oldest. The mixer's limiter bends peaks above 0.75 towards full scale instead of clipping.

### Settings

`AudioOptions` and `AudioTweaks` in `Config/DesktopTweaks.cs` hold the volume, mute and capture settings and the
per-effect levels, intervals and distances. Volume and mute come from `--volume=N` / `TUNNERER_VOLUME` and
`--mute` / `TUNNERER_MUTE`; in game M toggles mute and - / = change the volume. Pausing fades the game's
sounds out and holds each in place.

The pause menu (`Gui/SessionOverlay.cs`) has a sound on/off button and a volume slider in steps of 5%. The
overlay is handed a `SoundMenuState`, changes it when the player clicks or drags, and `Game` passes the change
to `GameAudio`, which owns the setting. Every change of volume or mute, from the menu or the keys, plays a short
chime at the new level; it is a *menu sound*, which the mixer adds after the pause fade so it is heard while
the game is paused. When no sound device is open (automated runs) the two rows are drawn dimmed and do nothing.

### Automated runs and checking sound

Automated runs (`--hidden`, scripted, `--perf`) and the terrain test open no sound device. With
`TUNNERER_AUDIO_CAPTURE=file.wav` the mixer is instead driven by the simulation, exactly one step (2000 samples)
per step, into a WAV file, and every effect is logged as `[Audio] step N <effect> ...`; this is deterministic for
a given seed and script. `--dump-sounds=DIR` writes each effect to its own WAV file and prints its length, peak
and RMS level, and for loops the size of the jump at the loop point.
