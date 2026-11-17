import { dotnet } from "./_framework/dotnet.js";

let api;
self.onmessage = async ({ data }) => {
    try {
        let stepped = false;
        const began = performance.now();
        if (data.type === "start") {
            if (!api) {
                const runtime = await dotnet
                    .withDiagnosticTracing(false)
                    .create();
                await runtime.runMain();
                const exports = await runtime.getAssemblyExports(
                    runtime.getConfig().mainAssemblyName,
                );
                api = exports.Tunnerer.Browser.BrowserApi;
            }
            api.Start(data.seed);
        } else if (data.type === "render") {
            if (data.input) {
                const i = data.input;
                api.Step(i.x, i.y, i.fire, i.aimX, i.aimY, i.build, i.upgrade);
                stepped = true;
            }
        } else {
            return;
        }
        const simulated = performance.now();
        // One buffer holds everything the frame draws (layout: BrowserScene.cs). The view is of the runtime's own
        // memory, so copy it out before the next call; the copy is handed to the page without another one.
        const view = api.Render(
            data.width,
            data.height,
            data.scale,
            data.alpha || 0,
            data.delta || 0,
            data.time || 0,
        );
        const packet = view.slice();
        view.dispose();
        // The HUD only changes when the simulation steps.
        const state =
            stepped || data.type === "start"
                ? JSON.parse(api.GetFrame(stepped))
                : null;
        const timing = {
            step: stepped ? simulated - began : null,
            render: performance.now() - simulated,
        };
        self.postMessage({ type: "frame", state, packet, timing }, [
            packet.buffer,
        ]);
    } catch (error) {
        self.postMessage({
            type: "error",
            message: `${error?.message || String(error)}\n${error?.stack || ""}`,
        });
    }
};
