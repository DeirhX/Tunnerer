# Browser play and deployment

The browser edition is a playable local match against the existing bot, with the standard terrain, heat,
regrowth, ore, machines, power network, fog, radar, upgrades, respawns and the core-tap win condition, and network
matches on the relay with other browsers and desktop players (Network play, below).
It uses the real `Emberward.Core` assembly compiled to WebAssembly. No simulation rules are translated to JavaScript.

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
`python3 tools/browser/publish.py --no-aot`; the interpreted simulation runs about five times more slowly, so do not
judge the framerate on it (Performance, below). The publisher replaces
its generated `wwwroot` directory to prevent older runtime assets from leaking into the offline cache.

### Two runtimes

A simulation step is the client's largest cost, and most of it is the heat solver. The default publish runs it on
one thread, on any host and in any current browser. `python3 tools/browser/publish.py --threads` publishes a
deployment that carries a second .NET runtime, the threaded one, beside the first, so that the heat solver can use
several cores:

```sh
python3 tools/browser/publish.py --threads --restore-workloads
python3 tools/browser/serve.py --isolate
```

(The threaded runtime is the SDK's `wasm-experimental` workload; `--restore-workloads` installs it too.)

**A fault that ended the threaded runtime, and its fix.** Measured 2026-11-20 in Edge 157 on Windows
(`tools/browser/soak.mjs`, a script that plays at random and reports a stall or a runtime that exited): in about one
match in four, within the first minutes, .NET logged `ReleaseJSOwnedObjectByGCHandle: Unexpected synchronous failure
... ArgumentNullException ... key` on one of its own threads and exited with `RuntimeError: unreachable`. The player
saw "Assert failed: .NET runtime already exited with 1", or a frozen game. The fault is in .NET's JavaScript interop
with threads: it frees the proxy of each marshalled value when the browser collects it. The page's one source of
those proxies was the byte views handed over every frame (`[JSMarshalAs<JSType.MemoryView>]`: the frame's packet,
the relay's messages, the sound bank). They now cross as an address in .NET's memory, pinned for the call, and a
length; `frames.js` copies them out of the heap (`copyOut`). The same soak on the same machine, eight heat workers:
the old code ended in 3 of 12 matches of 45 s; the new code in none of 10 of 45 s, one of 570 s and four of 300 s
(one of those runs also paused 8 s once, at 293 s, and nothing more is known of it).
Do not marshal a view, an object or a delegate to JavaScript on the frame path again. The soak is the check:
`node tools/browser/soak.mjs` against a `--threads` publish served with `--isolate` and `?threads=8`. This was probably
also what stopped WebKit's threaded runtime below, which has not been tried again.

The page chooses when a match starts (`wwwroot/threads.js`). It uses the threaded runtime only where all of this
holds, and the single-thread runtime in its worker everywhere else:

- The page is cross-origin isolated (`crossOriginIsolated`), which browsers require before they share memory
  between threads. The host makes it so by sending `Cross-Origin-Opener-Policy: same-origin` and
  `Cross-Origin-Embedder-Policy: require-corp` with every file; `serve.py --isolate` sends them, and a `--threads`
  publish adds them to `_headers`. A host that cannot send headers (GitHub Pages) serves the same deployment on one
  thread.
- The device reports more than two cores. The heat solver gets one worker per core but one, eight at most.
- The browser is Chromium (Chrome, Edge and their relatives). In WebKit (Playwright's WebKit 27.2, measured
  2026-11-17 with .NET 10.0.15; Chromium 159 beside it never stopped) the threaded runtime sometimes stops
  answering the page: a call from the page never comes back, after anything from four to a few hundred steps, in
  about a third of the matches tried. A whole match played inside one call (`BrowserApi.Fingerprint`) never
  stopped, and a call that does nothing stopped once, so the fault is on the way between the page and .NET's own
  thread, not in the solver. Firefox has not been tried. `?threads=4` in the address uses the threaded runtime
  with four heat workers in any isolated browser, and `?threads=0` keeps Chromium to one thread, for comparing the
  two on one device.

If the threaded runtime fails to load, the match starts on one thread instead.

One deployment carries both runtimes, rather than the runtime being fixed when publishing, because a deployment
with only the threaded one would not start where the page is not isolated, the device has few cores, or the
browser is not Chromium. A browser still downloads and keeps only the one it uses: the offline manifest lists the
two apart (1.9 MiB shared, 1.4 MiB of it the HUD art; single-thread 10.2 MiB, threaded 10.8 MiB), and the service worker asks `threads.js` the
page's own question and caches that one ahead of time. The other is cached if the page ever loads it. The zip
holds both, so it is about twice the size of a default publish's.

The world does not depend on the choice: every step gives the same bits on one thread as on eight, in either
runtime, and in the desktop build (`BrowserSessionThreadsTests`; the smoke test compares the two runtimes in the
browser).

The two builds need no clearing between them: the threaded one keeps its intermediate files apart, under
`src/Emberward.Browser/out/threads`. A `--no-aot` build does not: an AOT publish made over the intermediate files of
an interpreted one gave a threaded runtime that would not start (the smoke test showed it), so delete
`src/Emberward.Browser/out` when going from one to the other.

The browser host is built separately from `Emberward.slnx` so desktop builds do not require a WebAssembly workload.
The portable browser presentation and shared rendering projects are in the main solution and are covered by
its analyzers and tests. Publishing also runs the existing shader compiler with `--web`, translating the desktop
HLSL through SPIR-V into GLSL ES 3.00 using the SPIRV-Cross library already bundled with SDL_shadercross.
Generated GLSL lives under `wwwroot/shaders` and is regenerated from the HLSL, not edited by hand.
Before translating, the web export turns every implicit-LOD sample in its copy of the SPIR-V into an explicit
sample of level 0 (`ExplicitLodRewrite`), so the GLSL calls `textureLod(..., 0.0)` and never `texture()`: ANGLE's
Direct3D 11 backend must otherwise unroll PostPs's loops around implicit-derivative samples, and that compile did
not finish on Windows. It samples the same only because every web texture has level 0 alone and filters without
mipmaps (`webgl.js`).

Windows browsers (Edge, Chrome, Comet, Firefox) draw WebGL through ANGLE's Direct3D 11 backend, which translates
the GLSL to HLSL and compiles it with FXC; macOS browsers go through ANGLE's Metal backend and Apple's compiler,
which had no trouble. Two things made FXC hang there, measured on GitHub's Windows runner (Edge 156):

- **Returns before the end of a function.** DXC turns such a function into a loop that runs once, left by a break
  at each return, and FXC is very slow on that form around a large body: EntityPs took 35 to 40 s, and PostPs ran
  past four minutes and then crashed FXC, which is the empty "Shader linking failed" the page showed. Each entity
  shape alone compiled in under 0.1 s. With one return at the end of `main` and of the passes and materials (an
  `if (needed) { ... }` where there was an `if (!needed) return;`), EntityPs links in 1.3 s and PostPs in 8.8 s.
  The GPU skips the same work either way: the frames are identical, at the same speed (Metal, measured).
  `RenderingSourceGuardTests` keeps new shader functions to one return.
- **NaN-aware min, max and clamp.** DXC emits GLSL.std.450 NMin, NMax and NClamp, which SPIRV-Cross spells as
  `isnan(a) ? b : (isnan(b) ? a : max(a, b))`; ANGLE then makes every `?:` an `if`, and the `isnan` turns on
  FXC's strict IEEE mode. The web export makes them plain FMin, FMax and FClamp (`NanFreeMinMaxRewrite`), which
  differ only for NaN operands. It also halved the time a frame takes to draw on Metal (15.1 to 7.1 ms at
  3200x1844, M-series Mac): the pictures differ by at most one level in 255 in 0.1% of pixels.

The page compiles and links the shaders with `KHR_parallel_shader_compile` where the browser has it, waiting a
frame at a time. Loading reports its stages (`progress.js`): the shader download, each program's compile and link,
the runtime's download and start, and the world's generation. The loading line shows the stage under way, and says
so when one runs past ten seconds; the console logs each stage's time as `[load] ...`; each begin and end is a
`emberward-load` event on `window`, and the list so far is `window.emberwardLoad`, to copy into a bug report. A failed
start names the stage it stopped in ("while compiling the lighting and materials shaders, 12 s in").

A bar fills through the load. Each phase (shaders, download, runtime start, world) is as wide as it took on this
device last time (`localStorage`, `emberward.load-times`), or a guess on a first visit. The download fills by the bytes
that have arrived: `publish.py` writes each runtime's size into `threads.js`, and a resource loader in `frames.js`
counts what .NET fetches. A compile gives no progress, only done or not, so the other phases fill by their time
against last time's and slow down short of the end. .NET has the browser check every runtime file against its
published hash (subresource integrity), and the browser then holds back all the bytes of a file, even from a copy of
the response, until the last has arrived: the bar sat still through the 8 MB `dotnet.native.wasm`. The loader
therefore fetches without that check, counts a copy as it arrives and checks the SHA-256 itself before .NET gets the
response, at the moment the browser would have given it, so a file that does not match still stops the load. .NET
gets the response fetch made, not one built from the copy, so that the browser can keep the runtime's compiled code.

## Desktop builds on the welcome screen

The Desktop section under the welcome screen lists a download per platform (Windows, macOS, Linux). Its rows live
in `index.html`: a row with an `<a class="download-link">` is a published build, and a row without one says
"Not published yet". `downloads.js` works out the visitor's platform from the client hints or the user agent
(`tools/browser/downloads.test.mjs` covers the strings). On a computer it shows only that platform's row, and a
"Other platforms" button shows the rest; a phone, or an unknown platform, sees the whole list. A row with a link
points at the latest GitHub release in ordinary browser publishes. On Starfish the publish workflow downloads the
latest release, checks its SHA-256 sums and exposes its three single-file executables under `/downloads/`. Caddy
serves precompressed gzip copies when the client accepts gzip, and sends the original executable otherwise. The
files are excluded from the browser's offline cache. Pushing a `v*` tag runs `desktop-release.yml`, which publishes
three archives and `SHA256SUMS.txt` on GitHub. The workflow adds GitHub provenance attestations for the archives,
checksum file, and extracted executables served by the browser site. Verify any one of those files with the GitHub CLI,
for example `gh attestation verify ./emberward-windows-x64.zip -R DeirhX/Emberward` for an archive or
`gh attestation verify ./emberward-windows-x64.exe -R DeirhX/Emberward` for direct downloads. Run `publish-starfish`
after the release to update Starfish's copies. The release and browser build must come from the same commit for cross-play.
Downloads on macOS and Linux need their executable bit set (for example `chmod +x emberward-macos-arm64`). These
attestations prove build provenance; native Windows and macOS code signatures are still absent, so SmartScreen and
Gatekeeper may show warnings.

## Controls and installation

Drive with WASD or arrows; dirt is dug by moving into it. Aim with the mouse, and fire with a click or Space.
Firing while moving torches rock. E builds a relay, R a harvester, Y a local
generator, U a turret and T a gatling. Press a build key again outside a
base to plant the carried machine. Keys 1–7 buy upgrades while docked. H turns the heat view on and off, as on
the desktop: the ground tinted by its heat, so that enemy tunnels glow (it changes only what is drawn). Mouse wheel
or +/− changes zoom. Every match runs the energy network (DESIGN.md §4.9): every machine in sight near the tank
carries the desktop's charge label (charge over capacity, flow in and
out a second, fed or starved colour), the materials plate adds the base store's production, draw and net and the
gate shield, and G hides
or shows the labels. The off-network clock is the tank's reserve: the stage's tag sits beside OFF NETWORK, and
from the warning on X (or the button over the HUD) burns a unit of pyrite from the hold.
The on-screen controls support touch devices; tap the game to aim and fire, or hold FIRE while driving.
Escape opens the pause menu. Switching away from the page pauses the local match.

## Network play

The welcome screen has two tabs, **Solo** and **Online**, and keeps the one last chosen. **Online** joins a room on
the relay (docs/multiplayer-design.md): a name, a room and the number of tanks. The page remembers them
(`localStorage`). The page sends no secret: the public relay is open to everyone, and a relay that asks for a secret
refuses the browser. The first player into
a room opens it with a new seed and that many tanks, on the desktop's map for the count (480x300 for more than two,
`?map=WxH` for another); bots take the slots nobody joins. The lobby shows who is in each slot in their tank's
colour; the host (the lowest slot) starts with **Start match** or Enter, and a full room starts by itself. P pauses
the match for everyone and resumes it. Escape opens the menu over a running match, which does not stop for one
player, **Leave match** leaves the room, and the host's **Close to new players** shuts the running match to newcomers (and **Open to new players** reopens it); the button shows the state, disabled, to everyone else. A player who left or lost the connection joins again under the same
name and the page replays the match to the present, without sound, as the desktop does.

