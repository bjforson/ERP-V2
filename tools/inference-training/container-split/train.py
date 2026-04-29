#!/usr/bin/env python3
"""
v2 container-split — Step 2: train the U-Net student
=====================================================

Purpose
-------
Train the §3.3 ResNet-18 / U-Net student on the prepared dataset
(``manifest.parquet`` + ``images/*.npy``) produced by
``prepare_data.py``. Replaces the bring-up stub trainer at
``tools/inference-bringup/train_stub_split.py`` — the architecture is
the same so the I/O contract from §3.2 is preserved, but here the
weights actually move.

Architecture (§3.3)
-------------------
- Encoder: ``torchvision.models.resnet18`` with **ImageNet pretrained
  weights** per the spec ("ImageNet-pretrained init"). The 3-channel
  stem is replaced with a single-channel 7×7 conv whose weights are
  initialised from the mean of the original RGB stem (a common
  recipe — keeps the pretrained spatial filter, drops the colour
  selectivity).
- Decoder: 4 up-blocks (bilinear x2 + Conv-BN-ReLU x2) with skip
  connections from the matching encoder taps.
- Head: 1×1 conv to a single channel → mean-pool over the Y axis →
  sigmoid → ``(N, 1568)`` heatmap.

Loss (§3.5)
-----------
Gaussian-targeted MSE with σ = 8 px around the chosen split position.
For each sample the target is a 1D Gaussian centred on
``split_x_resized`` (or ``ground_truth_x_resized`` when
``--prefer-ground-truth`` is set). The loss is plain MSE between the
sigmoid heatmap and the Gaussian target.

Note on σ. The §3.5 σ is 8 px in the resized 1568-wide canvas. It is
**not** the per-original-pixel σ. This is intentional: the model output
is in the resized space.

Splits (§3.4)
-------------
80/10/10 train/val/test, stratified by ``container_count`` and by
``scanner_type``. The test split is locked at training time and saved
as a list of scan_ids to ``data/holdout.test.json`` so the same set can
be reused by ``evaluate.py``. The val split is the within-train check;
the test split is what ``evaluate.py`` reports.

Smoke training only
-------------------
This is a smoke run by design — **1 epoch maximum** unless
``--max-epochs`` is overridden. With ~88 jobs available, anything more
is just memorising the noise. The script prints train + val loss at
the start and end of training and saves the resulting weights.

Outputs
-------
::

    data/
      holdout.test.json              — list of scan_ids in the test split
      training_set_hash.txt          — sha256 of sorted train scan_ids
    runs/
      <run_id>/
        student.pt                   — torch state_dict
        training_summary.json        — hyperparams + train/val losses
        training_set_hash.txt        — copy

Flags
-----
``--data-root``         dir holding manifest.parquet + images/
``--run-dir``           output dir (default runs/run-<UTC>/)
``--max-epochs``        default 1 — do NOT raise without a reason
``--batch-size``        default 4
``--lr``                default 1e-4
``--seed``              default 0
``--prefer-ground-truth`` use ``ground_truth_x_resized`` when present
``--device``            ``cpu`` / ``cuda`` (auto-pick when omitted)
``--num-workers``       DataLoader workers (default 0 — Windows-safe)

Usage
-----
::

    "C:/Shared/ERP V2/tools/inference-bringup/.venv/Scripts/python.exe" \\
        train.py --data-root data --max-epochs 1
"""
from __future__ import annotations

import argparse
import dataclasses
import datetime as _dt
import hashlib
import json
import logging
import math
import os
import random
import sys
from collections import Counter, defaultdict
from pathlib import Path
from typing import Optional

import numpy as np
import pandas as pd
import torch
import torch.nn as nn
import torch.nn.functional as F
from torch.utils.data import DataLoader, Dataset
from torchvision.models import ResNet18_Weights, resnet18

INPUT_HEIGHT = 472
INPUT_WIDTH = 1568
SIGMA_PX = 8.0
INPUT_NAME = "input"
OUTPUT_NAME = "heatmap"

logger = logging.getLogger("train-container-split")


