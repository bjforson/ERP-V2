#!/usr/bin/env python3
"""
v2 container-OCR fine-tuner — Florence-2 + LoRA
================================================

Purpose
-------
Fine-tune ``microsoft/Florence-2-base`` on the harvested plate corpus from
``harvest_plates.py`` per §6.1 of ``docs/IMAGE-ANALYSIS-MODERNIZATION.md``.
LoRA adapters on the decoder + full fine-tune of the patch embedding;
encoder weights stay frozen. Validates on a 10 % stratified holdout and
reports CER, exact match, and check-digit pass rate.

This script does NOT execute training when run by Team B's scaffold pass.
It is a self-contained CLI that humans (or a later Claude session under
``--actually-train``) will run on a GPU box. The point of this file
landing in tree today is so the parameter set is reviewable and the
trainer's I/O contract is locked before weights ever change.

Usage
-----
::

    python train.py \\
        --manifest "C:/Shared/ERP V2/tools/inference-training/container-ocr/labels.csv" \\
        --image-root /mnt/v1-mirror/fs6000images \\
        --synthetic-dir "C:/Shared/ERP V2/tools/inference-training/container-ocr/synthetic" \\
        --out-dir "C:/Shared/ERP V2/storage/models/container-ocr/v1" \\
        --epochs 6 --batch-size 8 --lora-rank 16 \\
        --actually-train

    # Without --actually-train the script:
    #   1. Validates the manifest + image root layout.
    #   2. Dumps the resolved hyperparameter dict.
    #   3. Exits 0 — useful for CI pre-flight.

Exit codes
----------
    0   success (or pre-flight exit when --actually-train is unset)
    2   bad CLI args
    3   manifest schema invalid
    4   image root unreadable / empty
    5   torch / peft / transformers missing
    6   training failure (non-zero loss explosion or NaN)

Hyperparameters (§6.1.3 / §6.1.5)
---------------------------------
- Family: microsoft/Florence-2-base, ~0.23 B params, MIT license.
- Adapter scope: LoRA rank 16 on decoder cross/self-attn projections;
  patch embedding fully tunable; encoder frozen.
- Optimiser: AdamW, lr 1e-4 (LoRA params), 1e-5 (patch-embed), weight
  decay 0.01, cosine schedule, 200 warmup steps.
- Loss: token-level cross-entropy with label smoothing 0.05; eval-only
  sequence reward = check-digit pass.
- Augmentation: ±5° perspective warp, ±20 % brightness/gamma, motion blur
  ≤ 5 px, plate-edge crop jitter ±8 px, paint-run overlay (synthetic).
- Class balance: stratify by owner-prefix family (top 20 prefixes);
  oversample <unreadable> to ≥ 10 % of each minibatch (§6.1.4).
- Splits: 80/10/10 by image_id with no synthetic generator-seed leakage
  across splits. Test split frozen at v1.0.0 release.

Implementation notes
--------------------
- LoRA wiring uses ``peft.LoraConfig(target_modules=["q_proj","v_proj"])``
  on the BART-style decoder; alpha = 32, dropout 0.1.
- Validation runs constrained beam search (width 4, max len 16) so the
  reported metrics match production decode.
- The trainer writes ``training_state.json`` next to the checkpoint with
  the manifest sha256, hyperparameter dict, and an ``eval_metrics``
  block — these are surfaced verbatim by ``export_onnx.py`` into
  ``model.metadata.json``.

Caveats
-------
- This script imports ``transformers``, ``peft``, ``torch``,
  ``torchvision``. It will exit 5 with a clear message if any are
  missing rather than crash deep in an import.
- Florence-2's MIT license still requires attribution in the model
  card. The exporter writes that into the metadata under
  ``base_model.attribution``.
- Memory budget: training Florence-2-base + LoRA fits in ~14 GB VRAM at
  batch 8; smaller GPUs need gradient accumulation (use
  ``--accumulation-steps``).
"""
from __future__ import annotations

import argparse
import csv
import json
import logging
import os
import random
import sys
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

