// The simulation as the page talks to it: a worker, or something that answers like one. Which of the two is
// decided in threads.js.
//
// - Single-thread runtime: worker.js owns it. Ending a match ends the worker and its memory.
// - Threaded runtime: .NET can only start it from the page itself, not inside a worker. It then runs on threads of
//   its own, and the page's calls are answered later, so the page stays free all the same. A runtime cannot be
//   unloaded, so it is loaded once and the matches after the first start in it at once.
import { errorMessage, startRuntime } from "./frames.js";

// ?threads=0 keeps to one thread where the threaded runtime would be used, and ?threads=3 gives the heat solver
// three workers, also in a browser where it would not be used: for comparing the two on one device.
const asked = Number.parseInt(
    new URLSearchParams(location.search).get("threads"),
    10,
);
const plan = self.tunnererThreads || {};
const plannedWorkers = !plan.possible
    ? 1
    : Number.isNaN(asked)
      ? plan.use
          ? plan.heatWorkers
          : 1
      : Math.max(1, Math.min(16, asked));
// Beside the heat workers .NET starts threads of its own (the one managed code runs on, timers, the garbage
// collector's). Each is a Web Worker that takes time to start, so they are all asked for before the match begins.
const RUNTIME_THREADS = 6;
let threaded = null,
    threadsFailed = false;

export class Simulation {
    constructor() {
        this.onmessage = this.onerror = null;
        this.closed = false;
        this.worker = null;
        // How many threads step the heat. One: the single-thread runtime.
        this.heatWorkers = threadsFailed ? 1 : plannedWorkers;
        if (this.heatWorkers === 1) this.startWorker();
    }

    startWorker() {
        this.heatWorkers = 1;
        this.worker = new Worker(new URL("./worker.js", import.meta.url), {
            type: "module",
        });
        this.worker.onerror = (event) => this.onerror?.(event);
        this.worker.onmessage = (event) => this.onmessage?.(event);
    }

    // The page keeps one request in flight, so answers come back in the order asked.
    postMessage(data) {
        if (this.worker) this.worker.postMessage({ ...data, heatWorkers: 1 });
        else this.ask(data);
    }

    async ask(data) {
        let runtime;
        try {
            threaded ||= import("./_framework-threads/dotnet.js").then(
                ({ dotnet }) =>
                    startRuntime(dotnet, {
                        pthreadPoolInitialSize:
                            this.heatWorkers + RUNTIME_THREADS,
                    }),
            );
            runtime = await threaded;
        } catch (error) {
            // A browser that promised shared memory and then could not start the runtime still gets a game.
            console.warn(
                "The threaded runtime did not start; playing on one thread.",
                error,
            );
            threaded = null;
            threadsFailed = true;
            if (this.closed) return;
            this.startWorker();
            this.worker.postMessage({ ...data, heatWorkers: 1 });
            return;
        }
        let message;
        try {
            message = await runtime.answer({
                ...data,
                heatWorkers: this.heatWorkers,
            });
        } catch (error) {
            message = errorMessage(error);
        }
        if (!this.closed) this.onmessage?.({ data: message });
    }

    // Ends the match. The threaded runtime stays for the next one; a late answer from this one is dropped.
    terminate() {
        this.closed = true;
        this.worker?.terminate();
    }
}
