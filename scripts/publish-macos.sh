#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
rid=osx-arm64
if [[ $# -gt 0 ]]; then rid="$1"; shift; fi
[[ "$rid" == osx-arm64 || "$rid" == osx-x64 ]] || exit 1
exec pwsh -NoProfile -File "$repo/scripts/publish-unix.ps1" -Rid "$rid" "$@"