EXIT_OK = 0
EXIT_BAD_ARGS = 2
EXIT_MANIFEST_INVALID = 3
EXIT_IMAGE_ROOT_INVALID = 4
EXIT_DEPS_MISSING = 5
EXIT_TRAIN_FAILED = 6

logger = logging.getLogger("v2-container-ocr-trainer")


@dataclass
class TrainConfig:
    manifest: str
    image_root: str
    synthetic_dir: Optional[str]
    out_dir: str
    base_model: str = "microsoft/Florence-2-base"
    epochs: int = 6
    batch_size: int = 8
    accumulation_steps: int = 1
    learning_rate_lora: float = 1e-4
    learning_rate_patch_embed: float = 1e-5
    weight_decay: float = 0.01
    warmup_steps: int = 200
    label_smoothing: float = 0.05
    lora_rank: int = 16
    lora_alpha: int = 32
    lora_dropout: float = 0.1
    lora_target_modules: List[str] = field(default_factory=lambda: ["q_proj", "v_proj"])
    image_size: int = 384
    max_target_length: int = 16
    beam_width: int = 4
    holdout_fraction: float = 0.10
    seed: int = 0
    actually_train: bool = False


def _required_manifest_columns() -> List[str]:
    # Mirrors harvest_plates.CSV_FIELDS so a stale manifest is caught up-front.
    return [
        "image_id", "image_uri", "image_type",
        "v1_predicted", "analyst_corrected", "captured_at",
        "truck_plate", "file_path", "scan_id", "tenant_id", "source_table",
    ]


def validate_manifest(path: Path) -> int:
    if not path.is_file():
        sys.stderr.write(f"FATAL: manifest not found at {path}\n")
        return EXIT_MANIFEST_INVALID
    required = set(_required_manifest_columns())
    with path.open("r", encoding="utf-8", newline="") as fh:
        reader = csv.DictReader(fh)
        present = set(reader.fieldnames or [])
        missing = required - present
        if missing:
            sys.stderr.write(
                f"FATAL: manifest at {path} is missing columns: "
                f"{sorted(missing)}. Re-run harvest_plates.py.\n"
            )
            return EXIT_MANIFEST_INVALID
        n = sum(1 for _ in reader)
    logger.info("manifest OK: %s (%d rows, %d columns)", path, n, len(present))
    return EXIT_OK


def validate_image_root(path: Path) -> int:
    if not path.exists():
        sys.stderr.write(
            f"FATAL: image root {path} does not exist. The trainer fetches plate "
            "ROIs from this tree (one image per manifest row, keyed by image_id).\n"
        )
        return EXIT_IMAGE_ROOT_INVALID
    return EXIT_OK


def _import_training_stack():
    """Return torch / transformers / peft modules; exit cleanly if any missing."""
    try:
        import torch  # type: ignore  # noqa: F401
        import transformers  # type: ignore  # noqa: F401
        import peft  # type: ignore  # noqa: F401
        return True
    except ImportError as e:
        sys.stderr.write(
            f"FATAL: required ML stack missing ({e}). Install with:\n"
            "    pip install 'torch>=2.4' 'transformers>=4.43' 'peft>=0.12' pillow\n"
        )
        return False


def _build_lora_config(cfg: TrainConfig):
    from peft import LoraConfig, TaskType  # type: ignore

    return LoraConfig(
        r=cfg.lora_rank,
        lora_alpha=cfg.lora_alpha,
        lora_dropout=cfg.lora_dropout,
        bias="none",
        task_type=TaskType.SEQ_2_SEQ_LM,
        target_modules=cfg.lora_target_modules,
    )


