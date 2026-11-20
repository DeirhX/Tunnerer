// The way into .NET (BrowserApi.cs), shared by the two places a runtime lives: worker.js loads the single-thread
// one, simulation.js the threaded one. Every call into .NET answers with a promise in both.

// Starts a runtime from `dotnet`, the builder its dotnet.js exports, and reports it to the page's loading line and
// bar (progress.js) through `report`: first downloading its `bytes` of .wasm, as they arrive, then starting it.
// `threads` says how it runs, for the console.
export async function startRuntime(dotnet, { config = {}, report = () => {}, bytes = 0, threads = "" } = {}) {
    const starting = { stage: "runtime", label: "Starting the game runtime", state: "begin" };
    let downloading = bytes > 0,
        loaded = 0,
        reportedAt = 0;
    const downloaded = () => {
        downloading = false;
        report({ stage: "download", state: "end", detail: `${(loaded / 1048576).toFixed(1)} MiB` });
        report(starting);
    };
    const builder = dotnet.withDiagnosticTracing(false).withConfig(config);
    if (downloading) {
        report({ stage: "download", label: "Downloading the game", state: "begin" });
        builder.withResourceLoader(
            countingLoader((count) => {
                loaded += count;
                if (!downloading) return;
                if (loaded >= bytes) downloaded();
                else if (performance.now() - reportedAt >= 100) {
                    reportedAt = performance.now();
                    report({ stage: "download", state: "bytes", loaded, total: bytes });
                }
            }),
        );
    } else report(starting);
    const runtime = await builder.create();
    // Fewer bytes than the publish counted: a file .NET did not need.
    if (downloading) downloaded();
    // Bytes cross from .NET as an address and a length in its memory, which it keeps pinned until its next call of
    // that kind, and are copied out here once its promise has resolved. Nothing is handed over by a call from .NET
    // into the page, and nothing is marshalled as a view or an object: in the threaded runtime a call from .NET's
    // thread into the page's is synchronous across threads, and WebKit's page froze in one; and .NET frees the proxy
    // of a marshalled value when the browser collects it, which now and then failed and ended the runtime ("already
    // exited with 1"). The copy is an ordinary buffer of its own (WebGL takes no view of memory that threads share,
    // and a worker transfers it without another copy). The heap's view is taken anew each time because the memory
    // may have grown.
    const copyOut = (address, length) => runtime.localHeapViewU8().slice(address, address + length);
    let netMatch = false;
    await runtime.runMain();
    const exports = await runtime.getAssemblyExports(
        runtime.getConfig().mainAssemblyName,
    );
    const api = exports.Emberward.Browser.BrowserApi;
    report({ stage: "runtime", state: "end", detail: threads });
    // What the request made for the relay: one buffer, which the worker hands to the page without a copy.
    const takeOutgoing = async () => {
        if (!netMatch) return null;
        const { address, length } = JSON.parse(await api.TakeSend());
        return length ? copyOut(address, length) : null;
    };
    return {
        // Answers one request of the page, { type: "start" }, { type: "join" } or { type: "render" }, with the
        // frame message it waits for. A render request with `input` steps a local match once before the frame is
        // built; a network match steps as the relay's ticks have arrived. A request with `sounds` also gets the
        // sound bank, as the frame's `sounds`.
        //
        // A network match (net.js): "join" says hello, and a request's `received` (and `lost`, when the socket
        // closed) is what the page's socket got, and `command` what the player asked of the room. Every answer
        // carries what to send to the relay as `send`. Until the relay has let the player in and the world is
        // built, the answer is { type: "net", failure } instead of a frame.
        //
        // `report` hears of the slow stages of a start, as { stage, label, state: "begin" | "end" } (progress.js).
        async answer(data, report = () => {}) {
            if (data.type === "start") {
                netMatch = false;
                report({ stage: "world", label: "Generating the world", state: "begin" });
                await api.Start(
                    data.seed,
                    data.heatWorkers,
                    data.mapWidth || 0,
                    data.mapHeight || 0,
                    data.machines || 0,
                    Boolean(data.energyNetwork),
                );
                report({ stage: "world", state: "end" });
            }
            if (data.type === "join") {
                netMatch = true;
                await api.Join(
                    data.room,
                    data.name,
                    data.players,
                    data.seed,
                    data.mapWidth || 0,
                    data.mapHeight || 0,
                    data.heatWorkers,
                );
            }
            if (data.type === "join" || data.received || data.lost) {
                const waiting = await api.Receive(
                    data.received || new Uint8Array(0),
                    data.lost || null,
                );
                if (waiting !== null)
                    return {
                        type: "net",
                        failure: waiting || null,
                        send: await takeOutgoing(),
                    };
            }
            if (data.command) await api.Command(data.command);
            const i = data.input;
            const answer = JSON.parse(
                await api.Frame(
                Boolean(i),
                i?.x || 0,
                i?.y || 0,
                Boolean(i?.fire),
                i?.aimX || 0,
                i?.aimY || 0,
                i?.build || 0,
                i?.upgrade || 0,
                Boolean(i?.recall),
                data.width,
                data.height,
                data.scale,
                data.alpha || 0,
                data.delta || 0,
                data.time || 0,
                Boolean(data.thermalView),
                data.powerLabelCellSize || 0,
                Boolean(data.due),
                data.now || 0,
                ),
            );
            // The packet lies in .NET's memory until the next request, which comes after this copy.
            const packet = copyOut(answer.address, answer.length),
                { hud, steps } = answer,
                timing = { step: answer.stepMs < 0 ? null : answer.stepMs, render: answer.frameMs };
            let bank = null;
            if (data.sounds) {
                const { address, length, layout } = JSON.parse(await api.SoundBank());
                bank = { samples: new Float32Array(copyOut(address, length).buffer), layout };
            }
            // The HUD's state comes only with frames after which it may differ: the first, and those that follow
            // a step.
            return {
                type: "frame",
                state: hud || null,
                packet,
                timing,
                steps,
                sounds: bank,
                send: await takeOutgoing(),
            };
        },
        // The world a short match nobody watches ends with (BrowserApi.Fingerprint), for comparing runtimes.
        fingerprint: (seed, heatWorkers, ticks) =>
            api.Fingerprint(seed, heatWorkers, ticks),
    };
}

