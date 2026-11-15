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

## Shaders

One HLSL source in `Rendering/Shaders`. `tools/Tunnerer.ShaderCompiler` runs before every build that touches a shader (target
  `CompileSdlGpuShaders` in `Tunnerer.Desktop.csproj`). It uses SDL_shadercross (DXC and SPIRV-Cross, from NuGet) to
  write `<name>.dxil`, `.spv` and `.msl` plus a `.json` with the resource counts `SDL_CreateGPUShader` needs, into
  `Shaders/` next to the executable. It compiles all three formats on every OS, so a shader one API cannot take
  fails the build where it is edited. It also fails when a texture has no sampler at its index.

SDL_GPU wants fragment textures and samplers in space2, fragment uniforms in space3 and vertex uniforms in space1, so every resource is declared through the macros in
`PostPs/Common/Registers.hlsli`. Each texture has its own sampler at the same index (`s0`-`s2`); on the CPU all are
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
- `Render()`
- `GameTextureId` (used by HUD viewport `ImGui.Image`)
- `SupportsUi` (if false, HUD/ImGui path is skipped)

Upload payload (`GamePixelsUpload`) includes:

- camera/view metadata (`RenderView`)
- the composed world image the terrain pass samples (`NativeContinuousUpload`)
- tank glow array
- terrain auxiliary mask texture data + dirty rect, and the material coverage texture data + dirty rect

## Per-Frame Flow (Game.cs)

Simplified frame pipeline:

1. Poll SDL3 input/events (`SdlRenderer.PollEvents`)
2. Advance simulation (`World.Advance`)
3. Build high-res terrain buffer:
   - full render, or
   - camera scroll copy + exposed strips, plus
   - dirty terrain updates
4. Build/update terrain aux texture payload
5. Compose entities over terrain
6. Build tank glow metadata
7. Upload composed frame to backend (`UploadGamePixels`)
8. Clear frame (`ClearFrame`)
9. If backend supports UI:
   - `NewFrame`
   - `GameHud.Draw` (`ImGui.Image` + overlays/panels)
10. `Render` backend output

## Current Profiling Output

`DrawProfile` (logged every 100 frames) includes:

- top-level: `[Draw]`
- screen-level: `[Screen]`
- decomposition: `[Screen+]`
- finer decomposition: `[Screen++]`

`[Screen++]` currently includes:

- `fullTerrain`
- `terrainCopy`
- `entities`
- `tankGlowBuild`
- `qualityAdjust`
- `clear`
- `newFrame`
- `hud`

## Notes on Known Behavior

- Performance spikes observed in recent traces are usually terrain-side (`dirtyTerrain`, `fullTerrain`, `exposedStrips`) rather than post/blit.
- Remaining visual parity work is focused on fine-tuning look consistency (e.g. heat/emissive tint) and stability edge cases.

## Display options (CLI)

| Argument | Effect |
|----------|--------|
| `--hidden` | Creates the window hidden (never shown) and borderless at the largest display's resolution (or at `--windowed=WxH` if given), so automated runs and performance measurement render at full size without showing anything (`TUNNERER_HIDDEN` does the same). |
| `--windowed` / `--windowed=WxH` | Uses a normal bordered, resizable window (default 1280×720) instead of borderless fullscreen, so test runs do not cover other windows (`TUNNERER_WINDOWED` does the same). |
| `--display=N` / `--display=secondary` | Opens the (borderless fullscreen) window on SDL display N instead of the primary one (`TUNNERER_DISPLAY` does the same). The game logs each display's bounds as `[Display] i: WxH at (x,y)`. Use it to keep test runs off the monitor you are working on. |

Without these flags the window is borderless fullscreen on the primary display.

## Sound

Everything is in `Audio/`. Sound only observes the simulation: it reads world state and never writes it.

| File | Role |
|------|------|
| `GameAudio.cs` | Decides what to play. Called before and after every simulation step. |
| `SoundBank.cs` | Synthesises every effect at start-up (filtered noise, sine partials, envelopes) as mono 48 kHz samples. No sound files; the noise is seeded per effect, so the bank is the same on every run. |
| `SoundMixer.cs` | Mixes one-shot voices and continuous loops to stereo, applies master volume, pause and a soft limiter. |
| `SdlAudioOutput.cs` | The SDL audio device. SDL calls back on its own thread for samples and the mixer fills the buffer (512 samples, about 11 ms). |
| `WavFile.cs` | WAV writer for captures and `--dump-sounds`. |

### Detecting events

`GameAudio.AfterStep` compares the world with what it recorded after the previous step. Triggering on steps
rather than displayed frames is what keeps an effect from repeating on the frames drawn between steps.

| Effect | Trigger |
|--------|---------|
| Fire | `Tank.ShotsFired` rose (the one signal Core provides for sound; nothing in the simulation reads it) |
| Impact | A bullet in the projectile snapshot is dead. A projectile that dies in a step stays in the list, marked dead, until the next step removes it, so each is seen once. At most 2 per step. |
| Debris | 3 or more shrapnel died in the step: one rattle at their centre, at most every 6 steps, however many there were |
| Tank hit | `HitFlashTicks` rose |
| Tank death, respawn | `IsDead` changed |
| Build relay / harvester, plant | `IsCarryingMachine` changed (a tank that dies drops its machine silently) |
| Plant refused | `PlantRefusedTicks` rose (player only) |
| Pickup | A cell that held gold before the step (`BeforeStep` records those near each tank) is empty and under the tank, or the tank's mineral store grew. At most every 3 steps; a run of pickups climbs in pitch. |
| Dock | The player's tank entered its own base |
| Warnings | Player only, most urgent first like the HUD banner: overheating, low shield, running hot. The thresholds are shared with the HUD through `PlayerWarnings`. Each repeats at its own interval while it lasts. |
| Engine loop | Per tank, while alive: idle, or louder and higher while its position changes |
| Dig loop | Per tank: its dirt store grew, or cells next to it were emptied while it was driving or torching |
| Torch loop | Per tank: digging while the turret is firing |
| Recharge loop | Player, docked, and the base is repairing, cooling or unloading it |

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