The relay is the site's own `wss://<host>/play` on crust.bokovka.cz, which proxies it, and
`wss://crust.bokovka.cz/play` everywhere else; `?relay=ws://localhost:8130/play` picks another. WebSockets are not
subject to the cross-origin isolation headers, so a `--threads` deployment plays online too.

A browser plays in the same rooms as the desktop. The relay keeps a room to one build, and the browser's build id
is the desktop's: the Core assembly's version, which carries the commit. A browser and a desktop built from the
same commit compute the same world (the native and WebAssembly fingerprints agree), so the published page joins
desktop players running the same commit. The browser runs the desktop's own `NetSession` and slot stepper (shared
through `Emberward.Rendering`) and the protocol's `RoomClient`, the relay state the desktop's `RelayConnection` wraps.
The page owns the WebSocket (`net.js`) and carries only bytes: each request to .NET takes what arrived, with
arrival times on the page's clock, and its answer brings the messages to send (`BrowserNetMatch`, `frames.js`).
A frame simulates the ticks that have arrived, as on the desktop; a build or upgrade press waits in .NET for a tick
to carry it, followed by a tick that releases it. A step is drawn from when the request that took it was sent,
because ticks arrive by the relay's clock. A hidden tab stops drawing and stepping; when it comes back it catches up
as a rejoining player does.

