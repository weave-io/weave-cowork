#!/usr/bin/env bash
# Puts OpenCode 2.4.1 for linux-x64 at the path given, downloaded from npm (@opencode/cli-linux-x64, the package OpenCode's
# own v2 installer uses) and checked against the pinned SHA-256. The binary is never run here.
#
#   deploy/sandbox/fetch-opencode.sh /path/to/opencode
#
# OPENCODE_BIN: use this binary instead of downloading (it must match the same SHA-256).
set -euo pipefail
DEST="${1:?Usage: fetch-opencode.sh <destination path>}"
VERSION="${OPENCODE_VERSION:-2.4.1}"
OPENCODE_SHA256="${OPENCODE_SHA256:-ec93043f18f8a6b4d8e71ec9260e292ba6f91b66b142782135d40f0b58b0c152}"

check() { [ "$(sha256sum "$1" | cut -c1-64)" = "$OPENCODE_SHA256" ]; }
mkdir -p "$(dirname "$DEST")"
if [ -n "${OPENCODE_BIN:-}" ]; then
  check "$OPENCODE_BIN" || { echo "$OPENCODE_BIN doesn't match the pinned SHA-256 $OPENCODE_SHA256." >&2; exit 1; }
  cp "$OPENCODE_BIN" "$DEST"; chmod 0755 "$DEST"; exit 0
fi
if [ -f "$DEST" ] && check "$DEST"; then exit 0; fi

tmp="$(mktemp -d "${TMPDIR:-/tmp}/opencode-XXXXXX")"
trap 'rm -rf "$tmp"' EXIT
curl -fsSL --retry 3 -o "$tmp/pkg.tgz" "https://registry.npmjs.org/@opencode/cli-linux-x64/-/cli-linux-x64-${VERSION}.tgz"
tar -xzf "$tmp/pkg.tgz" -C "$tmp" package/bin/opencode
check "$tmp/package/bin/opencode" || { echo "The downloaded OpenCode ${VERSION} doesn't match the pinned SHA-256 $OPENCODE_SHA256." >&2; exit 1; }
install -m 0755 "$tmp/package/bin/opencode" "$DEST"
