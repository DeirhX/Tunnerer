#!/usr/bin/env python3
"""Add verified desktop release downloads to a published Starfish browser bundle."""
import gzip
import hashlib
from pathlib import Path
import shutil
import sys
import tarfile
import zipfile

import publish


def main():
    output = Path(sys.argv[1]).resolve()
    release = Path(sys.argv[2]).resolve()
    root = output / "wwwroot"
    checksums = dict(line.split(maxsplit=1)[::-1] for line in
                     (release / "SHA256SUMS.txt").read_text().splitlines())
    builds = [
        ("emberward-windows-x64.zip", "Emberward.Desktop.exe", "emberward-windows-x64.exe"),
        ("emberward-macos-arm64.zip", "Emberward.Desktop", "emberward-macos-arm64"),
        ("emberward-linux-x64.tar.gz", "./Emberward.Desktop", "emberward-linux-x64"),
    ]
    downloads = output / "desktop-downloads"
    downloads.mkdir(exist_ok=True)
    for archive, member, name in builds:
        path = release / archive
        if hashlib.sha256(path.read_bytes()).hexdigest() != checksums.get(archive):
            raise ValueError(f"Release checksum mismatch: {archive}")
        if archive.endswith(".zip"):
            with zipfile.ZipFile(path) as bundle, bundle.open(member) as source, (downloads / name).open("wb") as dest:
                shutil.copyfileobj(source, dest)
        else:
            with tarfile.open(path, "r:gz") as bundle, bundle.extractfile(member) as source, (downloads / name).open("wb") as dest:
                shutil.copyfileobj(source, dest)
        with (downloads / name).open("rb") as source, gzip.open(downloads / (name + ".gz"), "wb", compresslevel=1) as dest:
            shutil.copyfileobj(source, dest)
        old = f'https://github.com/DeirhX/Emberward/releases/latest/download/{archive}'
        publish.rewrite(root, "index.html", f'href="{old}"', f'href="./downloads/{name}" download')
    # Downloads are intentionally outside the offline manifest: a browser should not cache all three executables.
    publish.package(output)
    with zipfile.ZipFile(output / "emberward-browser.zip", "a", compression=zipfile.ZIP_STORED) as bundle:
        for path in sorted(downloads.iterdir()):
            bundle.write(path, "downloads/" + path.name)


if __name__ == "__main__":
    main()