Not yet in the browser: name tags over other tanks, the desync report file the desktop writes, and choosing a map
size or seed for a new room from the page.

The header says **SAVED FOR OFFLINE** after the entire app, including the .NET runtime, has been cached, and
**OFFLINE · SOLO ONLY** without a connection; otherwise it says nothing.
Chrome and Edge expose the **Install game** button when installation is available. Other browsers may expose
installation in their own menus; on iPhone/iPad use Safari's Share → Add to Home Screen. Browser support for
installation varies; the game can also be played directly in a tab.

The installed app launches in its own window and starts offline once caching has completed. Each launch starts
a fresh match. A match is kept in memory while the page stays open; save/resume across closing the app is not
implemented. Browser storage can be cleared or evicted; reopening online prepares the offline cache again.

## Host it for players

Deploy **the contents of** `artifacts/browser/wwwroot`, or extract `artifacts/browser/emberward-browser.zip`, onto
any HTTPS static host. GitHub Pages, Netlify, Cloudflare Pages and a conventional web server can serve it.
There is no game server or .NET runtime to install on the host. All URLs are relative, so hosting beneath a
prefix such as `/Emberward/` works as well as at the domain root. Keep the directory structure intact.

Serve `.wasm` as `application/wasm`, JavaScript as `text/javascript`, and `.webmanifest` as
`application/manifest+json`. Serve `index.html`, `sw.js`, `assets-manifest.js` and `threads.js` without long-lived
HTTP caching. An example `_headers` file is included for hosts that support it. Deploy each complete publish
together.
The default publish needs no COOP/COEP headers or `SharedArrayBuffer`: the normal single-threaded .NET runtime
lives inside a module Web Worker. It uses the same serial heat solver as desktop with one thermal worker.

