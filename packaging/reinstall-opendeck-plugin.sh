#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_PATH="${1:-$SCRIPT_DIR/../output/io.github.brendangrant.opendeck.loupedeck.sdPlugin}"
PLUGINS_ROOT="${OPENDECK_PLUGINS_ROOT:-$HOME/.config/opendeck/plugins}"
PLUGIN_ID="${OPENDECK_PLUGIN_ID:-io.github.brendangrant.opendeck.loupedeck.sdPlugin}"
INSTALLED_PLUGIN_DIR="$PLUGINS_ROOT/$PLUGIN_ID"
RESTART_OPENDECK="${RESTART_OPENDECK:-1}"

if [[ ! -f "$PACKAGE_PATH" ]]; then
    echo "Plugin package not found: $PACKAGE_PATH" >&2
    exit 1
fi

mkdir -p "$PLUGINS_ROOT"

if [[ "$RESTART_OPENDECK" == "1" ]]; then
    pkill -x opendeck 2>/dev/null || true
    pkill -x opendeck-loupedeck 2>/dev/null || true
fi

rm -rf "$INSTALLED_PLUGIN_DIR"
unzip -o "$PACKAGE_PATH" -d "$PLUGINS_ROOT" >/dev/null

echo "Installed plugin:"
echo "  $INSTALLED_PLUGIN_DIR"

if [[ "$RESTART_OPENDECK" == "1" ]]; then
    nohup opendeck >/tmp/opendeck.log 2>&1 &
    echo "Restarted OpenDeck:"
    echo "  opendeck"
fi