# ── dataset ────────────────────────────────────────────────────────────
class ContainerSplitDataset(Dataset):
    def __init__(self, df: pd.DataFrame, data_root: Path, prefer_gt: bool) -> None:
        self.df = df.reset_index(drop=True)
        self.data_root = data_root
        self.prefer_gt = prefer_gt

    def __len__(self) -> int:
        return len(self.df)

    def _label_x(self, row: pd.Series) -> float:
        if self.prefer_gt and not pd.isna(row.get("ground_truth_x_resized", float("nan"))):
            return float(row["ground_truth_x_resized"])
        return float(row["split_x_resized"])

    def __getitem__(self, idx: int):
        row = self.df.iloc[idx]
        npy_path = self.data_root / row["npy_path"]
        x = np.load(npy_path).astype(np.float32)
        if x.ndim == 2:
            x = x[np.newaxis, ...]
        # Build the Gaussian target heatmap.
        label_x = self._label_x(row)
        if math.isnan(label_x):
            label_x = INPUT_WIDTH / 2.0  # fall back to centre — flagged via mask
            label_valid = 0.0
        else:
            label_valid = 1.0
        idxs = np.arange(INPUT_WIDTH, dtype=np.float32)
        target = np.exp(-0.5 * ((idxs - label_x) / SIGMA_PX) ** 2)
        return {
            "input": torch.from_numpy(x),
            "target": torch.from_numpy(target.astype(np.float32)),
            "label_x": torch.tensor(label_x, dtype=torch.float32),
            "label_valid": torch.tensor(label_valid, dtype=torch.float32),
            "scan_id": row["scan_id"],
        }


# ── model ──────────────────────────────────────────────────────────────
class _UpBlock(nn.Module):
    def __init__(self, in_ch: int, skip_ch: int, out_ch: int) -> None:
        super().__init__()
        self.conv1 = nn.Conv2d(in_ch + skip_ch, out_ch, kernel_size=3, padding=1, bias=False)
        self.bn1 = nn.BatchNorm2d(out_ch)
        self.conv2 = nn.Conv2d(out_ch, out_ch, kernel_size=3, padding=1, bias=False)
        self.bn2 = nn.BatchNorm2d(out_ch)

    def forward(self, x: torch.Tensor, skip: torch.Tensor) -> torch.Tensor:
        x = F.interpolate(x, size=skip.shape[-2:], mode="bilinear", align_corners=False)
        x = torch.cat([x, skip], dim=1)
        x = F.relu(self.bn1(self.conv1(x)), inplace=True)
        x = F.relu(self.bn2(self.conv2(x)), inplace=True)
        return x


class ContainerSplitUNet(nn.Module):
    """Real-trainer counterpart of ContainerSplitStubModel — same shape,
    different weight init. Pretrained encoder, randomly-init decoder."""

    def __init__(self, pretrained: bool = True) -> None:
        super().__init__()
        weights = ResNet18_Weights.IMAGENET1K_V1 if pretrained else None
        encoder = resnet18(weights=weights)

        # Single-channel adaptation. Initialise the new 1-channel stem
        # from the mean of the pretrained 3-channel stem so we keep the
        # ImageNet spatial filter shape.
        new_stem = nn.Conv2d(1, 64, kernel_size=7, stride=2, padding=3, bias=False)
        if pretrained:
            with torch.no_grad():
                new_stem.weight.copy_(encoder.conv1.weight.mean(dim=1, keepdim=True))
        self.stem = nn.Sequential(new_stem, encoder.bn1, encoder.relu)
        self.maxpool = encoder.maxpool

        self.layer1 = encoder.layer1
        self.layer2 = encoder.layer2
        self.layer3 = encoder.layer3
        self.layer4 = encoder.layer4

        self.up1 = _UpBlock(in_ch=512, skip_ch=256, out_ch=256)
        self.up2 = _UpBlock(in_ch=256, skip_ch=128, out_ch=128)
        self.up3 = _UpBlock(in_ch=128, skip_ch=64, out_ch=64)
        self.up4 = _UpBlock(in_ch=64, skip_ch=64, out_ch=32)

        self.head = nn.Conv2d(32, 1, kernel_size=1)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        s0 = self.stem(x)
        x1 = self.maxpool(s0)
        e1 = self.layer1(x1)
        e2 = self.layer2(e1)
        e3 = self.layer3(e2)
        e4 = self.layer4(e3)

        d1 = self.up1(e4, e3)
        d2 = self.up2(d1, e2)
        d3 = self.up3(d2, e1)
        d4 = self.up4(d3, s0)

        d4 = F.interpolate(d4, size=(INPUT_HEIGHT, INPUT_WIDTH), mode="bilinear", align_corners=False)

        logits = self.head(d4)
        heatmap = logits.mean(dim=2).squeeze(1)
        heatmap = torch.sigmoid(heatmap)
        return heatmap