A `--threads` publish wants the two cross-origin isolation headers ("Two runtimes" above) and plays on one thread
without them. An isolated page cannot load anything from another origin that does not allow it, and other sites
cannot open it in a frame unless they are isolated too; the game loads nothing from elsewhere. .NET still marks
its threaded WebAssembly runtime experimental.

`publish.py` hashes the complete deployed app and emits `assets-manifest.js`. The service worker caches every
required asset (of two runtimes, the one this browser uses) before becoming active, isolates caches by deployment
path, and does not reload a running game. A new version installs in the background. A page that holds no game
(on the start page with nothing to read, not typing in a field) then moves to it and reloads itself, if no other
Emberflow tab is open. Otherwise the page shows an Update button in the header, which reloads when pressed
(`install.js`). Pressing it moves every tab to the new version; another tab with a match running keeps playing
and shows the button too. The start page's footer names the version the page runs and, when it differs, the latest
one on the server ("VERSION 5cc86e02 · LATEST e7ef5bb1"), or says it is up to date. The page also
looks for a new version when it comes back into view, at most every ten minutes. Failed update downloads
leave the previous working version active. The manifest must be regenerated after changing a published file; rerun
the publisher rather than editing files in `wwwroot`.

The `browser` GitHub Actions workflow publishes the AOT build on Windows, macOS and Linux, once as the default
and once with `--threads`, so a change that one OS's toolchain cannot build fails there. On macOS it also runs the
actual game in Chromium, on one thread and in the threaded runtime, and uploads a `emberward-browser` artifact
containing the deployable zips and screenshots. Build, formatting and the script tests
are blocking; the graphics smoke run is non-blocking because hosted GPU availability varies, following the desktop
smoke test's existing policy. Deployment to a public host is separate; this workflow does not change Pages settings.

### Starfish (crust.bokovka.cz)

The public copy is served from the Starfish VM at `https://crust.bokovka.cz`, beside the relay
(`docs/multiplayer-design.md`, "The relay", describes the VM, SSH access and updating the relay). Caddy's site file,
`/etc/caddy/conf.d/crust.bokovka.cz.caddy`, sends `/play` and `/health` to the relay and serves everything else
from `/srv/tunnerer/browser`. It sends the cross-origin isolation headers and the no-cache headers listed above,
so deploy a `--threads` publish. `/srv/tunnerer/browser.prev` keeps the build that was live before.

The easy way is the **publish-starfish** workflow (`.github/workflows/publish-starfish.yml`): on GitHub, Actions →
publish-starfish → Run workflow, with the branch to publish (normally `master`). It runs only when started by hand,
and its job is skipped unless the repository owner started it. It publishes with `--threads`, downloads and verifies the latest desktop release, adds executables and gzip copies to the bundle, uploads it,
checks that the site serves the version it built and with the isolation headers, and writes that version in the
run's summary.

