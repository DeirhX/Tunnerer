# Architecture

Emberflow is a C# / .NET 10 game that began as a port of a C++ engine (kept on the `cplusplus-legacy` branch) and has
since grown its own thermal simulation and a GPU renderer. The simulation is platform-independent
(`Emberward.Core`); the desktop host lives in `Emberward.Desktop`. The browser host runs Core as WebAssembly in a
Web Worker (or, where the host and browser allow, on several threads), with a WebGL 2 backend and an installable
offline shell (see `docs/browser.md`). Both hosts use the same procedural shaders and platform-independent
rendering code in `Emberward.Rendering`.

For build and run instructions, controls and command-line options see `README.md`.

## Solution structure

```
Emberward.slnx
src/
  Emberward.Core/           Simulation. No SDL, ImGui or Direct3D references (enforced by ArchitectureGuardTests).
    ARCHITECTURE.md        Detailed design
  Emberward.Browser.Client/ Portable browser session (local or network), presentation snapshots; references Core,
                           Rendering and Protocol.
  Emberward.Browser/        WebAssembly exports, WebGL backend, browser input and offline PWA shell.
  Emberward.Rendering/      Shared assembly; compiles the pure rendering sources from Desktop, and the network
                           session (NetSession, Seats, SlotStepper) the browser plays network matches with.
  Emberward.Desktop/        SDL3 window, input and sound, renderer (SDL_GPU), ImGui HUD, game loop.
    ARCHITECTURE.md        Rendering and sound design
  Emberward.Protocol/       What a network match's clients and relay share: tick bundles and the relay's tick rule.
                           References nothing (enforced by ArchitectureGuardTests). See docs/multiplayer-design.md.
  Emberward.Relay/          The relay server (ASP.NET Core, one WebSocket endpoint). References only the protocol.
tests/
  Emberward.Tests/          xUnit tests for Core and Desktop logic, plus guard tests for both
tools/
  Emberward.ShaderCompiler/ Build step: compiles the HLSL to DXIL, SPIR-V and MSL for SDL_GPU
```

## Design principles

- **Core has no platform dependencies.** It takes a `ControllerOutput` per tank per tick and exposes state through
  plain data (`TerrainGrid`, render snapshots such as `TankRenderState`). It knows nothing about windows or GPUs.
- **Fixed-step, deterministic simulation.** `World.Advance` runs at `Tweaks.Perf.TargetFps` ticks per second. With
  a seed, level generation, the simulation and the bot are reproducible, which is what replays, the determinism
  tests and scripted screenshot runs rely on.
- **Heat is conserved and audited.** Thermal exchange is energy-exact; `ThermalLedger` records every transfer in or
  out of the system, and the profile output reports the residual, which should stay near zero.
- **Tunables live in one place.** Gameplay and simulation constants are in `Emberward.Core/Config/Tweaks.cs`;
  `SimulationSettings` wraps them so tests can inject other values. Window, render and lighting constants that only
  the desktop client reads are in `Emberward.Desktop/Config/DesktopTweaks.cs`.

## Frame loop

`Program.cs` parses the command line and constructs `Game`. The constructor opens the window and GPU backend,
asks `WorldFactory.Build` for the world (a generated match level, or the static patterns of `--terrain-test`) and
sets up input. `--fast` only makes level generation and materialisation parallel; the simulation is
deterministic either way. `Game.Run` hands a set of callbacks to `GameFrameCoordinator`, which owns the loop:

