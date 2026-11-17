#!/usr/bin/env python3
"""Serve a published game locally. HTTPS is required when hosting anywhere except localhost."""
import argparse
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


class Handler(SimpleHTTPRequestHandler):
    extensions_map = {**SimpleHTTPRequestHandler.extensions_map, ".wasm": "application/wasm",
                      ".webmanifest": "application/manifest+json", ".js": "text/javascript"}

    isolate = False

    def end_headers(self):
        if self.isolate:
            self.send_header("Cross-Origin-Opener-Policy", "same-origin")
            self.send_header("Cross-Origin-Embedder-Policy", "require-corp")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("X-Content-Type-Options", "nosniff")
        super().end_headers()

    def log_request(self, code="-", size="-"):
        if isinstance(code, int) and code >= 400:
            super().log_request(code, size)


class Server(ThreadingHTTPServer):
    # Runtime and shader loading overlap with offline precaching on a first visit.
    request_queue_size = 128


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", nargs="?", type=Path, default=Path("artifacts/browser/wwwroot"))
    parser.add_argument("--port", type=int, default=8703)
    parser.add_argument("--isolate", action="store_true",
                        help="Send the cross-origin isolation headers, which a deployment published with --threads "
                             "needs before a browser lets it use its threaded runtime.")
    args = parser.parse_args()
    Handler.isolate = args.isolate
    if not (args.directory / "assets-manifest.js").is_file():
        parser.error("Run python3 tools/browser/publish.py first (offline manifest missing).")
    print(f"Tunnerer: http://localhost:{args.port}", flush=True)
    Server(("127.0.0.1", args.port), partial(Handler, directory=str(args.directory.resolve()))).serve_forever()
