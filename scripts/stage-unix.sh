#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
if [[ $# -lt 1 ]]; then echo 'RID required: linux-x64, osx-arm64 or osx-x64' >&2; exit 1; fi
exec pwsh -NoProfile -File "$repo/scripts/bootstrap-unix.ps1" -Rid "$1"
