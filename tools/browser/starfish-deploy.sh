#!/bin/sh
# Puts a published browser game (the emberward-browser.zip publish.py makes, read from standard input) in place on
# Starfish, keeping the build it replaces in browser.prev. Installed on the VM as /usr/local/bin/tunnerer-deploy-browser
# and run as root: by hand, and by the publish-starfish workflow, whose key may run nothing else (docs/browser.md).
set -eu
site=/srv/tunnerer/browser
work=$(mktemp -d /tmp/emberward-deploy.XXXXXX)
trap 'rm -rf "$work"' EXIT
cat > "$work/game.zip"
mkdir "$work/new"
python3 -m zipfile -e "$work/game.zip" "$work/new"
# A wrong or truncated upload stops here, before the running build is touched.
test -f "$work/new/index.html"
test -f "$work/new/assets-manifest.js"
test -d "$work/new/_framework-threads"
for file in emberward-windows-x64.exe emberward-macos-arm64 emberward-linux-x64; do
    test -s "$work/new/downloads/$file"
    test -s "$work/new/downloads/$file.gz"
done
chown -R root:root "$work/new"
find "$work/new" -type d -exec chmod 755 {} +
find "$work/new" -type f -exec chmod 644 {} +
rm -rf "$site.prev"
if [ -d "$site" ]; then mv "$site" "$site.prev"; fi
mv "$work/new" "$site"
sed -n 's/.*"version": "\([0-9a-f]*\)".*/deployed \1/p' "$site/assets-manifest.js"
