#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
exec pwsh -NoProfile -File "$repo/scripts/publish-unix.ps1" -Rid linux-x64 "$@"