# ── splits ─────────────────────────────────────────────────────────────
def _stratified_split(
    df: pd.DataFrame,
    seed: int,
    val_frac: float = 0.1,
    test_frac: float = 0.1,
) -> tuple[pd.DataFrame, pd.DataFrame, pd.DataFrame]:
    rng = random.Random(seed)
    # Stratify on (container_count, scanner_type or '_').
    buckets: dict[tuple, list[int]] = defaultdict(list)
    for i, row in df.iterrows():
        key = (int(row["container_count"]),
               (row.get("scanner_type") or "").strip() or "_")
        buckets[key].append(i)
    train_idx, val_idx, test_idx = [], [], []
    for _, idxs in buckets.items():
        idxs = idxs[:]
        rng.shuffle(idxs)
        n = len(idxs)
        n_test = max(1, int(round(test_frac * n))) if n >= 3 else 0
        n_val = max(1, int(round(val_frac * n))) if n - n_test >= 2 else 0
        test_idx += idxs[:n_test]
        val_idx += idxs[n_test:n_test + n_val]
        train_idx += idxs[n_test + n_val:]
    train_df = df.iloc[sorted(train_idx)].reset_index(drop=True)
    val_df = df.iloc[sorted(val_idx)].reset_index(drop=True)
    test_df = df.iloc[sorted(test_idx)].reset_index(drop=True)
    return train_df, val_df, test_df


def _training_set_hash(train_df: pd.DataFrame) -> str:
    sha = hashlib.sha256()
    for sid in sorted(train_df["scan_id"].tolist()):
        sha.update(sid.encode("utf-8"))
        sha.update(b"\n")
    return sha.hexdigest()


# ── train loop ─────────────────────────────────────────────────────────
def _epoch(
    model: nn.Module,
    loader: DataLoader,
    device: torch.device,
    optimizer: Optional[torch.optim.Optimizer],
) -> tuple[float, float]:
    train_mode = optimizer is not None
    if train_mode:
        model.train()
    else:
        model.eval()
    total_loss = 0.0
    total_mae = 0.0
    n_seen = 0
    n_valid = 0
    grad_ctx = torch.enable_grad() if train_mode else torch.no_grad()
    with grad_ctx:
        for batch in loader:
            x = batch["input"].to(device, non_blocking=True)
            target = batch["target"].to(device, non_blocking=True)
            label_x = batch["label_x"].to(device)
            label_valid = batch["label_valid"].to(device)
            pred = model(x)
            # Per-sample MSE so we can mask labels of unknown ground truth.
            per_sample = ((pred - target) ** 2).mean(dim=1)
            valid_mask = label_valid > 0.5
            if valid_mask.any():
                loss = per_sample[valid_mask].mean()
            else:
                loss = per_sample.mean()  # degenerate — should not occur
            if train_mode:
                optimizer.zero_grad(set_to_none=True)
                loss.backward()
                optimizer.step()
            with torch.no_grad():
                pred_x = pred.argmax(dim=1).float()
                err = (pred_x - label_x).abs()
                if valid_mask.any():
                    total_mae += err[valid_mask].sum().item()
                    n_valid += int(valid_mask.sum().item())
            total_loss += loss.item() * x.shape[0]
            n_seen += x.shape[0]
    avg_loss = total_loss / max(1, n_seen)
    avg_mae = total_mae / max(1, n_valid)
    return avg_loss, avg_mae