```
while running:
  1. SdlRenderer.PollEvents            -> Game.HandleEvent (session keys, ImGui, debug hotkeys)
  2. wait until a simulation step or a display frame is due (sleeps instead of spinning)
  3. update camera / view state         (match: keep the player centred; terrain test: WASD pans)
  4. capture mouse state
  5. for each due step (at most 4 to catch up):
       match:
         GameSimulationStepper.AdvanceOneStep
           scripted commands -> keyboard / scripted input for the local slot, a BotTankAI per other slot -> World.Advance
         EntityLayerBuilder.CaptureStep    (tank, projectile, machine and link snapshot for the entity layer)
         GameAudio.AfterStep               (SoundDirector plays the step's World.StepEvents, sets the loops)
         GameHud.AfterStep                 (GameEventWatcher counts the ore harvesters banked this step)
         TerrainAuxUploader.ObserveTerrainChanges (changed cells, to repack on the next frame)
         Game.DrawTerrainChangesToSurface  (incremental update of the world pixel buffer)
           with fog of war both follow the cells the player's VisionMap says changed in the terrain
           the player knows, not the terrain's own change list
       terrain test, or paused in an automated run:
         scripted commands and screenshots only; the world does not step
  6. if a step was due: mark the world pixels changed, for the next upload
  7. Game.RenderImGuiFrame
       EntityLayerBuilder.Build          (entity quads at the frame's step progress; with fog only those in sight)
       FogLayer.Prepare                  (repack the fog texture where the player's vision changed)
       TerrainAuxUploader.Prepare        (repack changed, revealed and heated terrain; dirty rects to upload)
       TerrainAuxBuilder.BuildGpuTankHeatGlowData
       IGameRenderBackend.UploadGamePixels, then TerrainAuxUploader.Commit
       ClearFrame, NewFrame, GameHud (match HUD and session overlay, or the terrain test panel), Render
```

The simulation always advances in fixed steps. Display frames are not tied to them: the loop draws as often as
the display allows (vsync, capped by `DesktopScreenTweaks.MaxRenderFps`) and passes each frame its *step
progress*, the fraction of a step elapsed since the last one. `EntityLayerBuilder` draws tanks, projectiles, machines and links
at that point between the previous and the latest step (`Rendering/Entities/EntityInterpolator`), and the camera follows the interpolated player, so
motion is smooth at the display rate while the terrain only changes on steps. The cost is that what is shown
trails the simulation by up to one step. Beams (links, charge, tethers) fade in and out over a fraction of a
second in display time (`Rendering/Entities/BeamFader`), so they do not pop with the simulation.

The game renders at the display's own pixel density (Retina included): the game view and camera are in device
pixels, the UI in points. The camera keeps the player in the middle of the screen, running past the map's edges
when it has to, and the bottom HUD is split into the two bottom corners so the middle of the view stays clear.
`src/Emberward.Desktop/ARCHITECTURE.md`, "Display density and the HUD", has the detail.

Automated runs are step-locked instead: one frame per step, showing exactly that step, so screenshots and timings
are reproducible. `EMBERWARD_INTERPOLATE=1` turns the in-between frames on for them.

A network match (`--net`, `Game.Net.cs`) is deterministic lockstep: every client runs the whole simulation and
normal play exchanges inputs and sync hashes. `Net/NetSession` sends the local player's input to the relay (`Emberward.Relay`), steps the world
only from the tick bundles the relay sends back, never from the local clock, and reports a world hash once a second
so a client that drifts apart is caught. Bots drive the slots nobody took, seeded the same on every client. Who
drives each tank is `Net/Seats`, and only the human mask every bundle carries changes it, so a seat changes hands at
the same tick everywhere. During a late join, one player uploads a snapshot and a second verifies it when available.
`Net/LateJoin` loads the snapshot, checks it and takes a bot seat through the WebSocket relay. The player-facing
join screens are still pending (`docs/late-join-design.md`). The pause menu opens over a running game.
`docs/multiplayer-design.md` describes the protocol and the relay.

Session behaviour (pause, controls screen, quit, map seed) is in `Game.Session.cs` and `Gui/SessionOverlay.cs`.
The pause menu's network entry (`Game.NetMenu.cs`, `Gui/NetMenuView.cs`) only collects choices: `Net/NetMenuModel` is the menu
without ImGui (screens, fields, validation, the launch Enter produces), `Net/PlayerSettings` is `settings.ini`, and
`Net/LaunchArguments` parses the command line and builds the one for the next run. The game then closes and `Program.cs`
starts it again in the match (the secret goes in `EMBERWARD_RELAY_SECRET`, never in the arguments); a launch the relay
refuses comes back as a local match with the menu open (`docs/multiplayer-lobby-ux.md`).
A run counts as *automated* when it is scripted, hidden or a perf capture. Automated runs and the terrain test are
not *interactive* (`Game.IsInteractiveSession`): they keep a fixed seed, make no sound, never pause on their own,
and Escape quits at once.

## Simulation order (`World.Advance`)

