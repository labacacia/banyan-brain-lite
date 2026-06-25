#!/usr/bin/env bash
# Build Banyan Lite Windows installer with NSIS.
# Runs on Linux (cross-compile) or Windows (with NSIS installed).
#
# Usage:
#   ./build-nsis.sh [VERSION]
#
# Prerequisites (Debian/Ubuntu): sudo apt install nsis

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PUBLISH_DIR="$SCRIPT_DIR/../../publish/win-x64"

if ! command -v makensis &>/dev/null; then
    echo "ERROR: makensis not found. Install with: sudo apt install nsis"
    exit 1
fi

if [[ ! -f "$PUBLISH_DIR/banyan.exe" ]]; then
    echo "ERROR: $PUBLISH_DIR/banyan.exe not found."
    echo "Run the publish step first:"
    echo "  dotnet publish src/Banyan.Cli/Banyan.Cli.csproj -c Release -r win-x64 --self-contained true \\"
    echo "      -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/win-x64"
    exit 1
fi

VERSION="${1:-1.1.0}"

echo "Building Banyan Lite $VERSION installer..."
makensis \
    -DVERSION="$VERSION" \
    -DPUBLISH_DIR="$(realpath "$PUBLISH_DIR")" \
    "$SCRIPT_DIR/banyan.nsi"

echo "Done: $SCRIPT_DIR/banyan-lite-${VERSION}-setup.exe"