By hand, publish from an up-to-date `master` and pipe the zip to the deploy script on the VM:

```sh
python3 tools/browser/publish.py --threads
gh release download --dir artifacts/desktop-release
python3 tools/browser/prepare_starfish.py artifacts/browser artifacts/desktop-release
ssh Starfish-azureuser sudo /usr/local/bin/tunnerer-deploy-browser < artifacts/browser/emberward-browser.zip
```

The deploy script is `tools/browser/starfish-deploy.sh`, installed on the VM as
`/usr/local/bin/tunnerer-deploy-browser`. It unpacks the zip, stops if it is not a complete `--threads` publish,
moves the live build to `browser.prev`, puts the new one in place and prints its version. After changing it,
install it again:

```sh
scp tools/browser/starfish-deploy.sh Starfish-azureuser:/tmp/tunnerer-deploy-browser
ssh Starfish-azureuser 'sudo install -m 755 /tmp/tunnerer-deploy-browser /usr/local/bin/ && rm /tmp/tunnerer-deploy-browser'
```

The workflow signs in with its own key, kept in the `starfish` environment of the repository's settings
(Settings → Environments): the secrets `STARFISH_SSH_KEY` (the private key) and `STARFISH_KNOWN_HOSTS` (the VM's
line from `ssh-keyscan 52.158.32.240`), and the variable `STARFISH_SSH_TARGET` (`azureuser@52.158.32.240`). On the
VM, that key's line in `~azureuser/.ssh/authorized_keys` lets it run the deploy script and nothing else, so a
leaked key can replace the game but cannot open a shell:

```
command="sudo /usr/local/bin/tunnerer-deploy-browser",restrict ssh-ed25519 AAAA… emberward-publish-starfish
```

To withdraw the workflow's access, delete that line.

The swap needs no Caddy reload. To check it, compare the version the publisher printed with the live one, and
look at the headers:

```sh
curl -sS https://crust.bokovka.cz/assets-manifest.js | grep -o '"version": "[^"]*"'
curl -sSI https://crust.bokovka.cz/ | grep -i cross-origin
```

Then open the page: its footer should say the version the publisher printed and "UP TO DATE". Start a game. A page on the start page moves to the new version by itself once it has
downloaded it; one with a match running shows the Update button. To roll back, swap the two directories
back. The relay is deployed on its own and needs updating only when the protocol changes.

## Verification

```sh
dotnet test Emberward.slnx
node --test tools/browser/pace.test.mjs
npm install --prefix artifacts/browser-tests playwright@1.65.1
artifacts/browser-tests/node_modules/.bin/playwright install chromium
python3 tools/browser/serve.py
# In another terminal:
node tools/browser/smoke.mjs
```

`node tools/browser/soak.mjs` plays a published game at random for `SOAK_SECONDS` (default 180) and fails on a
stall or a runtime that exited; `BROWSER_URL=http://localhost:8703/?threads=4` (a `--threads` publish served with
`--isolate`) soaks the threaded runtime, and `BROWSER_CHANNEL=msedge` uses an installed Edge.

`tools/browser/net-smoke.mjs` plays two pages in one room of a local relay
(`dotnet run --project src/Emberward.Relay -- --urls http://127.0.0.1:8130`): the lobby, the host's start, a bot in
the open slot, driving and building through the relay, pause for everyone, and a player who leaves and rejoins.
The relay compares the players' world hashes once a second, so a match that plays on computed one world in both.
`BrowserNetMatchTests` in `dotnet test` plays a browser client against a desktop client on the real relay and
compares their worlds.

For a `--threads` publish, serve it with `--isolate` and run `SMOKE_THREADS=1 node tools/browser/smoke.mjs`: the
match must then run in the threaded runtime, and both runtimes must compute the same world. `BROWSER=webkit` (after
`playwright install webkit`) runs the test in WebKit, where the match stays on one thread either way.

The browser smoke test checks real tank movement, build/plant, upgrades, firing events, pause, Chromium's
installation requirements, offline reload into another playable match, and the touch layout and controls.
The full procedural shaders need hardware acceleration. Software GPU emulation stalls during shader execution
until the browser drops the graphics context, so the game asks for an accelerated context and says so at once
where there is none; graphics CI follows the repository's macOS GPU smoke-test setup. Normal browser
runs use the display's own pixel density, capped at 2x and 3840×2160.

### Performance

