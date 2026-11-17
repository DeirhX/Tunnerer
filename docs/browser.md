# Browser play and deployment

The browser edition is a playable local match against the existing bot, with the standard terrain, heat,
regrowth, ore, machines, power network, fog, radar, upgrades, respawns and the core-tap win condition.
It uses the real `Tunnerer.Core` assembly compiled to WebAssembly. No simulation rules are translated to JavaScript.

## Build and run

Install the .NET 10 SDK or a later one, its WebAssembly workload, and Python 3. Players need WebGL 2 and WebAssembly;
hardware acceleration should be enabled in the browser. Run from the repository root:

```sh
python3 tools/browser/publish.py --restore-workloads
python3 tools/browser/serve.py
```

`--restore-workloads` installs the workload the SDK in use needs for this project (`wasm-tools` on .NET 10,
`wasm-tools-net10` on later SDKs); it is only needed the first time, and it may ask for administrator rights
where the SDK is installed for all users. Installing that workload by name with `dotnet workload install` does
the same.

The tools run the same on Windows, macOS and Linux; where Python is installed as `python` or `py` (Windows), use
that name. Open `http://localhost:8703`. Release publishes use AOT compilation. For quicker local iteration use
`python3 tools/browser/publish.py --no-aot`; the interpreted simulation runs more slowly. The publisher replaces
its generated `wwwroot` directory to prevent older runtime assets from leaking into the offline cache.

The browser host is built separately from `Tunnerer.slnx` so desktop builds do not require a WebAssembly workload.
The portable browser presentation and shared rendering projects are in the main solution and are covered by
its analyzers and tests. Publishing also runs the existing shader compiler with `--web`, translating the desktop
HLSL through SPIR-V into GLSL ES 3.00 using the SPIRV-Cross library already bundled with SDL_shadercross.
Generated GLSL lives under `wwwroot/shaders` and is regenerated from the HLSL, not edited by hand.

## Controls and installation

Drive with WASD or arrows; dirt is dug by moving into it. Aim with the mouse, and fire with a click or Space.
Firing while moving torches rock. E builds a relay, R a harvester, T a pylon; press a build key again outside a
base to plant the carried machine. Keys 1–6 buy upgrades while docked. Mouse wheel or +/− changes zoom.
The on-screen controls support touch devices; tap the game to aim and fire, or hold FIRE while driving.
Escape opens the pause menu. Switching away from the page pauses the local match.

The header changes to **OFFLINE READY** after the entire app, including the .NET runtime, has been cached.
Chrome and Edge expose the **Install game** button when installation is available. Other browsers may expose
installation in their own menus; on iPhone/iPad use Safari's Share → Add to Home Screen. Browser support for
installation varies; the game can also be played directly in a tab.

The installed app launches in its own window and starts offline once caching has completed. Each launch starts
a fresh match. A match is kept in memory while the page stays open; save/resume across closing the app is not
implemented. Browser storage can be cleared or evicted; reopening online prepares the offline cache again.

## Host it for players

Deploy **the contents of** `artifacts/browser/wwwroot`, or extract `artifacts/browser/tunnerer-browser.zip`, onto
any HTTPS static host. GitHub Pages, Netlify, Cloudflare Pages and a conventional web server can serve it.
There is no game server or .NET runtime to install on the host. All URLs are relative, so hosting beneath a
prefix such as `/Tunnerer/` works as well as at the domain root. Keep the directory structure intact.

Serve `.wasm` as `application/wasm`, JavaScript as `text/javascript`, and `.webmanifest` as
`application/manifest+json`. Serve `index.html`, `sw.js` and `assets-manifest.js` without long-lived HTTP caching.
An example `_headers` file is included for hosts that support it. Deploy each complete publish together.
No COOP/COEP headers or `SharedArrayBuffer` are needed: the normal single-threaded .NET runtime lives inside a
module Web Worker. It uses the same serial heat solver as desktop with one thermal worker.

.NET's threaded WebAssembly runtime would let the heat solver use several cores (`spikes/WasmThreads` measured
it), but it is not a build switch here: that runtime must be loaded by the page itself, not inside a worker, calls
into it from the page become asynchronous, and the host must send cross-origin isolation headers. It is also
still marked experimental.

`publish.py` hashes the complete deployed app and emits `assets-manifest.js`. The service worker caches every
required asset before becoming active, isolates caches by deployment path, and keeps an open game's existing
runtime active until all its tabs/windows close. Failed update downloads leave the previous working version
active. The manifest must be regenerated after changing a published file; rerun the publisher rather than
editing files in `wwwroot`.

