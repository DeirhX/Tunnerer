// The way into .NET (BrowserApi.cs), shared by the two places a runtime lives: worker.js loads the single-thread
// one, simulation.js the threaded one. Every call into .NET answers with a promise in both.

// Starts a runtime from `dotnet`, the builder its dotnet.js exports.
export async function startRuntime(dotnet, config = {}) {
    const runtime = await dotnet
        .withDiagnosticTracing(false)
        .withConfig(config)
        .create();
    let delivered = null,
        sounds = null;
    runtime.setModuleImports("frames.js", {
        // .NET hands over each frame here. `packet` is one buffer with everything the frame draws (layout:
        // BrowserScene.cs), as a view of the runtime's memory that is good for the length of this call. The copy
        // made of it is an ordinary buffer of its own: the worker hands it to the page without another copy, and
        // WebGL takes it, which it would not take of the memory the threaded runtime shares between its threads.
        deliver: (packet, hud, stepMs, frameMs) => {
            delivered = {
                packet: packet.slice(),
                hud,
                timing: { step: stepMs < 0 ? null : stepMs, render: frameMs },
            };
        },
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
    return {
        // Answers one request of the page, { type: "start" } or { type: "render" }, with the frame message it
        // waits for. A render request with `input` steps the simulation once before the frame is built. A request
        // with `sounds` also gets the sound bank, as the frame's `sounds`.
        async answer(data) {
            if (data.type === "start")
                await api.Start(
                    data.seed,
                    data.heatWorkers,
                    data.mapWidth || 0,
                    data.mapHeight || 0,
                    data.machines || 0,
                );
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
            );
            const { packet, hud, timing } = delivered;
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
                sounds: bank,
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
