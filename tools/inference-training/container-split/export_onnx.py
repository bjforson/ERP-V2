#!/usr/bin/env python3
"""
v2 container-split — Step 4: export student.pt → ONNX
=====================================================

Purpose
-------
Convert a trained ``student.pt`` checkpoint into the production ONNX
artifact + ``model.metadata.json`` pair required by the §3.7 deployment
contract and consumed by the ``IInferenceRunner`` plugin chain (§4).
Mirrors the bring-up stub exporter at
``tools/inference-bringup/train_stub_split.py`` so the InferenceSmokeTest
console app can target the v2 artifact unchanged.

I/O contract (§3.2)
-------------------
- Input  ``input``   : ``(N, 1, 472, 1568)`` float32 (dynamic batch).
- Output ``heatmap`` : ``(N, 1568)``       float32 (dynamic batch).
- ONNX opset 18.

Output paths (§3.7)
-------------------
::

    <storage_root>/models/container-split/v2/model.onnx
    <storage_root>/models/container-split/v2/model.metadata.json

The v1 stub is left in place at .../v1/ (it serves the existing smoke
test) — this script writes to v2/ by default.

Metadata (§3.7)
---------------
``model.metadata.json`` carries::

    model_id, version, sha256, onnx_opset,
    exported_at, exported_by, git_commit,
    training_set_hash, eval_metrics, base_model, notes

``training_set_hash`` is read from the run dir or
``data/training_set_hash.txt``; ``eval_metrics`` is read from
``eval_metrics.json`` next to the weights when present (produced by
``evaluate.py``). Both are optional and recorded as ``null`` if
missing — the C# runner will still load the model, but the artifact
will not be promotable past dev-eval.

Usage
-----
::

    python export_onnx.py \\
        --weights runs/<run_id>/student.pt \\
        --storage-root "C:/Shared/ERP V2/storage" \\
        --version v2.0.0-smoke \\
        --eval-metrics runs/<run_id>/eval_metrics.json \\
        --base-model resnet18-unet-v1
"""
from __future__ import annotations

import argparse
import datetime as _dt
import hashlib
import json
import logging
import os
import sys
from pathlib import Path
from typing import Optional

import torch

# Reuse the trainer's model definition so I/O semantics match exactly.
from train import ContainerSplitUNet, INPUT_HEIGHT, INPUT_WIDTH

INPUT_NAME = "input"
OUTPUT_NAME = "heatmap"
ONNX_OPSET = 18
MODEL_ID = "container-split"
DEFAULT_BASE_MODEL = "resnet18-unet-v1"

logger = logging.getLogger("export-onnx")


def _sha256_of(path: Path) -> str:
    sha = hashlib.sha256()
    with path.open("rb") as fp:
        for chunk in iter(lambda: fp.read(1 << 20), b""):
            sha.update(chunk)
    return sha.hexdigest()


def _maybe_load_json(path: Optional[Path]) -> Optional[dict]:
    if path is None or not path.exists():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        logger.warning("eval metrics file is not valid JSON: %s", path)
        return None


def _resolve_training_set_hash(weights_path: Path, data_root: Path) -> Optional[str]:
    """Look in three places, in order: the checkpoint dict, a sibling
    ``training_set_hash.txt``, then ``<data_root>/training_set_hash.txt``."""
    try:
        ckpt = torch.load(weights_path, map_location="cpu", weights_only=False)
        if isinstance(ckpt, dict) and ckpt.get("training_set_hash"):
            return str(ckpt["training_set_hash"])
    except Exception:
        pass
    sibling = weights_path.parent / "training_set_hash.txt"
    if sibling.exists():
        return sibling.read_text(encoding="utf-8").strip() or None
    fallback = data_root / "training_set_hash.txt"
    if fallback.exists():
        return fallback.read_text(encoding="utf-8").strip() or None
    return None


