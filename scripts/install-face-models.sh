#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
destination="${1:-$repo/artifacts/models/faces}"
mkdir -p "$destination"
base="https://media.githubusercontent.com/media/opencv/opencv_zoo/48b10fa505aeb51fbd127fc60a5c660040e823b7/models"
while read -r folder file sha; do
  target="$destination/$file"
  if [[ -f "$target" ]] && [[ "$(shasum -a 256 "$target" | awk '{print $1}')" == "$sha" ]]; then continue; fi
  curl -fL --retry 3 "$base/$folder/$file" -o "$target.partial"
  [[ "$(shasum -a 256 "$target.partial" | awk '{print $1}')" == "$sha" ]] || { echo 'Invalid face model SHA-256' >&2; exit 1; }
  mv "$target.partial" "$target"
done <<'MODELS'
face_detection_yunet face_detection_yunet_2023mar.onnx 8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4
face_recognition_sface face_recognition_sface_2021dec.onnx 0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79
MODELS
