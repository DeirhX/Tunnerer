// Which .NET runtime this browser gets. The page and the service worker both ask here, so that the runtime a match
// starts in is the one kept for offline play.
//
// The threaded runtime lets the heat solver, most of a simulation step, use several cores. It needs memory shared
// between threads, which browsers give only to a cross-origin isolated page (the host must send two headers:
// docs/browser.md), and it is worth its start-up only where there are cores to spare. Everywhere else the
// single-thread runtime runs in a worker, as it does in a deployment published without the threaded one.
(() => {
    // Whether this deployment carries the threaded runtime at all. `publish.py --threads` sets it.
    const published = false;
    // How many bytes of .wasm each runtime downloads, for the loading bar; publish.py fills it in.
    const runtimeBytes = { single: 0, threads: 0 };
    const cores = navigator.hardwareConcurrency || 1;
    // Off until .NET's threaded interop stops killing the runtime: now and then, a few minutes into a match, .NET
    // fails in ReleaseJSOwnedObjectByGCHandle on one of its own threads, the runtime exits with code 1 and the page
    // is left with "Assert failed: .NET runtime already exited with 1" (measured in Edge on Windows; docs/browser.md
    // has it). The single-thread runtime does not. ?threads=4 in the address uses the threaded runtime anyway.
    const threadedByDefault = false;
    // Only Chromium (Chrome, Edge and their relatives) so far. In WebKit the threaded runtime sometimes stops
    // answering the page in the middle of a match (docs/browser.md has what was measured), and Firefox has not
    // been tried. ?threads=4 in the address tries it in any browser.
    const chromium = Boolean(
        navigator.userAgentData?.brands?.some((b) => b.brand === "Chromium"),
    );
    const possible = published && self.crossOriginIsolated === true;
    self.emberwardThreads = {
        possible,
        use: threadedByDefault && possible && chromium && cores > 2,
        // One core is left to the page, which draws while the simulation steps. More than eight workers were not
        // measured (spikes/WasmThreads) and each is a thread to start.
        heatWorkers: Math.max(2, Math.min(8, cores - 1)),
        runtimeBytes,
    };
})();
