#!/usr/bin/env bash
# Builds cowork-runner as one self-contained file per platform into publish/runner/<rid>/.
# Usage: scripts/publish-runner.sh [rid ...]      (default: linux-x64 osx-arm64 win-x64)
set -euo pipefail
cd "$(dirname "$0")/.."
rids=("$@"); [ ${#rids[@]} -eq 0 ] && rids=(linux-x64 osx-arm64 win-x64)
for rid in "${rids[@]}"; do
  echo "== $rid"
  dotnet publish src/CoWork.Runner -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "publish/runner/$rid"
done
ls -lh publish/runner/*/cowork-runner* 2>/dev/null || true
