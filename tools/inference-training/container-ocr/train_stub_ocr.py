#!/usr/bin/env python3
"""
v2 inference scaffold bring-up — stub container-OCR exporter
============================================================

Purpose
-------
Produce a *valid* ONNX artifact for the v2 ``container-ocr`` family that
matches the I/O contract in §6.1.2 of ``docs/IMAGE-ANALYSIS-MODERNIZATION.md``,
so the just-landed ``NickERP.Inspection.Inference.OCR.ContainerNumber`` plugin
can be exercised end-to-end on real hardware without a full Florence-2
fine-tune. The model itself is a small random-init CNN that emits a
``[T, V]`` logits tensor where ``V == 36`` (the ISO 6346 alphabet); it has
no learned ability to read plates and is **not for production**.

Sibling pattern of ``tools/inference-bringup/train_stub_split.py``: same
seeding convention, same metadata schema, same exit codes.

I/O contract (§6.1.2 / §6.1.3)
------------------------------
- Input:  ``(N, 3, 384, 384)`` ``float32`` — ``pixel_values`` (dynamic batch).
- Output: ``(N, T, V)`` ``float32`` — ``logits`` where ``T = 16`` (token
  budget) and ``V = 36`` (ISO 6346 alphabet ``A–Z`` + ``0–9``). The real
  Florence-2 export will project a much wider BART vocabulary down to
  these 36 columns inside the ONNX graph; here we just emit the projected
  shape directly.

Usage
-----
::

    "C:/Shared/ERP V2/tools/inference-bringup/.venv/Scripts/python.exe" \\
        "C:/Shared/ERP V2/tools/inference-training/container-ocr/train_stub_ocr.py"

    python train_stub_ocr.py --storage-root "C:/Shared/ERP V2/storage"

Exit codes
----------
    0   success
    2   bad CLI args
    3   ONNX export failed
    4   onnx.checker rejected the exported graph

Limitations
-----------
- Random-init: every output for every input is ~uniform softmax garbage.
  The constrained beam decoder will still produce *some* 11-character
  string but it will be effectively random. The smoke test treats this
  as a proof of *contract*, not accuracy.
- The model does not implement encoder-decoder cross-attention. A real
  Florence-2 export would. The smoke-level shape contract is unaffected.
- Vocabulary order MUST match the C# constant
  ``Iso6346.AllowedAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"``.
  This script writes the alphabet into ``model.metadata.json`` so a
  divergence is caught by inspection.
"""

from __future__ import annotations

import argparse
import datetime as _dt
import hashlib
import json
import os
import sys
from pathlib import Path

import torch
import torch.nn as nn
import torch.nn.functional as F

# --- §6.1.2 / §6.1.3 constants ---------------------------------------------

INPUT_HEIGHT = 384
INPUT_WIDTH = 384
INPUT_CHANNELS = 3
TOKEN_BUDGET = 16
VOCAB_SIZE = 36
ONNX_OPSET = 18

INPUT_NAME = "pixel_values"
OUTPUT_NAME = "logits"

MODEL_ID = "container-ocr-v1"
MODEL_VERSION = "v1.0.0-stub"

# Must mirror Iso6346.AllowedAlphabet on the C# side.
ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"

EXIT_OK = 0
EXIT_BAD_ARGS = 2
EXIT_EXPORT_FAILED = 3
EXIT_CHECKER_FAILED = 4


# --- model -----------------------------------------------------------------


class ContainerOcrStubModel(nn.Module):
    """Tiny conv-encoder + linear-decoder stub.

    The encoder is a 4-block strided CNN that down-samples 384 → 24 spatial
    while widening 3 → 128 channels. A global-avg-pool yields a 128-vector
    summary; a per-position linear head expands that into the
    ``[N, T, V]`` logits tensor. No attention, no language model — this is
    a contract carrier.

    The intent is to keep the artifact tiny (≤ 5 MB) so it can ship inside
    a unit-test fixture if needed without a Git LFS dependency.
    """

    def __init__(self) -> None:
        super().__init__()
        self.encoder = nn.Sequential(
            nn.Conv2d(INPUT_CHANNELS, 16, kernel_size=3, stride=2, padding=1, bias=False),
            nn.BatchNorm2d(16),
            nn.ReLU(inplace=True),
            nn.Conv2d(16, 32, kernel_size=3, stride=2, padding=1, bias=False),
            nn.BatchNorm2d(32),
            nn.ReLU(inplace=True),
            nn.Conv2d(32, 64, kernel_size=3, stride=2, padding=1, bias=False),
            nn.BatchNorm2d(64),
            nn.ReLU(inplace=True),
            nn.Conv2d(64, 128, kernel_size=3, stride=2, padding=1, bias=False),
            nn.BatchNorm2d(128),
            nn.ReLU(inplace=True),
        )
        # One linear head per position keeps the export simple. T x (128 -> V).
        self.heads = nn.ModuleList(
            [nn.Linear(128, VOCAB_SIZE) for _ in range(TOKEN_BUDGET)]
        )

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        feat = self.encoder(x)                    # (N, 128, 24, 24)
        pooled = F.adaptive_avg_pool2d(feat, 1)   # (N, 128, 1, 1)
        pooled = pooled.flatten(1)                # (N, 128)
        # Stack T heads → (N, T, V)
        outputs = [head(pooled) for head in self.heads]
        logits = torch.stack(outputs, dim=1)
        return logits


