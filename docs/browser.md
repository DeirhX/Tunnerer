# Browser play and deployment

The browser edition is a playable local match against the existing bot, with the standard terrain, heat,
regrowth, ore, machines, power network, fog, radar, upgrades, respawns and the core-tap win condition.
It uses the real `Tunnerer.Core` assembly compiled to WebAssembly. No simulation rules are translated to JavaScript.

## Build and run

Install the .NET 10 SDK, its `wasm-tools` workload, and Python 3. Players need WebGL 2 and WebAssembly;
hardware acceleration should be enabled in the browser. Run from the repository root:

```sh
dotnet workload install wasm-tools
python3 tools/browser/publish.py
python3 tools/browser/serve.py
```

Open `http://localhost:8703`. Release publishes use AOT compilation. For quicker local iteration use
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

`publish.py` hashes the complete deployed app and emits `assets-manifest.js`. The service worker caches every
required asset before becoming active, isolates caches by deployment path, and keeps an open game's existing
runtime active until all its tabs/windows close. Failed update downloads leave the previous working version
active. The manifest must be regenerated after changing a published file; rerun the publisher rather than
editing files in `wwwroot`.

The `browser` GitHub Actions workflow publishes the AOT build, runs the actual game in Chromium on macOS, and
uploads a `tunnerer-browser` artifact containing the deployable zip and screenshots. Build and formatting are
blocking; the graphics smoke run is non-blocking because hosted GPU availability varies, following the desktop
smoke test's existing policy. Deployment to a public host is separate; this workflow does not change Pages settings.

## Verification

```sh
dotnet test Tunnerer.slnx
npm install --prefix artifacts/browser-tests playwright@1.65.1
artifacts/browser-tests/node_modules/.bin/playwright install chromium
python3 tools/browser/serve.py
# In another terminal:
node tools/browser/smoke.mjs
```

The browser smoke test checks real tank movement, build/plant, upgrades, firing events, pause, Chromium's
installation requirements, offline reload into another playable match, and the touch layout and controls.
The full procedural shaders need hardware acceleration for practical play. Software GPU emulation can stall
during shader execution, so graphics CI follows the repository's macOS GPU smoke-test setup. Normal browser
runs use the display's own pixel density, capped at 2x and 3840×2160.
Set `BROWSER_URL` to test a deployed path; set `BROWSER_CHANNEL=chrome` to use an installed Google Chrome.
Screenshots are written to `artifacts/browser-tests`. Unit tests check fog privacy, viewport bounds, and the
browser's use of Core build/upgrade rules without changing the simulation during presentation. Browser entity
vertices are compared byte-for-byte with the shared desktop emitters, and architecture guards keep native
window and graphics dependencies out of the shared rendering assembly.

## Client structure and current scope

- `Tunnerer.Rendering`: the terrain fields, material coverage, entity emitters, interpolation, machine sprite
  descriptors, shader constant layout and lighting settings used by both hosts. The source files retain their
  existing Desktop paths and namespaces; the assembly references only Core, without SDL or ImGui.
- `Tunnerer.Browser.Client`: local session, HUD snapshots, viewport texture uploads and the shared renderer's
  entity and uniform buffers.
- `Tunnerer.Browser`: WebAssembly exports and the static PWA shell.
- `worker.js`: owns the .NET runtime. Each display request optionally advances one fixed simulation tick, then
  builds the shared renderer's interpolated frame. The UI keeps at most one request in flight and caps catch-up.
- `webgl.js`: uploads the shared buffers and draws the translated terrain, post-processing and entity shaders.
  Its first pass flips the render-target Y axis for WebGL's framebuffer texture convention; the final pass and
  entity quads use the desktop clip-space convention. Textures use the same linear sampling and premultiplied
  alpha blending as desktop. Terrain uploads are bounded to the viewport with a margin; stationary frames reuse
  their textures. `renderer.js` handles display density and the welcome illustration.
- `main.js` / `input.js`: HUD, seed selection, session lifecycle and input. Build costs, placement, coverage, match
  outcome and upgrade descriptions come from Core. Audio reads Core's step events.

World rendering uses the desktop's procedural materials, curved cave edges, lighting, heat/scorch/frost effects,
fog, tank and machine shapes, animated beams and radar. The HTML HUD and procedural sound remain browser-specific.
Network multiplayer and persistent saves are not included. The existing relay and protocol are unchanged.
No Core gameplay constants or recorded simulation fingerprints changed.