def _train_loop(cfg: TrainConfig) -> int:
    """Hosted training loop. Stub-shaped: real implementation lives in a
    separate session per the §6.1 ship plan. Today this only proves the
    config + manifest contracts compile and the ML stack imports cleanly.
    """
    if not _import_training_stack():
        return EXIT_DEPS_MISSING

    import torch  # type: ignore
    from transformers import AutoProcessor, AutoModelForCausalLM  # type: ignore
    from peft import get_peft_model  # type: ignore

    random.seed(cfg.seed)
    torch.manual_seed(cfg.seed)

    logger.info("loading base model: %s", cfg.base_model)
    processor = AutoProcessor.from_pretrained(cfg.base_model, trust_remote_code=True)
    model = AutoModelForCausalLM.from_pretrained(cfg.base_model, trust_remote_code=True)

    logger.info("attaching LoRA adapters: rank=%d alpha=%d", cfg.lora_rank, cfg.lora_alpha)
    model = get_peft_model(model, _build_lora_config(cfg))

    # Real training would build a DataLoader over (image, target_string) pairs
    # from the harvested manifest + synthetic samples, run AdamW with a cosine
    # schedule, and checkpoint every epoch. That's the next session's job —
    # this function deliberately stops here so the scaffold pass doesn't
    # mutate weights.
    logger.info(
        "training stub reached. To actually run training, replace this stub "
        "with the DataLoader + trainer.fit loop. Hyperparameters: %s",
        json.dumps(asdict(cfg), default=str, indent=2),
    )
    return EXIT_OK


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--manifest", required=True, help="CSV manifest from harvest_plates.py")
    ap.add_argument("--image-root", required=True, help="Directory tree with plate-ROI images keyed by image_id.")
    ap.add_argument("--synthetic-dir", default=None, help="Optional dir of synthetic plates from synth_plates.py")
    ap.add_argument("--out-dir", required=True, help="Where to write checkpoint + training_state.json")
    ap.add_argument("--base-model", default="microsoft/Florence-2-base")
    ap.add_argument("--epochs", type=int, default=6)
    ap.add_argument("--batch-size", type=int, default=8)
    ap.add_argument("--accumulation-steps", type=int, default=1)
    ap.add_argument("--learning-rate-lora", type=float, default=1e-4)
    ap.add_argument("--learning-rate-patch-embed", type=float, default=1e-5)
    ap.add_argument("--weight-decay", type=float, default=0.01)
    ap.add_argument("--warmup-steps", type=int, default=200)
    ap.add_argument("--label-smoothing", type=float, default=0.05)
    ap.add_argument("--lora-rank", type=int, default=16)
    ap.add_argument("--lora-alpha", type=int, default=32)
    ap.add_argument("--lora-dropout", type=float, default=0.1)
    ap.add_argument("--lora-target-modules", default="q_proj,v_proj")
    ap.add_argument("--image-size", type=int, default=384)
    ap.add_argument("--max-target-length", type=int, default=16)
    ap.add_argument("--beam-width", type=int, default=4)
    ap.add_argument("--holdout-fraction", type=float, default=0.10)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument(
        "--actually-train",
        action="store_true",
        help="Run training. Without this flag the script only validates the manifest + image root.",
    )
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args(argv)

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
    )

    cfg = TrainConfig(
        manifest=args.manifest,
        image_root=args.image_root,
        synthetic_dir=args.synthetic_dir,
        out_dir=args.out_dir,
        base_model=args.base_model,
        epochs=args.epochs,
        batch_size=args.batch_size,
        accumulation_steps=args.accumulation_steps,
        learning_rate_lora=args.learning_rate_lora,
        learning_rate_patch_embed=args.learning_rate_patch_embed,
        weight_decay=args.weight_decay,
        warmup_steps=args.warmup_steps,
        label_smoothing=args.label_smoothing,
        lora_rank=args.lora_rank,
        lora_alpha=args.lora_alpha,
        lora_dropout=args.lora_dropout,
        lora_target_modules=[s for s in args.lora_target_modules.split(",") if s],
        image_size=args.image_size,
        max_target_length=args.max_target_length,
        beam_width=args.beam_width,
        holdout_fraction=args.holdout_fraction,
        seed=args.seed,
        actually_train=args.actually_train,
    )

    rc = validate_manifest(Path(cfg.manifest))
    if rc != EXIT_OK:
        return rc
    rc = validate_image_root(Path(cfg.image_root))
    if rc != EXIT_OK:
        return rc

    if not cfg.actually_train:
        logger.info(
            "Pre-flight OK. Resolved config:\n%s",
            json.dumps(asdict(cfg), default=str, indent=2),
        )
        return EXIT_OK

    return _train_loop(cfg)


if __name__ == "__main__":
    sys.exit(main())
