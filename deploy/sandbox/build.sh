#!/usr/bin/env bash
# Builds the sandbox image a pool starts for OpenCode templates: cowork-sandbox-opencode:2.4.1, with rootless podman.
#
#   deploy/sandbox/build.sh                 # tag cowork-sandbox-opencode:2.4.1
#
# OpenCode 2.4.1 comes from npm (@opencode/cli-linux-x64, the package OpenCode's own v2 installer uses) through fetch-opencode.sh,
# which checks it against the pinned SHA-256; OPENCODE_BIN uses a binary already on this machine instead (it must match too).
# The runner is published self-contained for linux-x64.
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
TAG="${TAG:-cowork-sandbox-opencode:2.4.1}"
CTX="${CTX:-$HOME/.cache/cowork-sandbox-build}"

rm -rf "$CTX"; mkdir -p "$CTX/runner"
# a clean Release build every time: an incremental publish once put a stale runner in the image
rm -rf "$REPO/src/CoWork.Runner/obj/Release" "$REPO/src/CoWork.Runner/bin/Release" "$REPO/src/CoWork.Contracts/obj/Release" "$REPO/src/CoWork.Contracts/bin/Release"
"$REPO/deploy/sandbox/fetch-opencode.sh" "$CTX/opencode"   # downloaded and checked, or OPENCODE_BIN copied and checked
cp "$REPO/deploy/sandbox/Containerfile" "$CTX/Containerfile"
dotnet publish "$REPO/src/CoWork.Runner/CoWork.Runner.csproj" -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=none -o "$CTX/runner" --nologo -v quiet
podman build --pull=missing -t "$TAG" --label cowork.image=sandbox-opencode -f "$CTX/Containerfile" "$CTX"
rm -rf "$CTX"
# an earlier build of this image is left untagged: remove those (only images carrying this label)
podman images -q --filter dangling=true --filter label=cowork.image=sandbox-opencode | xargs -r podman rmi >/dev/null 2>&1 || true
echo "Built $TAG"
