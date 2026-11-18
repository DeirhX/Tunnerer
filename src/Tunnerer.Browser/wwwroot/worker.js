// Owns the single-thread .NET runtime, so that a simulation step never holds up the page.
// Imported here and not with import() when the first match starts: a runtime imported that way inside a worker
// never finishes starting (.NET 10.0, Chromium).
import { dotnet } from "./_framework/dotnet.js";
import { errorMessage, startRuntime } from "./frames.js";

let runtime;
// Stages of a start for the page's loading line (progress.js).
const report = (step) => self.postMessage({ type: "progress", ...step });
self.onmessage = async ({ data }) => {
    if (!["start", "join", "render"].includes(data.type)) return;
    try {
        runtime ||= await startRuntime(dotnet, { report, bytes: data.runtimeBytes, threads: "one thread" });
        const frame = await runtime.answer(data, report);
        const transfer = frame.packet ? [frame.packet.buffer] : [];
        if (frame.sounds) transfer.push(frame.sounds.samples.buffer);
        if (frame.send) transfer.push(frame.send.buffer);
        self.postMessage(frame, transfer);
    } catch (error) {
        self.postMessage(errorMessage(error));
    }
};
