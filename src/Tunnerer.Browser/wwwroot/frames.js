// The way into .NET (BrowserApi.cs), shared by the two places a runtime lives: worker.js loads the single-thread
// one, simulation.js the threaded one. Every call into .NET answers with a promise in both.

// Starts a runtime from `dotnet`, the builder its dotnet.js exports.
export async function startRuntime(dotnet, config = {}) {
    const runtime = await dotnet
        .withDiagnosticTracing(false)
        .withConfig(config)
        .create();
    let delivered = null,
        sounds = null,
        outgoing = [];
    runtime.setModuleImports("frames.js", {
        // .NET hands over each frame here. `packet` is one buffer with everything the frame draws (layout:
        // BrowserScene.cs), as a view of the runtime's memory that is good for the length of this call. The copy
        // made of it is an ordinary buffer of its own: the worker hands it to the page without another copy, and
        // WebGL takes it, which it would not take of the memory the threaded runtime shares between its threads.
        deliver: (packet, hud, stepMs, frameMs, steps) => {
            delivered = {
                packet: packet.slice(),
                hud,
                steps,
                timing: { step: stepMs < 0 ? null : stepMs, render: frameMs },
            };
        },
        // Messages for the relay in a network match, each as its length (int32) and bytes, for the page's socket
        // (net.js) to send.
        deliverNet: (packed) => outgoing.push(packed.slice()),
        // The sound bank (BrowserSoundBank): every effect's samples, copied out of the runtime's memory as above.
        deliverSounds: (samples, layout) => {
            sounds = {
                samples: new Float32Array(samples.slice().buffer),
                layout: JSON.parse(layout),
            };
        },
    });
    await runtime.runMain();
    const exports = await runtime.getAssemblyExports(
        runtime.getConfig().mainAssemblyName,
    );
    const api = exports.Tunnerer.Browser.BrowserApi;
    // What the request made for the relay: one buffer, which the worker hands to the page without a copy.
    const takeOutgoing = () => {
        if (!outgoing.length) return null;
        const packed = new Uint8Array(
            outgoing.reduce((sum, part) => sum + part.length, 0),
        );
        let at = 0;
        for (const part of outgoing) {
            packed.set(part, at);
            at += part.length;
        }
        outgoing = [];
        return packed;
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
                report({ stage: "world", label: "Generating the world", state: "begin" });
                await api.Start(
                    data.seed,
                    data.heatWorkers,
                    data.mapWidth || 0,
                    data.mapHeight || 0,
                    data.machines || 0,
                );
                report({ stage: "world", state: "end" });
            }
            if (data.type === "join")
                await api.Join(
                    data.room,
                    data.name,
                    data.secret || "",
                    data.players,
                    data.seed,
                    data.mapWidth || 0,
                    data.mapHeight || 0,
                    data.heatWorkers,
                );
            if (data.type === "join" || data.received || data.lost) {
                const waiting = await api.Receive(
                    data.received || new Uint8Array(0),
                    data.lost || null,
                );
                if (waiting !== null)
                    return {
                        type: "net",
                        failure: waiting || null,
                        send: takeOutgoing(),
                    };
            }
            if (data.command) await api.Command(data.command);
            const i = data.input;
            await api.Frame(
                Boolean(i),
                i?.x || 0,
                i?.y || 0,
                Boolean(i?.fire),
                i?.aimX || 0,
                i?.aimY || 0,
                i?.build || 0,
                i?.upgrade || 0,
                data.width,
                data.height,
                data.scale,
                data.alpha || 0,
                data.delta || 0,
                data.time || 0,
                Boolean(data.thermalView),
                Boolean(data.due),
                data.now || 0,
            );
            const { packet, hud, timing, steps } = delivered;
            delivered = null;
            let bank = null;
            if (data.sounds) {
                await api.SoundBank();
                bank = sounds;
                sounds = null;
            }
            // The HUD's state comes only with frames after which it may differ: the first, and those that follow
            // a step.
            return {
                type: "frame",
                state: hud ? JSON.parse(hud) : null,
                packet,
                timing,
                steps,
                sounds: bank,
                send: takeOutgoing(),
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