# --- export ----------------------------------------------------------------


def _sha256_of(path: Path) -> str:
    sha = hashlib.sha256()
    with path.open("rb") as fp:
        for chunk in iter(lambda: fp.read(1 << 20), b""):
            sha.update(chunk)
    return sha.hexdigest()


def _export(model: nn.Module, out_path: Path) -> None:
    out_path.parent.mkdir(parents=True, exist_ok=True)
    dummy = torch.zeros(
        (1, INPUT_CHANNELS, INPUT_HEIGHT, INPUT_WIDTH), dtype=torch.float32
    )

    model.eval()
    with torch.no_grad():
        out = model(dummy)
    expected_shape = (1, TOKEN_BUDGET, VOCAB_SIZE)
    if tuple(out.shape) != expected_shape:
        raise RuntimeError(
            f"Forward pass output shape {tuple(out.shape)} != expected {expected_shape}; "
            "check ContainerOcrStubModel before exporting."
        )

    torch.onnx.export(
        model,
        dummy,
        str(out_path),
        input_names=[INPUT_NAME],
        output_names=[OUTPUT_NAME],
        dynamic_axes={
            INPUT_NAME: {0: "batch"},
            OUTPUT_NAME: {0: "batch"},
        },
        opset_version=ONNX_OPSET,
        do_constant_folding=True,
    )


def _write_metadata(metadata_path: Path, sha256: str) -> None:
    payload = {
        "model_id": MODEL_ID,
        "version": MODEL_VERSION,
        "sha256": sha256,
        "onnx_opset": str(ONNX_OPSET),
        "exported_at": _dt.datetime.now(_dt.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
        "exported_by": "stub-trainer-bringup-ocr",
        "git_commit": None,
        "training_set_hash": None,
        "eval_metrics": None,
        "base_model": "tiny-cnn-stub",
        "alphabet": ALPHABET,
        "token_budget": TOKEN_BUDGET,
        "input_shape": [INPUT_CHANNELS, INPUT_HEIGHT, INPUT_WIDTH],
        "notes": (
            "Random-init stub for v2 container-OCR scaffold bring-up. Not for "
            "production. See §6.1 of docs/IMAGE-ANALYSIS-MODERNIZATION.md. "
            "Output is a [T=16, V=36] logits tensor; the ConstrainedBeamDecoder "
            "will produce ~random 11-char outputs against this artifact."
        ),
    }
    metadata_path.parent.mkdir(parents=True, exist_ok=True)
    with metadata_path.open("w", encoding="utf-8") as fp:
        json.dump(payload, fp, indent=2)
        fp.write("\n")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument(
        "--storage-root",
        default=os.environ.get("ERP_V2_STORAGE_ROOT", r"C:\Shared\ERP V2\storage"),
        help=(
            "storage root; the artifact is written under "
            "<root>/models/container-ocr/v1/."
        ),
    )
    args = parser.parse_args(argv)

    storage_root = Path(args.storage_root)
    model_dir = storage_root / "models" / "container-ocr" / "v1"
    onnx_path = model_dir / "model.onnx"
    metadata_path = model_dir / "model.metadata.json"

    print(f"[stub-ocr] storage root: {storage_root}")
    print(f"[stub-ocr] target onnx:  {onnx_path}")

    torch.manual_seed(0)
    model = ContainerOcrStubModel()
    n_params = sum(p.numel() for p in model.parameters())
    print(f"[stub-ocr] model parameters: {n_params:,}")

    print(f"[stub-ocr] exporting ONNX (opset {ONNX_OPSET}, dynamic batch) ...")
    try:
        _export(model, onnx_path)
    except Exception as e:
        sys.stderr.write(f"FATAL: ONNX export failed: {e}\n")
        return EXIT_EXPORT_FAILED

    sha = _sha256_of(onnx_path)
    print(f"[stub-ocr] onnx sha256: {sha}")

    _write_metadata(metadata_path, sha)
    print(f"[stub-ocr] metadata written: {metadata_path}")

    try:
        import onnx

        onnx_model = onnx.load(str(onnx_path))
        onnx.checker.check_model(onnx_model)
    except Exception as e:
        sys.stderr.write(f"FATAL: onnx.checker rejected the exported graph: {e}\n")
        return EXIT_CHECKER_FAILED

    print(
        "[stub-ocr] onnx.checker.check_model OK — "
        f"ir_version={onnx_model.ir_version}, opset={onnx_model.opset_import[0].version}"
    )
    print(f"[stub-ocr] SUCCESS sha256={sha}")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
