# Tunnerer.Desktop — Rendering Architecture

This document describes the current rendering model used by the desktop client, including frame flow, post-processing, and profiling hooks.

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

- scene pixels (`uint[]`)
- camera/view metadata (`RenderView`)
- quality level
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