Open the game with `?perf` (for example `http://localhost:8703/?perf`) for a readout in the bottom-left corner:
the display's frames per second and how many of them drew a new picture, the time a simulation step and building
a frame take inside .NET, how many threads step the heat, texture bytes uploaded per second, the size the
world is drawn at, and what updating the HUD after a step costs the page (the art strip apart). The same numbers are
in `window.emberwardPerf`.

Judge the framerate on an AOT publish (the default): a `--no-aot` build steps the simulation about five times more
slowly, falls behind, and then draws a new picture only with each step. Measured 2026-11-18 in Chromium on an Apple
M5 Max, seed 1234, the window 1600×1000 at device pixel ratio 2, display at 120 Hz:

| Build | Heat threads | Step | New pictures a second | HUD a step (strip) |
|---|---|---|---|---|
| AOT, threaded runtime | 8 | 1.9 ms | 118 of 118 | 0.16 ms (0.12 ms) |
| AOT, single-thread runtime | 1 | 9.4 ms | 97 of 120 | 0.15 ms (0.11 ms) |
| `--no-aot` | 1 | 46 ms | 20 of 120 | 0.17 ms (0.16 ms) |

On one thread a step takes longer than a 120 Hz frame (8.3 ms), so the frame during each step shows the picture
before it: about one in five at 24 steps a second. A 60 Hz display (16.7 ms a frame) loses none. The art strip
draws what never changes once for each size into layers kept off screen and blurs nothing (`strip.js`); drawing it
all every step, with the titles' glow as a blur, cost 0.16 ms of the page's time where it now costs 0.12 ms.

- A frame costs what changed. The worker runs the desktop's `TerrainAuxUploader` and `FogLayer`: the first frame
  uploads the four world-sized textures once, and after that a frame uploads only the cells that changed in sight,
  the strip that scrolled into view and, every other step, the heat that changed. The page draws only when a new
  frame has arrived, and a paused game asks for none.
- The simulation step is the largest cost, and most of it is the heat solver. Measured 2026-11-17 on an Apple M5
  Max in Chromium, seed 1234 with the player idle, at the game's 24 steps a second: 9.4 ms a step on one thread
  and 4.1 to 4.7 ms in the threaded runtime with eight heat workers. Stepping without a pause between steps takes
  6.2 ms and 1.1 ms: about 3 ms of every paced step is the cores waking up, which threads do not shorten.