export function errorMessage(error) {
    return {
        type: "error",
        message: `${error?.message || String(error)}\n${error?.stack || ""}`,
    };
}

// A loader for .NET's own downloads (its .wasm files) that hands `count` the bytes of each as they arrive. It fetches
// as .NET would, and .NET gets the response that fetch made: the browser keeps the compiled code of a .wasm by the
// response's address, which a response made up here would not have.
//
// .NET asks the browser to check each file against the hash it was published with (subresource integrity). The
// browser then holds back every byte, also from a copy of the response, until all have arrived, so the bar could
// not move during the 8 MB the runtime is mostly made of. This loader fetches without that and checks the hash
// itself once the copy is read; .NET gets the response then, as it would have from the browser, and a file that
// does not match stops the load as before. Where it cannot check (no WebCrypto off https), the browser does.
function countingLoader(count) {
    const counted = new Set(["assembly", "dotnetwasm", "globalization", "pdb"]);
    return (type, name, url, integrity) => {
        if (!counted.has(type)) return undefined;
        const algorithm = integrity && DIGESTS[integrity.slice(0, integrity.indexOf("-"))];
        if (integrity && !(algorithm && globalThis.crypto?.subtle)) return undefined;
        return fetch(url, { cache: "no-cache" }).then(async (response) => {
            if (!response.ok || !response.body) return response;
            const copy = readAll(response.clone().body, count, Boolean(integrity));
            if (!integrity) {
                // Counted as .NET reads it; a failed read fails .NET's too, which reports it.
                copy.catch(() => {});
                return response;
            }
            const digest = await crypto.subtle.digest(algorithm, await copy);
            if (!integrity.endsWith(`-${base64(digest)}`))
                throw new Error(`${name} is not the file this version of the game was published with; reload the page`);
            return response;
        });
    };
}

const DIGESTS = { sha256: "SHA-256", sha384: "SHA-384", sha512: "SHA-512" };

// Reads a body to its end, handing `count` the size of each part, and gives back all its bytes if asked to `keep` them.
async function readAll(body, count, keep) {
    const reader = body.getReader(),
        parts = [];
    let size = 0;
    for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        if (keep) parts.push(value);
        size += value.byteLength;
        count(value.byteLength);
    }
    if (!keep) return null;
    const bytes = new Uint8Array(size);
    let at = 0;
    for (const part of parts) {
        bytes.set(part, at);
        at += part.byteLength;
    }
    return bytes;
}

function base64(buffer) {
    let text = "";
    for (const byte of new Uint8Array(buffer)) text += String.fromCharCode(byte);
    return btoa(text);
}