1. **Collision** - rebuild the sector lookup for tanks and machines
2. **Thermal** - `TerrainGrid.CoolDown`: heat diffusion between cells (only the tiles still changing, in parallel when many)
3. **Regrowth** - dirt grows back into open and scorched ground (only the cells that can change are looked at)
4. **Projectiles** - movement, impacts, explosions
5. **Tanks** - input, turret, movement, digging, shooting, building, heat exchange with terrain and base
6. **Machines** - relays pump heat to their base, harvesters mine and roam on the base's charge, generators and turrets work on the energy network
7. **Sprites** - lifetime
8. **Bases** - reactor heat sink and radiator
9. **LinkMap** - which machines are connected to a powered base (line of sight, range); brought up to date every 200 ms, only where points or the ground between them changed
10. **Vision** - fog of war for each side someone asked to track (`World.TrackVision`): what its tank, base and
    machines see, and the terrain as it last saw it; and the tank's radar (`World.TrackRadar`), whose turning beam
    leaves fading contacts where it reflects beyond sight. Output only; the steps above never read them

Detail, and recipes for adding machines, projectiles, materials and collidable entities, are in
`src/Emberward.Core/ARCHITECTURE.md`.

## Rendering pipeline

```
TerrainGrid
   | DrawChangesToSurface (change list)
worldPixels[w*h]                      terrain aux data (heat, solid field, material, scorch), material coverage,
   |                                  fog of war (how well each cell is seen, explored)
   |                                     |
   +------------- IGameRenderBackend.UploadGamePixels -------------+
                         |
              terrain + post-process shaders (SDL_GPU)  ->  game texture
              (the post pass ends with the fog: remembered ground drained and dimmed, unknown ground black)
                         |
              entity layer: link, machine, placement preview, tank and projectile quads (EntityLayerBuilder -> EntityPs)
                         |
              whole-screen effects, only while one shows: hit, overheat, freeze (ScreenEffects -> ScreenFxPs)
                         |
              ImGui: viewport image, HUD, overlays  ->  window
```

With fog of war (on in a match unless `--no-fog`) the terrain drawn is the terrain the player last saw: the world
pixels, aux and coverage data are built from `VisionMap.Known` through a `TerrainView`, and heat is only repacked
for cells in sight. The fog pass then only tones the frame, and the entity layer leaves out what is out of sight
and draws the radar's blips on the ground the player cannot see.

Nothing is drawn into the world pixels but terrain: links, machines, tanks and projectiles are drawn
procedurally at screen resolution by the entity layer. Shader passes and backend detail are in
`src/Emberward.Desktop/ARCHITECTURE.md`.

## Guidance: tutorial and notifications

`Emberward.Desktop/Gui/Guidance` tells the player what to do next without touching the simulation. It only reads Core
state. It is shared with the browser through `Emberward.Rendering`, with `Gui/HintSelector.cs` and
`Presentation/MachinePresentation.cs`, so neither file may use ImGui or SDL. `PlayerGuidance` is what both hosts
hold: they call `AfterStep` after every step and `Update` with their HUD clock, then show its `Tutorial`,
`Notifications`, `Hint` and `Warning`. The desktop updates it once per displayed frame on the HUD's time; the browser
updates it after each step on the match's time (`BrowserSession.HudSeconds`) and sends it with the frame.

- `TutorialDirector` is an ordered list of next logical steps (leave the base, mine ore, unload, build and link a relay,
  stay on the network, power from hot rock, harvester, relay beside it, power for it, frost). Each step knows from the world
  when it is done, so a player who acts early skips ahead; explainer steps also finish after a few seconds on screen.
  A step can name a world cell to point at.
- `GameEventWatcher` compares the player's side of the world with the previous frame and posts toasts to a
  `NotificationFeed` (relay linked or lost, harvester started, stalled or out of charge, ore
  store full, charge low, off network, radar scattered by heat, engine running, harvester ore deliveries). The
  deliveries are counted from `StepEventKind.OreHarvested` after every step (`AfterStep`), not from the store, so a
  tank unloading in the same frame is not mistaken for them. The feed de-duplicates by key with a cooldown.
- `HintSelector` picks the one context hint for the build keys (what E will do now, or why a carried machine will not
  plant), and `PlayerWarnings` the one warning banner, most urgent first (overheating, frostbite, chilled, low shield,
  running hot, the respawn shield, and after the first hit a tip about the thermal view where the host has one).
