# Tunnerer.Desktop — Rendering Architecture

This document describes the current rendering model used by the desktop client, including backend selection, frame flow, post-processing, and profiling hooks.

## High-Level Layout

`Tunnerer.Desktop` has a backend-agnostic render contract and one active implementation:

- `Tunnerer.Desktop.Rendering.Dx11.Backend`

Core orchestration lives in `Game.cs`; platform/windowing lives in `Rendering/SdlRenderer.cs`.

## Backend Selection

Selection path:

1. CLI argument `--backend=dx11` is parsed in `Program.cs`.
2. `Game` creates an SDL native window for DX backends.
3. `RenderBackendFactory` creates:
   - backend instance (`IGameRenderBackend`)
   - texture loader (`ITextureLoader`)

Only `Dx11` is currently exposed and supported.

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

1. Poll SDL input/events (`SdlRenderer.PollEvents`)
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
11. `SdlRenderer.SwapWindow()` (no-op for DX backends; DX11 presents via swap chain)

## DX11 Backend

DX11 backend now has:

- native swap chain + backbuffer RTV
- scene texture + SRV (uploaded from CPU frame buffer)
- post-process texture + RTV + SRV
- native-source texture + SRV for native continuous terrain mode
- terrain aux texture (t1) and material coverage texture (t2) + SRVs with dirty-rect uploads
- fullscreen VS + three PS variants:
  - native continuous terrain PS
  - post-process PS
  - final blit PS
- DX11 ImGui renderer/controller (`ImGuiController`)
- DX11 texture manager for HUD sprites (returns `ID3D11ShaderResourceView*` encoded as `nint`)

`SupportsUi = true` when native DX11 init succeeds; `GameTextureId` is an SRV pointer for ImGui.

DX11 implementation is split by responsibility:

- `Backend` partials (`Backend.*.cs`) for lifecycle, init, native pass, CPU fallback, SDL fallback, profiling, and shader compiler interop
- shader sources are grouped under `Rendering/Dx11/Shaders/`:
  - `FullscreenVs.cs`
  - `BlitPs.cs`
  - `PostPs.cs`
  - `TerrainPs.cs`

### DX11 Render Stages

`UploadGamePixels` (native path):

1. update terrain aux texture (full or dirty rect)
2. upload scene texture (`UpdateSubresource`) or native-source texture (native continuous mode)
3. optional native continuous terrain pass into scene RTV
4. run post-process pass into post RTV

`Render`:

1. fullscreen blit of display SRV to backbuffer RTV
2. ImGui draw on top (HUD + overlays)
3. `swapChain->Present`

### Per-frame DX11 Walkthrough

1. `Game.RenderImGuiFrame` computes the render `viewSize` (window minus HUD strip when UI is enabled), updates camera, and builds `RenderView`.
2. Terrain is rendered into the hi-res pixel buffer (full redraw, or scroll copy + exposed strips + dirty updates).
3. Entities are composited into that same hi-res pixel buffer.
4. Terrain auxiliary payload and tank glow metadata are built.
5. `Game` sends `GamePixelsUpload` to `IGameRenderBackend.UploadGamePixels`.
6. `Backend.UploadGamePixels` uploads scene pixels to `_sceneTexture`, updates `_terrainAux` and `_materialCoverage` (full/dirty rect), then runs the post pass.
7. `RunPostProcessPass` binds `_postRtv`, scene/aux/coverage SRVs, updates `PostParamsCBuffer`, and draws the fullscreen triangle into `_postTexture`.
8. `Game` clears the frame, starts backend UI frame (`NewFrame`), and records HUD draw calls (`GameHud.Draw`).
9. `Backend.Render` blits `_displaySrv` (or `_sceneSrv`) to backbuffer using a viewport matched to scene texture size, then renders ImGui.
10. Swapchain present displays the final composed frame.

### DX11 Safety/Debug Toggles

- `TUNNERER_DX11_CPU_FALLBACK=1`
  - bypasses DX11 post shader and uses CPU fallback effects path for comparison.
- `TUNNERER_DX11_PROFILE=1`
  - enables detailed DX11 stage timing printouts every 240 profiled frames and on dispose.

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

DX11 backend detailed profile (`TUNNERER_DX11_PROFILE=1`) includes:

- `sceneUpload`
- `auxUpload`
- `postTotal`
- `postSetup`
- `postCb`
- `postDraw`
- `blit`
- `ui`

## Notes on Known Behavior

- Performance spikes observed in recent traces are usually terrain-side (`dirtyTerrain`, `fullTerrain`, `exposedStrips`) rather than DX11 post/blit.
- Remaining visual parity work is focused on fine-tuning look consistency (e.g. heat/emissive tint) and stability edge cases.
## Notes

The older appended "Platform Layer" section has been removed because it described a previous SDL texture-presentation path that no longer matches the current DX11-first architecture.

## Display options (CLI)

| Argument | Effect |
|----------|--------|
| `--hidden` | Creates the window hidden (never shown) and borderless at the largest display's resolution (or at `--windowed=WxH` if given), so automated runs and performance measurement render at full size without showing anything (`TUNNERER_HIDDEN` does the same). |
| `--windowed` / `--windowed=WxH` | Uses a normal bordered, resizable window (default 1280×720) instead of borderless fullscreen, so test runs do not cover other windows (`TUNNERER_WINDOWED` does the same). |
| `--display=N` / `--display=secondary` | Opens the (borderless fullscreen) window on SDL display N instead of the primary one (`TUNNERER_DISPLAY` does the same). The game logs each display's bounds as `[Display] i: WxH at (x,y)`. Use it to keep test runs off the monitor you are working on. |

Without these flags the window is borderless fullscreen on the primary display.
