"""Build-time only: dynamic int8 MatMul weights. No Python is shipped in the APK."""
from pathlib import Path
import json
import hashlib
import sys
from onnxruntime.quantization import quantize_dynamic, QuantType

source, output = map(Path, sys.argv[1:3])
original = json.loads((source / "manifest.json").read_text(encoding="utf-8-sig"))
target = output / "xlmr"
for kind in ("visual", "textual"):
    dest = target / kind / "model.onnx"
    dest.parent.mkdir(parents=True, exist_ok=True)
    if not dest.exists():
        quantize_dynamic(str(source / "xlmr" / kind / "model.onnx"), str(dest),
                         weight_type=QuantType.QInt8, per_channel=True,
                         op_types_to_quantize=["MatMul", "Gemm", "Gather"],
                         extra_options={"MatMulConstBOnly": True})
files = []
for relative in ("visual/model.onnx", "textual/model.onnx", "textual/sentencepiece.model"):
    path = target / relative
    files.append({"path": relative, "size": path.stat().st_size,
                  "sha256": hashlib.file_digest(path.open("rb"), "sha256").hexdigest(),
                  "url": ""})
original.update(id=original["id"] + "-android-int8",
                revision=original["revision"] + "-ort1.23.2-dynamic-qint8-v2",
                preprocessingVersion="android-bicubic-224-center-rgb-unigram-v1",
                files=files)
(output / "manifest.json").write_text(json.dumps(original, indent=2), encoding="utf-8")
print("Android model assets:", sum(f["size"] for f in files), "bytes")
