// The page's WebSocket to the relay in a network match (docs/multiplayer-design.md). It only carries bytes: the
// messages are made and read in .NET (BrowserNetMatch.cs), which the page hands what arrived with each request
// and which answers with what to send (frames.js).

// The relay of the site the page came from, where it has one (the server proxies /play to it), else the shared
// one. ?relay=ws://localhost:8130/play picks another, for a relay run locally.
export function defaultRelay() {
    const asked = new URLSearchParams(location.search).get("relay");
    if (asked) return asked;
    if (location.hostname === "crust.bokovka.cz")
        return `wss://${location.host}/play`;
    return "wss://crust.bokovka.cz/play";
}

export class RelaySocket {
    constructor(url) {
        this.received = [];
        this.lost = null;
        this.open = false;
        this.socket = new WebSocket(url);
        this.socket.binaryType = "arraybuffer";
        this.opened = new Promise((resolve, reject) => {
            this.socket.onopen = () => {
                this.open = true;
                resolve();
            };
            this.socket.onerror = () => {
                if (!this.open) reject(new Error(`Could not reach the relay at ${url}`));
            };
        });
        this.socket.onmessage = ({ data }) => {
            if (data instanceof ArrayBuffer)
                this.received.push({ bytes: new Uint8Array(data), at: performance.now() });
        };
        this.socket.onclose = ({ wasClean, reason }) => {
            this.open = false;
            this.lost ||=
                wasClean && reason
                    ? `the relay closed the connection (${reason}).`
                    : wasClean
                      ? "the relay closed the connection."
                      : "lost the connection to the relay.";
        };
    }

    // Whether there is something new for .NET: messages, or that the connection ended.
    get pending() {
        return this.received.length > 0 || (this.lost !== null && !this.lostTaken);
    }

    // What arrived since the last call, as BrowserApi.Receive reads it: each message's arrival time on the page's
    // clock (a little-endian double), its length (int32) and its bytes. And why the connection ended, once.
    take() {
        const length = this.received.reduce((sum, m) => sum + 12 + m.bytes.length, 0);
        const packed = new Uint8Array(length);
        const view = new DataView(packed.buffer);
        let at = 0;
        for (const { bytes, at: arrived } of this.received) {
            view.setFloat64(at, arrived, true);
            view.setInt32(at + 8, bytes.length, true);
            packed.set(bytes, at + 12);
            at += 12 + bytes.length;
        }
        this.received = [];
        let lost = null;
        if (this.lost !== null && !this.lostTaken) {
            this.lostTaken = true;
            lost = this.lost;
        }
        return { received: length ? packed : null, lost };
    }

    // Sends what .NET made (frames.js): messages, each as its length (int32) and bytes.
    send(packed) {
        if (!packed || !this.open) return;
        const view = new DataView(packed.buffer, packed.byteOffset, packed.byteLength);
        for (let at = 0; at + 4 <= packed.byteLength; ) {
            const length = view.getInt32(at, true);
            this.socket.send(packed.subarray(at + 4, at + 4 + length));
            at += 4 + length;
        }
    }

    close() {
        this.lost ||= "left the match.";
        this.lostTaken = true;
        this.socket.close(1000, "left");
    }
}