- `GameHud.Guidance` draws the card (top left, F2 hides it), the toasts under it and the pointer (a ring on screen,
  an edge arrow with the distance off screen). `GameHud.Goal` draws the sector goal lines, the upgrade list while
  docked (base track on keys 1-4, tank track on 5-6) and the sector-complete banner. The build panel asks
  `Tank.QuoteBuild` for each machine's ore cost and payer. The MATERIALS panel shows Emberite piling up inside
  the hold, the exact count and capacity, and the base store or UNLOADING (`Tank.IsUnloadingOre`).

Every overlay (tutorial card, toasts, status and upgrade panels, warning banners, pause and controls menus, tags) is
drawn with `HudTheme`, which follows the bottom HUD art: chamfered gunmetal plates, a bronze frame with an amber inner
line, amber corner brackets, amber titles, segmented bars and key caps. `HudTheme.BeginWindow` turns an ImGui window
into such a plate, so new panels match by default. While docked, toasts stop above the upgrade panel rather than
cover it.

## Sound

`Emberward.Desktop/Audio` turns the simulation into sound without the simulation knowing. `SoundDirector` runs once
after every simulation step, never per displayed frame, so an effect cannot repeat on the frames shown between
steps. It does not compare the world between steps: Core lists what happened during the step in `World.StepEvents`
(a shot, an impact, a death, a planting, a docking...) and `SoundDirector` starts the matching effect, placed in
stereo and attenuated by distance from the player's tank, into an `ISoundSink`. All effects are synthesised
(`SoundBank`). On the desktop `GameAudio` gives the director its mixer (`SoundMixer`), played through an SDL audio
device; automated runs open no device. The browser shares `SoundDirector` and `SoundBank`: its sink records each
step's sounds into the frame, and the page plays them with the bank's samples (`docs/browser.md`). Detail is in `src/Emberward.Desktop/ARCHITECTURE.md`.

## Tests

`dotnet test Emberward.slnx` runs one xUnit project, `tests/Emberward.Tests`, which references both Core and Desktop.

- **Core**: terrain and pixels, collision, level generation, tank movement, heat and the temperature band, relays,
  the power loop, network coverage, hot and frost rock, the sector goal and base upgrades, the thermal ledger and whole-world thermal behaviour, determinism, replays and
  recorded fingerprints, match outcome, network status, placement preview, fog of war and radar (`VisionMapTests`, `RadarTests`), emberite ore (deposits, mining,
  the economy, the radar's echo) and the bot.
- **Desktop logic that needs no window**: HUD decisions (`HudLogicTests`: hints, heat scale, the HUD's scale,
  gap and covered area), the tutorial and
  event notifications (`GuidanceTests`), entity
  interpolation (`EntityInterpolatorTests`), beam fading (`BeamFaderTests`), machine presentation (`MachinePresentationTests`), terrain aux upload
  planning (`TerrainAuxUploaderTests`), fog rendering (`FogRenderingTests`), world construction (`WorldFactoryTests`), and the solid field and material
  coverage the terrain shader samples.
- **Network matches**: the input codec and messages (`MessageCodecTests`), the relay's rooms and tick rule
  (`MatchRoomTests`, `LockstepRoomTests`, `SnapshotHoldTests`), client sessions and joining in progress
  (`NetSessionTests`, `NetSessionJoinTests`), recordings, and a relay over real
  sockets (`RelayOverSocketsTests`; the tests that wait on a timer or a paced match are its nested classes in
  `TimedTestGroup`, which never run in parallel with the rest, and the quick run skips them).
  The network menu: `LaunchArgumentsTests` (the command line and the arguments of the next run, the secret kept out
  of them), `NetMenuModelTests`, `PlayerSettingsTests`, `TextInputSuppressesHotkeysTests` and `NetMenuRefusalTests`
  (what the real relay says to a refused launch).
- **Guard tests** keep the structure in place; `AGENTS.md` lists them.

How the suite stays quick: a test over about five seconds is marked `[Trait("Category", "Slow")]`, so
`--filter "Category!=Slow"` skips it. xUnit runs the tests of one class one after another, so a long class is split
into nested classes to let its parts overlap, and `SlowFirstCollectionOrderer` starts the collections that hold slow
tests first (a class-level `Slow` counts for all of its tests), so a long collection does not end up running alone
at the end. Tests that depend on real time (sockets, keep-alives, a room's clock) go in `TimedTestGroup` and run
last, one at a time.

The window, GPU backend and sound device have no unit tests. They are checked with scripted hidden runs that save
final-frame screenshots and, for sound, a WAV capture and an event log (see "Automated runs" in `README.md`).
