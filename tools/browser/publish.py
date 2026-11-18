#!/usr/bin/env python3
"""Publish a complete static PWA, including a content-versioned offline asset manifest."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import zipfile


# The two .NET runtimes a deployment can carry, by the directory each is in. Only one is used in any one browser
# (wwwroot/threads.js decides), so the offline manifest lists them apart and the service worker keeps one.
RUNTIMES = {"single": "_framework", "threads": "_framework-threads"}
# The desktop's HUD art (src/Tunnerer.Desktop/resources/hud) that the browser's strip draws.
HUD_ART = ["heat_icon.png", "shield_icon.png", "panel_frame.png", "build_panel.png"]
ISOLATION_HEADERS = ["Cross-Origin-Opener-Policy: same-origin", "Cross-Origin-Embedder-Policy: require-corp"]


def rewrite(root, name, old, new):
    """Replaces the one `old` in a published file; `old` may be a compiled regular expression."""
    text = (root / name).read_text(encoding="utf-8")
    if isinstance(old, re.Pattern):
        text, count = old.subn(new, text)
    else:
        count = text.count(old)
        text = text.replace(old, new)
    if count != 1:
        raise RuntimeError(f"Expected one {getattr(old, 'pattern', old)!r} in {name}")
    (root / name).write_bytes(text.encode())
    # The publish left compressed copies of the file as it was, which some hosts serve in its place.
    for suffix in (".br", ".gz"):
        (root / (name + suffix)).unlink(missing_ok=True)


def write_runtime_bytes(root):
    """Tells threads.js how many bytes each runtime downloads, for the loading bar: the files .NET fetches itself
    (its .wasm files), not the .js modules the browser imports."""
    sizes = {}
    for name, directory in RUNTIMES.items():
        folder = root / directory
        if folder.is_dir():
            sizes[name] = sum(p.stat().st_size for p in folder.iterdir()
                              if p.is_file() and p.suffix not in {".js", ".br", ".gz", ".pdb", ".map", ".json"})
    rewrite(root, "threads.js", re.compile(r"const runtimeBytes = \{[^}]*\};"),
            "const runtimeBytes = " + json.dumps(sizes) + ";")


def megabytes(paths):
    return sum(p.stat().st_size for p in paths) / 1024 / 1024


def package(output):
    root = output / "wwwroot"
    if not (root / "_framework" / "dotnet.js").is_file():
        raise RuntimeError(f"Missing .NET browser runtime in {root}")
    write_runtime_bytes(root)
    assets = sorted(p for p in root.rglob("*") if p.is_file()
                    and p.suffix not in {".br", ".gz", ".pdb", ".map"}
                    and p.name not in {"assets-manifest.js", "sw.js", "_headers"})
    digest = hashlib.sha256()
    for path in assets:
        digest.update(path.relative_to(root).as_posix().encode())
        digest.update(hashlib.sha256(path.read_bytes()).digest())
    # Worker behavior is also part of the version, even though sw.js must never serve itself from its cache.
    digest.update((root / "sw.js").read_bytes())
    def urls(paths):
        return ["./" + p.relative_to(root).as_posix() for p in paths]

    manifest = {"version": digest.hexdigest()[:24], "urls": urls(assets)}
    sizes = f"{megabytes(assets):.1f} MiB"
    if (root / RUNTIMES["threads"]).is_dir():
        groups = {name: [p for p in assets if p.relative_to(root).parts[0] == directory]
                  for name, directory in RUNTIMES.items()}
        shared = [p for p in assets if not any(p in group for group in groups.values())]
        manifest["urls"] = urls(shared)
        manifest["runtimes"] = {name: urls(group) for name, group in groups.items()}
        sizes = (f"{megabytes(shared):.1f} MiB, and one of two runtimes: single-thread "
                 f"{megabytes(groups['single']):.1f} MiB, threaded {megabytes(groups['threads']):.1f} MiB")
    # The same bytes on every OS: no locale encoding, no translated line ends.
    (root / "assets-manifest.js").write_bytes(("self.tunnererAssets = " + json.dumps(manifest) + ";\n").encode())
    archive = output / "tunnerer-browser.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as bundle:
        for path in sorted(root.rglob("*")):
            if path.is_file():
                bundle.write(path, path.relative_to(root))
    print(f"Published {len(assets)} offline assets ({sizes}), version {manifest['version']}")
    print(f"Serve or deploy: {root}\nStatic hosting bundle: {archive}")
    return manifest


def add_threaded_runtime(publish, output, repo, env):
    """Publishes the project again with threads and adds that runtime to the deployment beside the first."""
    root = output / "wwwroot"
    threaded = output / "threads"
    if threaded.is_dir():
        shutil.rmtree(threaded)
    subprocess.run(publish + ["-p:WasmEnableThreads=true", "-o", str(threaded)], cwd=repo, env=env, check=True)
    # Everything outside _framework is the same in both publishes.
    shutil.move(threaded / "wwwroot" / "_framework", root / RUNTIMES["threads"])
    shutil.rmtree(threaded)

    rewrite(root, "threads.js", "const published = false;", "const published = true;")
    # The threaded runtime is used only on a cross-origin isolated page.
    rewrite(root, "_headers", "/*\n", "/*\n" + "".join(f"  {header}\n" for header in ISOLATION_HEADERS))


def main():
    repo = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=repo / "artifacts" / "browser")
    parser.add_argument("--configuration", choices=["Debug", "Release"], default="Release")
    parser.add_argument("--no-aot", action="store_true", help="Faster local builds; slower game execution.")
    parser.add_argument("--threads", action="store_true",
                        help="Also publish the threaded runtime, for hosts that send the cross-origin isolation "
                             "headers. Needs the wasm-experimental workload.")
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
        # The HUD's font is the desktop's, copied like the shaders rather than kept twice in the repository.
        font_dir = repo / "src/Tunnerer.Browser/wwwroot/fonts"
        font_dir.mkdir(exist_ok=True)
        for name in ("ChakraPetch-Medium.ttf", "OFL-chakrapetch.txt"):
            shutil.copy2(repo / "src/Tunnerer.Desktop/resources/fonts" / name, font_dir / name)
        # So is the desktop's HUD art, which the wide layout's strip draws (wwwroot/strip.js).
        hud_dir = repo / "src/Tunnerer.Browser/wwwroot/hud"
        hud_dir.mkdir(exist_ok=True)
        for name in HUD_ART:
            shutil.copy2(repo / "src/Tunnerer.Desktop/resources/hud" / name, hud_dir / name)
        project = str(repo / "src/Tunnerer.Browser/Tunnerer.Browser.csproj")
        if args.restore_workloads:
            # Which workload that is depends on the SDK in use (wasm-tools on .NET 10, wasm-tools-net10 on later
            # ones), so let the SDK say. It has to read the project as the publish will: only a Release build
            # compiles ahead of time, which is what needs the workload, and 'workload restore' takes no
            # configuration option, hence the environment variables. The threaded runtime is a workload of its own.
            for properties in [{}] + ([{"WasmEnableThreads": "true"}] if args.threads else []):
                subprocess.run([dotnet, "workload", "restore", project], cwd=repo,
                               env={**env, "Configuration": args.configuration, **properties}, check=True)
        command = [dotnet, "publish", project, "-c", args.configuration]
        if args.no_aot:
            command.append("-p:RunAOTCompilation=false")
        subprocess.run(command + ["-o", str(output)], cwd=repo, env=env, check=True)
        if args.threads:
            add_threaded_runtime(command, output, repo, env)
    package(output)


if __name__ == "__main__":
    main()