The `browser` GitHub Actions workflow publishes the AOT build on Windows, macOS and Linux, so a change that one
OS's toolchain cannot build fails there. On macOS it also runs the actual game in Chromium and uploads a
`tunnerer-browser` artifact containing the deployable zip and screenshots. Build, formatting and the script tests
are blocking; the graphics smoke run is non-blocking because hosted GPU availability varies, following the desktop
smoke test's existing policy. Deployment to a public host is separate; this workflow does not change Pages settings.

## Verification

```sh
dotnet test Tunnerer.slnx
node --test tools/browser/pace.test.mjs
npm install --prefix artifacts/browser-tests playwright@1.65.1
artifacts/browser-tests/node_modules/.bin/playwright install chromium
python3 tools/browser/serve.py
# In another terminal:
node tools/browser/smoke.mjs
```

The browser smoke test checks real tank movement, build/plant, upgrades, firing events, pause, Chromium's
installation requirements, offline reload into another playable match, and the touch layout and controls.
The full procedural shaders need hardware acceleration. Software GPU emulation stalls during shader execution
until the browser drops the graphics context, so the game asks for an accelerated context and says so at once
where there is none; graphics CI follows the repository's macOS GPU smoke-test setup. Normal browser
runs use the display's own pixel density, capped at 2x and 3840×2160.

### Performance

Open the game with `?perf` (for example `http://localhost:8703/?perf`) for a readout in the bottom-left corner:
the display's frames per second and how many of them drew a new picture, the worker's time for a simulation step
and for building a frame, texture bytes uploaded per second, and the size the world is drawn at. The same numbers
are in `window.tunnererPerf`.

- A frame costs what changed. The worker runs the desktop's `TerrainAuxUploader` and `FogLayer`: the first frame
  uploads the four world-sized textures once, and after that a frame uploads only the cells that changed in sight,
  the strip that scrolled into view and, every other step, the heat that changed. The page draws only when a new
  frame has arrived, and a paused game asks for none.
- The simulation step is the largest cost, and most of it is the heat solver on its one thread.
- Where the graphics hardware cannot keep 45 frames a second, the world is drawn at a lower resolution and
  stretched (`pace.js`: 80%, 65%, 50%); the HUD stays sharp. Steps that do not make frames come faster are undone.
Set `BROWSER_URL` to test a deployed path; set `BROWSER_CHANNEL=chrome` to use an installed Google Chrome.
Screenshots are written to `artifacts/browser-tests`. Unit tests check fog privacy, viewport bounds, and the
browser's use of Core build/upgrade rules without changing the simulation during presentation. Browser entity
vertices are compared byte-for-byte with the shared desktop emitters, and architecture guards keep native
window and graphics dependencies out of the shared rendering assembly.

## Client structure and current scope

- `Tunnerer.Rendering`: the terrain fields, material coverage, the uploader that decides what a frame repacks of
  them, the fog layer, entity emitters, interpolation, machine sprite descriptors, shader constant layout and
  lighting settings used by both hosts. The source files retain their
  existing Desktop paths and namespaces; the assembly references only Core, without SDL or ImGui.
- `Tunnerer.Browser.Client`: local session, HUD snapshots, and the frame packet: one buffer with the shader
  constants, the entity vertices and the changed rectangles of the four terrain textures (`BrowserScene`).
- `Tunnerer.Browser`: WebAssembly exports and the static PWA shell.
- `worker.js`: owns the .NET runtime. Each display request optionally advances one fixed simulation tick, then
  builds the shared renderer's interpolated frame and hands its packet to the page without copying it again. HUD
  state is sent only with frames that follow a tick. The UI keeps at most one request in flight and caps catch-up.
- `webgl.js`: uploads the shared buffers and draws the translated terrain, post-processing and entity shaders.
  Its first pass flips the render-target Y axis for WebGL's framebuffer texture convention; the final pass and
  entity quads use the desktop clip-space convention. Textures use the same linear sampling and premultiplied
  alpha blending as desktop. It must take every packet, in order: each carries only what changed since the one
  before. `renderer.js` handles display density and the welcome illustration; `pace.js` picks the resolution the
  world is drawn at; `perf.js` is the `?perf` readout.
- `main.js` / `input.js`: HUD, seed selection, session lifecycle and input. Build costs, placement, coverage, match
  outcome and upgrade descriptions come from Core. Audio reads Core's step events.

World rendering uses the desktop's procedural materials, curved cave edges, lighting, heat/scorch/frost effects,
fog, tank and machine shapes, animated beams and radar. The HTML HUD and procedural sound remain browser-specific.
Network multiplayer and persistent saves are not included. The existing relay and protocol are unchanged.
No Core gameplay constants or recorded simulation fingerprints changed.