def _export(model: torch.nn.Module, out_path: Path) -> None:
    out_path.parent.mkdir(parents=True, exist_ok=True)
    dummy = torch.zeros(
        (1, 1, INPUT_HEIGHT, INPUT_WIDTH), dtype=torch.float32
    )
    model.eval()
    with torch.no_grad():
        out = model(dummy)
    expected_shape = (1, INPUT_WIDTH)
    if tuple(out.shape) != expected_shape:
        raise RuntimeError(
            f"Forward output shape {tuple(out.shape)} != expected {expected_shape}; "
            "abort before export."
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


def main(argv: list[str] | None = None) -> int:
    here = Path(__file__).resolve().parent
    ap = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument("--weights", required=True)
    ap.add_argument("--data-root", default=str(here / "data"))
    ap.add_argument(
        "--storage-root",
        default=os.environ.get("ERP_V2_STORAGE_ROOT", r"C:\Shared\ERP V2\storage"),
    )
    ap.add_argument("--version", default="v2.0.0-smoke")
    ap.add_argument("--eval-metrics", default=None,
                    help="path to eval_metrics.json (default: <weights_dir>/eval_metrics.json)")
    ap.add_argument("--base-model", default=DEFAULT_BASE_MODEL)
    ap.add_argument("--exported-by", default="train.py")
    ap.add_argument("--git-commit", default=None)
    ap.add_argument("--notes", default=(
        "v2 container-split student trained on v1 splitter labels. "
        "1-epoch smoke run with ~88 scans — pipeline proof, NOT production."))
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args(argv)

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
    )

    weights_path = Path(args.weights)
    if not weights_path.exists():
        logger.error("weights not found: %s", weights_path)
        return 4

    data_root = Path(args.data_root)
    storage_root = Path(args.storage_root)
    target_dir = storage_root / "models" / "container-split" / "v2"
    onnx_path = target_dir / "model.onnx"
    metadata_path = target_dir / "model.metadata.json"

    logger.info("loading weights from %s", weights_path)
    ckpt = torch.load(weights_path, map_location="cpu", weights_only=False)
    state_dict = ckpt["state_dict"] if isinstance(ckpt, dict) and "state_dict" in ckpt else ckpt

    # Build a model with the same shape; pretrained=False because we're
    # just going to overwrite the weights from the checkpoint anyway.
    model = ContainerSplitUNet(pretrained=False)
    model.load_state_dict(state_dict)

    logger.info("exporting ONNX (opset %d, dynamic batch) → %s", ONNX_OPSET, onnx_path)
    _export(model, onnx_path)
    sha = _sha256_of(onnx_path)
    size_bytes = onnx_path.stat().st_size
    logger.info("onnx sha256=%s size=%d bytes", sha, size_bytes)

    # Verify the artifact parses as ONNX before we declare success.
    import onnx  # type: ignore
    onnx_model = onnx.load(str(onnx_path))
    onnx.checker.check_model(onnx_model)
    logger.info(
        "onnx.checker OK ir_version=%d opset=%d",
        onnx_model.ir_version,
        onnx_model.opset_import[0].version,
    )

    # Stamp the metadata.
    eval_metrics_path = (
        Path(args.eval_metrics) if args.eval_metrics
        else weights_path.with_name("eval_metrics.json")
    )
    eval_metrics = _maybe_load_json(eval_metrics_path)

    payload = {
        "model_id": MODEL_ID,
        "version": args.version,
        "sha256": sha,
        "onnx_opset": str(ONNX_OPSET),
        "exported_at": _dt.datetime.now(_dt.timezone.utc)
            .isoformat(timespec="seconds").replace("+00:00", "Z"),
        "exported_by": args.exported_by,
        "git_commit": args.git_commit,
        "training_set_hash": _resolve_training_set_hash(weights_path, data_root),
        "eval_metrics": eval_metrics,
        "base_model": args.base_model,
        "size_bytes": size_bytes,
        "notes": args.notes,
    }
    metadata_path.write_text(
        json.dumps(payload, indent=2) + "\n", encoding="utf-8"
    )
    logger.info("metadata written → %s", metadata_path)

    print(f"[export-onnx] OK")
    print(f"[export-onnx] sha256       = {sha}")
    print(f"[export-onnx] size_bytes   = {size_bytes}")
    print(f"[export-onnx] onnx_path    = {onnx_path}")
    print(f"[export-onnx] metadata     = {metadata_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
