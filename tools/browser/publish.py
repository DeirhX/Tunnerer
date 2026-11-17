#!/usr/bin/env python3
"""Publish a complete static PWA, including a content-versioned offline asset manifest."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import zipfile


def package(output):
    root = output / "wwwroot"
    if not (root / "_framework" / "dotnet.js").is_file():
        raise RuntimeError(f"Missing .NET browser runtime in {root}")
    assets = sorted(p for p in root.rglob("*") if p.is_file()
                    and p.suffix not in {".br", ".gz", ".pdb", ".map"}
                    and p.name not in {"assets-manifest.js", "sw.js", "_headers"})
    digest = hashlib.sha256()
    for path in assets:
        digest.update(path.relative_to(root).as_posix().encode())
        digest.update(hashlib.sha256(path.read_bytes()).digest())
    # Worker behavior is also part of the version, even though sw.js must never serve itself from its cache.
    digest.update((root / "sw.js").read_bytes())
    manifest = {"version": digest.hexdigest()[:24],
                "urls": ["./" + p.relative_to(root).as_posix() for p in assets]}
    # The same bytes on every OS: no locale encoding, no translated line ends.
    (root / "assets-manifest.js").write_bytes(("self.tunnererAssets = " + json.dumps(manifest) + ";\n").encode())
    archive = output / "tunnerer-browser.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as bundle:
        for path in sorted(root.rglob("*")):
            if path.is_file():
                bundle.write(path, path.relative_to(root))
    size = sum(p.stat().st_size for p in assets) / 1024 / 1024
    print(f"Published {len(assets)} offline assets ({size:.1f} MiB), version {manifest['version']}")
    print(f"Serve or deploy: {root}\nStatic hosting bundle: {archive}")
    return manifest


def main():
    repo = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=repo / "artifacts" / "browser")
    parser.add_argument("--configuration", choices=["Debug", "Release"], default="Release")
    parser.add_argument("--no-aot", action="store_true", help="Faster local builds; slower game execution.")
    parser.add_argument("--package-only", action="store_true", help="Repackage an already published wwwroot.")
    parser.add_argument("--restore-workloads", action="store_true",
                        help="First install the .NET workloads this publish needs (changes the SDK installation).")
    args = parser.parse_args()
    output = args.output.resolve()
    if not args.package_only:
        # Publish does not remove assets from older builds; never mix two runtime versions in the offline cache.
        if (output / "wwwroot").is_dir():
            shutil.rmtree(output / "wwwroot")
        dotnet = shutil.which("dotnet") or str(Path.home() / ".dotnet" / "dotnet")
        env = dict(os.environ)
        env["PATH"] = str(Path(dotnet).parent) + os.pathsep + env.get("PATH", "")
        shader_dir = repo / "src/Tunnerer.Browser/wwwroot/shaders"
        shader_source = repo / "src/Tunnerer.Desktop/Rendering/Shaders"
        shaders = [("FullscreenVs", "vertex"), ("TerrainPs", "fragment"), ("PostPs", "fragment"),
                   ("EntityVs", "vertex"), ("EntityPs", "fragment")]
        shader_args = [str(shader_source / (name + ".hlsl")) + "=" + stage for name, stage in shaders]
        subprocess.run([dotnet, "run", "--project", str(repo / "tools/Tunnerer.ShaderCompiler"),
                        "-c", args.configuration, "--", "--web", "--out", str(shader_dir)] + shader_args,
                       cwd=repo, env=env, check=True)
        # Native shader binaries are needed by desktop only; the browser ships the generated GLSL.
        for path in shader_dir.iterdir():
            if path.suffix != ".glsl":
                path.unlink()
        project = str(repo / "src/Tunnerer.Browser/Tunnerer.Browser.csproj")
        if args.restore_workloads:
            # Which workload that is depends on the SDK in use (wasm-tools on .NET 10, wasm-tools-net10 on later
            # ones), so let the SDK say. It has to read the project as the publish will: only a Release build
            # compiles ahead of time, which is what needs the workload, and 'workload restore' takes no
            # configuration option, hence the environment variable.
            subprocess.run([dotnet, "workload", "restore", project], cwd=repo,
                           env={**env, "Configuration": args.configuration}, check=True)
        command = [dotnet, "publish", project, "-c", args.configuration, "-o", str(output)]
        if args.no_aot:
            command.append("-p:RunAOTCompilation=false")
        subprocess.run(command, cwd=repo, env=env, check=True)
    package(output)


if __name__ == "__main__":
    main()
