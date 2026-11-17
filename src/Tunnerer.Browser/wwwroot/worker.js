import { dotnet } from "./_framework/dotnet.js";

let api;
self.onmessage = async ({ data }) => {
    try {
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
            }
        } else {
            return;
        }
        api.Render(
            data.width,
            data.height,
            data.scale,
            data.alpha || 0,
            data.delta || 0,
            data.time || 0,
        );
        const uniforms = api.Uniforms(),
            vertices = api.Vertices();
        const textures = [0, 1, 2, 3].map((i) => api.Texture(i));
        const render = {
            uniforms,
            vertices,
            textures,
            rect: api.TextureRect(),
        };
        const state = JSON.parse(api.GetFrame(!!data.input));
        self.postMessage({ type: "frame", state, render }, [
            uniforms.buffer,
            vertices.buffer,
            ...textures.map((t) => t.buffer),
        ]);
    } catch (error) {
        self.postMessage({
            type: "error",
            message: `${error?.message || String(error)}\n${error?.stack || ""}`,
        });
    }
};