def main(argv: list[str] | None = None) -> int:
    here = Path(__file__).resolve().parent
    ap = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument("--data-root", default=str(here / "data"))
    ap.add_argument("--run-dir", default=None)
    ap.add_argument("--max-epochs", type=int, default=1,
                    help="STAY AT 1 unless you have a reason — see docstring.")
    ap.add_argument("--batch-size", type=int, default=4)
    ap.add_argument("--lr", type=float, default=1e-4)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--prefer-ground-truth", action="store_true",
                    help="use operator ground_truth_split_x when available")
    ap.add_argument("--device", default=None)
    ap.add_argument("--num-workers", type=int, default=0)
    ap.add_argument("--no-pretrained", action="store_true",
                    help="skip ImageNet pretrained init (useful when offline)")
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args(argv)

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
    )

    data_root = Path(args.data_root)
    manifest_path = data_root / "manifest.parquet"
    if not manifest_path.exists():
        logger.error("manifest not found at %s — run prepare_data.py first", manifest_path)
        return 4

    df = pd.read_parquet(manifest_path)
    logger.info("manifest rows=%d", len(df))
    logger.info("scanner_type counts: %s",
                Counter(df["scanner_type"].fillna("").tolist()))
    logger.info("container_count counts: %s",
                Counter(df["container_count"].tolist()))

    random.seed(args.seed)
    np.random.seed(args.seed)
    torch.manual_seed(args.seed)

    train_df, val_df, test_df = _stratified_split(df, args.seed)
    logger.info("split sizes: train=%d val=%d test=%d",
                len(train_df), len(val_df), len(test_df))

    if len(train_df) == 0:
        logger.error("empty train split; aborting")
        return 4

    # Persist the test split for evaluate.py.
    holdout_path = data_root / "holdout.test.json"
    holdout_path.write_text(
        json.dumps({"scan_ids": test_df["scan_id"].tolist()}, indent=2),
        encoding="utf-8",
    )
    logger.info("wrote holdout test split → %s (%d ids)",
                holdout_path, len(test_df))

    training_hash = _training_set_hash(train_df)
    (data_root / "training_set_hash.txt").write_text(training_hash, encoding="utf-8")
    logger.info("training_set_hash=%s", training_hash)

    device = torch.device(
        args.device or ("cuda" if torch.cuda.is_available() else "cpu")
    )
    logger.info("device=%s", device)

    train_loader = DataLoader(
        ContainerSplitDataset(train_df, data_root, args.prefer_ground_truth),
        batch_size=args.batch_size,
        shuffle=True,
        num_workers=args.num_workers,
        drop_last=False,
    )
    val_loader = DataLoader(
        ContainerSplitDataset(val_df, data_root, args.prefer_ground_truth),
        batch_size=args.batch_size,
        shuffle=False,
        num_workers=args.num_workers,
    ) if len(val_df) > 0 else None

    pretrained = not args.no_pretrained
    logger.info("model: ResNet-18 U-Net (pretrained=%s)", pretrained)
    try:
        model = ContainerSplitUNet(pretrained=pretrained).to(device)
    except Exception as e:
        logger.warning("pretrained init failed (%s); falling back to no-pretrained", e)
        model = ContainerSplitUNet(pretrained=False).to(device)
        pretrained = False
    n_params = sum(p.numel() for p in model.parameters())
    logger.info("params=%s (target ~12 M per §3.3)", f"{n_params:,}")

    optimizer = torch.optim.Adam(model.parameters(), lr=args.lr)

    # Pre-train check — what is loss before any optimisation step?
    pre_train_loss, pre_train_mae = _epoch(model, train_loader, device, optimizer=None)
    logger.info("PRE-TRAIN train_loss=%.6f train_mae_px=%.2f",
                pre_train_loss, pre_train_mae)

    history: list[dict] = []
    best_val = float("inf")
    for epoch in range(args.max_epochs):
        train_loss, train_mae = _epoch(model, train_loader, device, optimizer)
        if val_loader is not None and len(val_loader) > 0:
            val_loss, val_mae = _epoch(model, val_loader, device, optimizer=None)
        else:
            val_loss, val_mae = float("nan"), float("nan")
        logger.info(
            "epoch %d/%d  train_loss=%.6f  train_mae_px=%.2f  val_loss=%.6f  val_mae_px=%.2f",
            epoch + 1, args.max_epochs, train_loss, train_mae, val_loss, val_mae,
        )
        history.append({
            "epoch": epoch + 1,
            "train_loss": train_loss,
            "train_mae_px": train_mae,
            "val_loss": val_loss,
            "val_mae_px": val_mae,
        })
        if not math.isnan(val_loss):
            best_val = min(best_val, val_loss)

    run_id = args.run_dir or _dt.datetime.utcnow().strftime("run-%Y%m%dT%H%M%SZ")
    run_dir = Path(args.run_dir) if args.run_dir else (here / "runs" / run_id)
    run_dir.mkdir(parents=True, exist_ok=True)

    weight_path = run_dir / "student.pt"
    torch.save(
        {
            "state_dict": model.state_dict(),
            "training_set_hash": training_hash,
            "input_shape": [1, 1, INPUT_HEIGHT, INPUT_WIDTH],
            "pretrained": pretrained,
            "max_epochs": args.max_epochs,
        },
        weight_path,
    )
    summary_path = run_dir / "training_summary.json"
    summary_path.write_text(
        json.dumps(
            {
                "run_id": run_id,
                "args": {k: v for k, v in vars(args).items()},
                "device": str(device),
                "n_train": len(train_df),
                "n_val": len(val_df),
                "n_test": len(test_df),
                "training_set_hash": training_hash,
                "pre_train_loss": pre_train_loss,
                "pre_train_mae_px": pre_train_mae,
                "history": history,
                "best_val_loss": None if math.isinf(best_val) else best_val,
                "weight_path": str(weight_path),
                "n_params": n_params,
                "pretrained": pretrained,
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    (run_dir / "training_set_hash.txt").write_text(training_hash, encoding="utf-8")
    logger.info("saved weights → %s", weight_path)
    logger.info("saved summary → %s", summary_path)
    print(f"[train] OK weight_path={weight_path}")
    print(f"[train] OK training_set_hash={training_hash}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
