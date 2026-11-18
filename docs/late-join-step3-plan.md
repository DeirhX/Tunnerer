# Late join, step 3: snapshots in Core — status and what is left

Step 3 of the build order in [late-join-design.md](late-join-design.md) §13. This file records where the work
stopped (2026-11-18) and the rest of the plan, so it can be picked up from here.

## Done

**Save and load** (`src/Tunnerer.Core/Snapshots/`):

- `WorldSnapshot.Save(world, bots)` returns raw, canonical bytes: the same state always gives the same bytes.
  `WorldSnapshot.Load(freshWorld, bytes)` loads them into a world freshly built from the same settings, and returns
  the bots. `Compress` and `Decompress` use Brotli. `Decompress` refuses output over `MaxBytes` (32 MiB), because the
  bytes come from another player and could be a decompression bomb.
- **A general saver.** Every field reachable from the world and the bots is saved, unless it is marked
  `[NotSaved("reason")]`. The mark can go on a field, a class or a struct.
  - Objects are written breadth first, so a long chain of links doesn't recurse.
  - Each object is written once; later references use its id.
  - `MachineBehaviors.All` are written as constants.
  - Arrays of primitives and enums are copied as raw bytes. Structs are written field by field, never as raw memory,
    because padding would make the bytes non-canonical.
- **Lists are saved by count and items, through the public API.** This corrects the design doc, which said to copy
  the internals of `List` and `Dictionary`. Those internals belong to the runtime, which is the same problem as
  `System.Random`. Dictionaries keyed by strings are also laid out differently in every process.
- **Keyed lookups are rebuilt on load** through `ISnapshotLoaded`:
  - `MachineList._buckets`. Every caller that doesn't sort the result is an order-free "is anything here" query.
  - `LinkMap._linksByTile`. Rechecking a link doesn't depend on the order links are met in.
- **Loading fills objects in place** wherever the built world already holds one of the same type at the same spot
  (the world, its systems, the tanks, the terrain arrays). Settings and delegates that close over the world stay
  wired to it. Objects created during play, such as machines and links, are made new.
- **22 marks**, each with a reason:
  - Fixed for the match: settings, rules, heat engines, contexts, the system list, the heat-tile delegates, the
    worker options.
  - Output only: step events, vision and radar, the render change list.
  - Diagnostics: `SimulationProfile` and `LastCoolDownProfile`. These hold wall-clock timings, so saving them would
    make two honest uploaders' bytes differ.
  - Scratch: the heat solver's per-tile delta buffers (about 1.6 MB), and the regrowth blockers' `HashSet`.
  - A pool: regrowth spare tiles, which only ever hold all-zero arrays.

**Round-trip tests** (`SnapshotRoundTripTests`, 10 tests). Each one saves mid-match, loads into a freshly built
world, and checks:
- the loaded world saves back to the same bytes;
- both worlds then give the same full fingerprint (both temperature fields cell by cell, the ledger) and the same
  sync digest every tick;
- they save to the same bytes at the end.

The scenarios:
- bots mid-match with hot and frost pockets and ore
- a link network and a working harvester
- machines wrecked after the load
- a link cut by new rock after the load
- a tank respawning, then under spawn protection
- the shield up and the build and upgrade keys held across the load
- a core tap mid-hold

Also: saving is canonical and loading twice gives the same world; compression round-trips and the size cap works;
a snapshot of another size, a truncated one or a foreign one is refused.

**The tests fail when they should** (mutation checks, by hand):
- Leaving `ProjectileList._rng` out fails 4 scenarios, caught on load in the `Latent` part of the digest.
- Skipping the machine-bucket rebuild fails 2 scenarios, a few ticks after the load.
- Skipping the link-tile rebuild failed nothing at first. That is why the link-cut scenario was added; it now fails
  13 ticks after the load.
- Leaving `TankTurret._cooldown` out was **not** caught: the cooldown was back to 0 at every save, the same value a
  freshly built world has.

The general lesson: a field wrongly marked `NotSaved` shows up only when its live value differs from a built
world's. The list of marks in the coverage test (below) is the defence against that.

## What is left in step 3

1. **Faster round-trip tests.** The class takes about 40 s. Hashing both temperature fields every tick is most of
   it. Compare the sync digest every tick (it already hashes every cell's temperature bits) and the full fingerprint
   every 20 ticks and at the end. Mark any test still over 5 s `[Trait("Category", "Slow")]`, and give each slow test
   its own class if needed (AGENTS.md).
2. **Coverage guard test** (`SnapshotCoverageTests`; design §10.3, item 1).
   - Walk every type reachable from `World` and the bot types: field types, element types, generic arguments, and
     the runtime types met in a saved scenario world.
   - Fail on any field whose type the saver can't write (dictionary, set, stack, delegate, other `System` classes)
     unless it is marked `NotSaved`.
   - Fail on any mark with an empty reason.
   - Keep an explicit list of every `NotSaved` mark ("Type.field: reason") in the test, as a ratchet like
     `SourceGuardTests`. A new mark fails until it is added there, so leaving something out is always a reviewed
     decision.
   - Also: saving every fingerprint scenario succeeds (the writer throws on anything unsupported).
3. **More scenarios:**
   - regrowth busy across the load: dig after the load, scorch fading;
   - a machine being carried (`MachineMaterializer`) across the load;
   - projectiles pending at the save;
   - the four fingerprint scenarios themselves, saved halfway.

   Then repeat the mutation checks above for a few fields of each owner.
4. **Measure**, in Release:
   - raw and compressed size (Brotli `Optimal` against `Fastest`);
   - save, load, compress and decompress times;
   - on 480 x 300 and on 1500 x 750, on this Mac, scaled for weaker machines
     ([[dev-mac-is-fast-target-weaker-cpus]]).

   Record the numbers in design §13. They decide whether the §9.3 limits hold, and whether the "send only what
   differs from a built world" idea in §14 is needed sooner.
5. **Docs:**
   - Design §9.1: lists are saved through their public API, keyed lookups are rebuilt, nothing copies runtime
     internals.
   - Design §10.3: the masking caveat above.
   - Design §13: mark step 3 done, with the measurements.
   - `src/Tunnerer.Core/ARCHITECTURE.md`: a `Snapshots/` entry.
   - AGENTS.md: a rule. A new field is saved by default; mark it `[NotSaved("reason")]` only when it is rebuilt,
     fixed for the match, output only or scratch, and add it to the coverage test's list.
6. **Full suite and `dotnet format --verify-no-changes`**, then commit with the measurements in the message.

## Carried to later steps

- **Compare uploaders on raw bytes, not compressed ones** (step 4). Brotli's output can differ between .NET patch
  versions for identical input. Hash the raw bytes (SHA-256, or a laned FNV like the sync digest) and send that
  digest with `SnapshotDone`.
- **After loading, the client must rebuild what Core leaves to it** (step 6):
  - recast vision and radar from scratch (`SideSensing` is not saved);
  - upload the whole terrain to the renderer (the change list is not saved);
  - read no step events from the load tick.
- **The browser build.** The saver uses reflection and `Type.GetType` with assembly-qualified names. Trimming in
  the WebAssembly build could remove type information it needs. If browser players are to join network matches,
  check a trimmed build, or swap the reflection walk for a source generator. The coverage test stays the same
  either way.
- **Sizes and limits** (step 5): keep the relay's 32 MiB cap in step with `WorldSnapshot.MaxBytes`.
