// Owns the single-thread .NET runtime, so that a simulation step never holds up the page.
// Imported here and not with import() when the first match starts: a runtime imported that way inside a worker
// never finishes starting (.NET 10.0, Chromium).
import { dotnet } from "./_framework/dotnet.js";
import { errorMessage, startRuntime } from "./frames.js";

let runtime;
self.onmessage = async ({ data }) => {
    if (data.type !== "start" && data.type !== "render") return;
    try {
        runtime ||= await startRuntime(dotnet);
        const frame = await runtime.answer(data);
        self.postMessage(frame, [frame.packet.buffer]);
    } catch (error) {
        self.postMessage(errorMessage(error));
    }
};