- Where the graphics hardware cannot keep 60 frames a second (or the display's own pace where that is slower), the
  world is drawn at a lower resolution and stretched (`pace.js`: 80%, 65%, 50%); the HUD stays sharp. It judges a
  window of frames by their average with the slowest 15% left out, so hitches do not count but a GPU that misses
  every third refresh does. Steps that do not make frames come faster are undone, and it never steps back up.
- `?map=3000x1500` plays on a larger map (up to 4096 each way) and `?machines=400` grows networks of that many
  machines from every base before play (`BrowserStress`: tunnels dug through dirt, relays where the placement
  preview shows a live link, a harvester and a second relay beside every third), for measuring a busy late game. Setting
  that up steps the world for a while, so the match takes longer to start.
  Measured 2026-11-18 in headless Chromium on an Apple M5 Max, AOT build, seed 1234, display at 60 Hz, device pixel
  ratio 1:

  | Match | Heat threads | Step | Frame | Steps a second | Start |
  |---|---|---|---|---|---|
  | 1500x750, no machines | 8 | 4.2 ms | 0.2 ms | 24 | 2.4 s |
  | 3000x1500, 400 machines | 8 | 6.6 ms | 1.5 ms | 24 | 9.7 s |
  | 3000x1500, 400 machines | 1 | 22.3 ms | 1.3 ms | 24 | 16.2 s |
  | 4000x2000, no machines | 8 | 7.3 ms | 0.3 ms | 24 | 15.5 s |
  | 4000x2000, no machines | 1 | 28.2 ms | 0.6 ms | 24 | 20.1 s |
  | 4000x2000, 800 machines | 8 | 10.7 ms | 3.1 ms | 24 | 19.1 s |
  | 4000x2000, 800 machines | 1 | 36.4 ms | 3.3 ms | 20 | 37.5 s |

  The 800 machines were 558 relays, 125 harvesters and 117 pylons (measured before the pylon was removed; a
  second relay now stands where the pylon did). On one thread the largest falls behind: a step
  and a frame take nearly all of the 41.7 ms a step has, and the worker does both. Most of a step's cost comes with
  the map's size: the same map without machines takes 7.3 ms with eight threads and 28.2 ms on one. The machines
  add the rest (natively, mostly their own actions, then vision and links), and about 3 ms a frame to draw. With
  eight threads a step's time varies by about a millisecond from one page load to the next. Starting is mostly
  generating the map (15.5 s of the 19.1 s with eight threads); on one thread each of the setup's steps costs
  more, and the setup takes about 17 s.
Set `BROWSER_URL` to test a deployed path; set `BROWSER_CHANNEL=chrome` to use an installed Google Chrome.
Screenshots are written to `artifacts/browser-tests`. Unit tests check fog privacy, viewport bounds, and the
browser's use of Core build/upgrade rules without changing the simulation during presentation. Browser entity
vertices are compared byte-for-byte with the shared desktop emitters, and architecture guards keep native
window and graphics dependencies out of the shared rendering assembly.

## Client structure and current scope

- `Emberward.Rendering`: the terrain fields, material coverage, the uploader that decides what a frame repacks of
  them, the fog layer, entity emitters, entity motion (extrapolation), machine sprite descriptors, shader constant layout and
  lighting settings used by both hosts, and what the HUD tells the player (`PlayerGuidance`: the tutorial, the
  game-event toasts, the build-key hint and the warning banner), and a client's part in a network match
  (`NetSession`, `SlotStepper`, `NetMatch`). The source files retain their existing Desktop paths and namespaces;
  the assembly references only Core and the protocol, without SDL or ImGui.
- `Emberward.Browser.Client`: the session (local, or this player's world in a network match), the network match
  (`BrowserNetMatch`), HUD snapshots, and the frame packet: one buffer with the shader constants, the entity
  vertices and the changed rectangles of the four terrain textures (`BrowserScene`).
- `Emberward.Browser`: WebAssembly exports and the static PWA shell.
- `simulation.js`: what the page talks to. It is `worker.js` with the single-thread runtime, or the threaded
  runtime behind the same messages: .NET can only start that one from the page itself, where it then runs on
  threads of its own and answers the page's calls later. `threads.js` decides which.
- `frames.js`: the one way into .NET for both. Each display request optionally advances one fixed simulation
  tick, then builds the shared renderer's frame; .NET hands the packet over as a view of its memory
  and it is copied once, into an ordinary buffer (WebGL takes no view of memory that threads share), which the
  worker passes to the page without copying it again. HUD state is sent only with frames that follow a tick.
  The UI keeps at most one request in flight and caps catch-up. After each tick the page announces the HUD state
  as a `emberward-step` event on `window`, which is how the smoke test follows a match in either runtime.
- `webgl.js`: uploads the shared buffers and draws the translated terrain, post-processing and entity shaders.
  An entity vertex is 14 floats (56 bytes): its corner in world cells, the local position, the colour and kind, four
  parameters, and its motion in cells per simulation step (`ENTITY_ATTRIBUTE_FLOATS`, `ENTITY_VERTEX_BYTES`;
  `EntityLayoutGuardTests` compares both with the C# struct). `EntityVs` draws it at `corner + motion * StepAlpha`
  and the camera moves the same way, so the page can draw the last packet again as it is `alpha` of the way through
  its step on a frame that brought no new packet: `setAlpha` patches the one `StepAlpha` float of the uniform buffer
  (`STEP_ALPHA`, checked against `PostParamsBlock` by `PostParamsLayoutGuardTests`). `main.js` computes the alpha
  (`stepAlpha`). Quads that are rebuilt on every frame (beams, the tether and the radar) stand still on such a frame.
- `progress.js`: the stages of a load, for the loading line, the loading bar, the console and the `emberward-load`
  event.
  Its first pass flips the render-target Y axis for WebGL's framebuffer texture convention; the final pass and
  entity quads use the desktop clip-space convention. Textures use the same linear sampling and premultiplied
  alpha blending as desktop. It must take every packet, in order: each carries only what changed since the one
  before. `renderer.js` handles display density and the welcome illustration; `pace.js` picks the resolution the
  world is drawn at; `perf.js` is the `?perf` readout.
- `main.js` / `input.js`: HUD, seed selection, session lifecycle, input and the heat view (H, which the frame
  request carries to `BrowserSession.ThermalView`). Build costs, placement, coverage, match
  outcome and upgrade descriptions come from Core. `guidance.js` shows the desktop's tutorial card (F2 hides it),
  toasts, hint line and warning banner as each frame after a step carries them (`guidance`), and the renderer points
  at the tutorial's target. `hud.js` shows the status panels, the upgrade list (base track above tank track, with
  level pips), the heat orb and bar in the desktop's heat colours (`HeatScale`) and the hit flash with a wedge
  toward the shooter. `strip.js` draws the desktop's art strip (below). With the energy network on, each frame after
  a step carries the base charge row (`basePower`) for the status panel. The render packet carries the machine
  charge labels as entity quads from `PowerLabelQuads`. The browser supplies the displayed cell size for the
  shared zoom fade, and G redraws the view with labels hidden or shown.
- The HUD looks like the desktop's (`HudTheme`): chamfered plates with a bronze frame and amber corner brackets,
  amber titles, segmented bars and the desktop's palette and font (Chakra Petch, which `publish.py` copies from
  `src/Emberward.Desktop/resources/fonts` as it generates the shaders).
- On a screen at least 1000×620 with a mouse, the page takes the desktop's layout (`HudLayout`): `strip.js` draws
  the desktop's art strip in the bottom corners on two canvases, TEMP, SHIELD and MATERIALS on the left and BUILD
  STRUCTURES and COMMAND on the right, at the desktop's art coordinates and scale. It ports `HudStrip`,
  `HeatGauge`, `LiquidGauges` and `BuildPanel`: the heat orb with its dial, needle and trend arrow, the shield
  crystal, the cargo hold filling with pieces of Pyrite, the OFF NETWORK tag, the build slots and the COMMAND button (a click
  on a slot builds, on COMMAND opens the upgrades). What the slots say comes from the shared `BuildSlotView`, which
  the desktop's `BuildPanel` draws too, and the orb's frozen and boiling states from `HeatScale`. The desktop
  draws the orb's liquid with a shader (`HeatOrbPs`); the canvas draws ice, bubbles and a burning glow instead.
  The plates keep what the strip does not show, at the top right where the desktop's BASE panel stands, and the
  upgrades sit at the left above the strip. `publish.py` copies the HUD panels and ore pile sprite sheet from
  `src/Emberward.Desktop/resources/hud` into `wwwroot/hud`. Phones and narrow windows keep the plates,
  which fit them; the art does not. The strip is drawn after each step, not each display frame, and not while
  paused; the heat dial's colours come with a match's first frame only.
- The HUD's cost follows what changes. It updates after every step, 24 times a second, and writes to the page
  only text, classes and styles whose values changed; toasts are added and removed, not rebuilt; the hit flash
  changes only opacity and a transform. The overlay canvas (bearings, the tutorial's pointer) is cleared only
  when something was drawn on it. Measured 2026-11-18 in headless Chromium on an Apple M5 Max, seed 1234, the same
  15 seconds of digging and idling: 33 changes to the page a second where rebuilding the HUD made 1,150, and
  1.0 ms of layout a second where it took 5.5 ms. The match is built by Core's `MatchSetup`, as on the desktop.
  What sounds, for whom, how loud, where and at what pitch is decided by the desktop's `SoundDirector` after each
  step; the frame after the step lists the one-shots it started and where each loop (engines, digging, torch,
  recharge) stands (`sounds`). The samples are the desktop's `SoundBank`, synthesised in .NET the first time the
  player turns sound on and sent with the next frame (`BrowserSoundBank`). `audio.js` plays them with Web Audio: one-shots through a gain and a stereo panner, at most four of an
  effect at once, and loops that glide to each step's values. Opening the menu suspends sound, and the loops fall
  silent when the match ends. As on the desktop, what is heard follows distance, not sight.

World rendering uses the desktop's procedural materials, curved cave edges, lighting, heat/scorch/frost effects,
fog, tank and machine shapes, animated beams, radar and the heat view. The HUD says what the desktop's says and
the sounds are the desktop's; wide screens show the desktop's art strip and layout, and there is no volume control
yet (sound is on or off).
Persistent saves are not included. Network play needed no change to the relay or the protocol's bytes.
No Core gameplay constants or recorded simulation fingerprints changed.

## Next steps

What is intended next for the browser client, roughly in order:

- A volume slider in place of the sound on/off button, as the desktop's audio options have.
- The rest of the desktop's wide layout: its BASE panel (goal, lives, network, hint) in place of the two plates at
  the top right, and the heat orb's liquid drawn by its shader (`HeatOrbPs`, through WebGL) instead of canvas
  strokes.
- Play and listen to the desktop build once. Its sound decisions and HUD guidance moved into shared code
  (`SoundDirector`, `PlayerGuidance`) for the browser; the tests cover them, but the desktop has not been played
  since.
- Find why the offline download grew by about 0.7 MiB a runtime with the guidance, sound and HUD work.
- Larger maps. A 4000x2000 map takes about 15 seconds to generate, and a step's cost grows with the map's size
  (Performance, above): on one thread the largest measured falls behind with machines. Map generation and the heat
  solver's cost per map need work first, measured on slower devices than the one above. With threads, each paced
  step also spends about 3 ms waking the cores.
- Try the threaded runtime in Firefox. WebKit keeps to one thread, because its threaded runtime sometimes stops
  answering (Two runtimes, above).
- Saving and resuming a match across closing the app.
- Name tags over other tanks in a network match, and a desync report the player can download.
