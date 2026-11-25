#!/usr/bin/env bash
# Builds the sandbox image a pool starts for OpenCode templates: cowork-sandbox-opencode:2.4.1, with rootless podman.
#
#   deploy/sandbox/build.sh                 # tag cowork-sandbox-opencode:2.4.1
#
# OpenCode 2.4.1 isn't published as a package (npm stops at 1.x), so the build copies a binary that is already on this machine, read only, and
# refuses any other build of it: OPENCODE_BIN (default: Weave Fleet's install at ~/.weave/harnesses/opencode2/.opencode/bin/opencode) must say
# "opencode v2.4.1" and match the pinned SHA-256. Nothing else of ~/.weave is read. The runner is published self-contained for linux-x64.
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
TAG="${TAG:-cowork-sandbox-opencode:2.4.1}"
OPENCODE_BIN="${OPENCODE_BIN:-$HOME/.weave/harnesses/opencode2/.opencode/bin/opencode}"
OPENCODE_SHA256="${OPENCODE_SHA256:-ec93043f18f8a6b4d8e71ec9260e292ba6f91b66b142782135d40f0b58b0c152}"
CTX="${CTX:-$HOME/.cache/cowork-sandbox-build}"

[ -x "$OPENCODE_BIN" ] || { echo "No OpenCode binary at $OPENCODE_BIN (set OPENCODE_BIN)." >&2; exit 1; }
version="$("$OPENCODE_BIN" --version 2>/dev/null || true)"
[ "$version" = "opencode v2.4.1" ] || { echo "$OPENCODE_BIN is '$version', not opencode v2.4.1." >&2; exit 1; }
sum="$(sha256sum "$OPENCODE_BIN" | cut -c1-64)"
[ "$sum" = "$OPENCODE_SHA256" ] || { echo "$OPENCODE_BIN has SHA-256 $sum, not the pinned $OPENCODE_SHA256." >&2; exit 1; }

rm -rf "$CTX"; mkdir -p "$CTX/runner"
# a clean Release build every time: an incremental publish once put a stale runner in the image
rm -rf "$REPO/src/CoWork.Runner/obj/Release" "$REPO/src/CoWork.Runner/bin/Release" "$REPO/src/CoWork.Contracts/obj/Release" "$REPO/src/CoWork.Contracts/bin/Release"
cp "$OPENCODE_BIN" "$CTX/opencode"                         # a copy into the build context; the original is only read
cp "$REPO/deploy/sandbox/Containerfile" "$CTX/Containerfile"
dotnet publish "$REPO/src/CoWork.Runner/CoWork.Runner.csproj" -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=none -o "$CTX/runner" --nologo -v quiet
podman build --pull=missing -t "$TAG" --label cowork.image=sandbox-opencode -f "$CTX/Containerfile" "$CTX"
rm -rf "$CTX"
# an earlier build of this image is left untagged: remove those (only images carrying this label)
podman images -q --filter dangling=true --filter label=cowork.image=sandbox-opencode | xargs -r podman rmi >/dev/null 2>&1 || true
echo "Built $TAG"
