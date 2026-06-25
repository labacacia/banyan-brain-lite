#!/usr/bin/env bash
# Build a Debian package for Banyan Brain Lite from the linux-x64 publish output.
#
# Usage:
#   scripts/build-deb.sh [VERSION]
#
# Prerequisites:
#   dotnet publish src/Banyan.Cli/Banyan.Cli.csproj -c Release -r linux-x64 --self-contained true \
#       -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/linux-x64

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${1:-1.1.0}"
PUBLISH_DIR="$ROOT/publish/linux-x64"
ARTIFACT_DIR="$ROOT/artifacts/installers"
PKG_ROOT="$ROOT/artifacts/deb/banyan-lite_${VERSION}_amd64"

if [[ ! -f "$PUBLISH_DIR/banyan" ]]; then
    echo "ERROR: $PUBLISH_DIR/banyan not found."
    echo "Run the publish step first:"
    echo "  dotnet publish src/Banyan.Cli/Banyan.Cli.csproj -c Release -r linux-x64 --self-contained true \\"
    echo "      -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/linux-x64"
    exit 1
fi

rm -rf "$PKG_ROOT"
mkdir -p \
    "$PKG_ROOT/DEBIAN" \
    "$PKG_ROOT/usr/lib/banyan-lite" \
    "$PKG_ROOT/usr/bin" \
    "$PKG_ROOT/usr/share/doc/banyan-lite"

cp -a "$PUBLISH_DIR"/. "$PKG_ROOT/usr/lib/banyan-lite/"
chmod 0755 "$PKG_ROOT/usr/lib/banyan-lite/banyan"
ln -s ../lib/banyan-lite/banyan "$PKG_ROOT/usr/bin/banyan"
cp "$ROOT/README.md" "$ROOT/LICENSE" "$ROOT/NOTICE" "$ROOT/CHANGELOG.md" "$PKG_ROOT/usr/share/doc/banyan-lite/"

cat > "$PKG_ROOT/DEBIAN/control" <<CONTROL
Package: banyan-lite
Version: ${VERSION}
Section: utils
Priority: optional
Architecture: amd64
Maintainer: INNO LOTUS PTY LTD <support@innolotus.com>
Description: Banyan Brain Lite offline-first memory node for AI agents
 Banyan Brain Lite is a local SQLite-backed memory node with CLI, Web UI,
 MCP integration, NID authentication, and knowledge-pack support.
CONTROL

cat > "$PKG_ROOT/DEBIAN/postinst" <<'POSTINST'
#!/bin/sh
set -e
chmod 0755 /usr/lib/banyan-lite/banyan
exit 0
POSTINST
chmod 0755 "$PKG_ROOT/DEBIAN/postinst"

mkdir -p "$ARTIFACT_DIR"
fakeroot dpkg-deb --build "$PKG_ROOT" "$ARTIFACT_DIR/banyan-lite_${VERSION}_amd64.deb"
echo "Done: $ARTIFACT_DIR/banyan-lite_${VERSION}_amd64.deb"
