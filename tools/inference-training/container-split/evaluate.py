#!/usr/bin/env python3
"""
v2 container-split — Step 3: evaluate the trained student
=========================================================

Purpose
-------
Run the §3.5 evaluation suite on a saved ``student.pt`` checkpoint over
the holdout test split locked at training time. Reports every
acceptance metric the model ships against, and additionally slices each
metric by container count and source scanner per the spec's stratified
reporting requirement.

Acceptance metrics (§3.5)
-------------------------
- **MAE in pixels** vs ground truth — primary metric, target ≤ 4 px.
  Computed as ``|argmax(heatmap) - chosen_split_x|`` in the resized
  1568-wide canvas. When ``--prefer-ground-truth`` is set and an
  operator label exists, that's the reference; otherwise the chosen
  split (multi-strategy consensus) is the reference.
- **F1@5 px** of detected split positions — secondary, target ≥ 0.97.
  A predicted peak counts as a true positive when its argmax is within
  5 px of the reference. Single-prediction-per-image regime here
  (matches v1's dual-container assumption); F1 collapses to "share of
  predictions within 5 px" for this corpus.
- **% agreement with multi-strategy consensus** — sanity, target ≥ 97 %.
  Share of predictions within ±30 px of ``split_x_resized`` (the v1
  AGREEMENT_THRESHOLD_PX).
- **Catastrophic-fail rate** (MAE > 30 px) — safety, target < 0.5 %.

Stratified slices
-----------------
Per spec §3.4 footnote — slice by ``container_count`` and by
``scanner_type`` so over- / under-performing strata are visible.

Usage
-----
::

    python evaluate.py \\
        --weights runs/<run_id>/student.pt \\
        --data-root data \\
        --holdout data/holdout.test.json

Outputs
-------
- prints a markdown-ish report to stdout
- writes ``runs/<run_id>/eval_metrics.json`` with the full numbers (or
  ``--out`` if specified) so ``export_onnx.py`` can stamp it into
  ``model.metadata.json``.

Honest caveat
-------------
With ~88 jobs in v1's corpus and ~10 % stratified holdout, "the test
set" is roughly 10 scans. Numbers below 4 px MAE on this size are
overfitting noise, not a green-light to deploy. The §3.5 acceptance
gates are written for ≥ 50 k samples — they apply to a real run, not
this smoke run.
"""
from __future__ import annotations

import argparse
import json
import logging
import math
import sys
from collections import defaultdict
from pathlib import Path

import numpy as np
import pandas as pd
import torch
from torch.utils.data import DataLoader

# Reuse the trainer's dataset + model.
from train import (
    ContainerSplitDataset,
    ContainerSplitUNet,
    INPUT_WIDTH,
)

logger = logging.getLogger("evaluate-container-split")

CATASTROPHIC_PX = 30.0
F1_THRESHOLD_PX = 5.0
AGREEMENT_THRESHOLD_PX = 30.0


def _load_holdout(holdout_path: Path) -> set[str]:
    if not holdout_path.exists():
        raise FileNotFoundError(f"holdout file not found: {holdout_path}")
    payload = json.loads(holdout_path.read_text(encoding="utf-8"))
    return set(payload.get("scan_ids", []))


def _slice_metrics(rows: list[dict], key: str) -> dict[str, dict]:
    bucket: dict[str, list[dict]] = defaultdict(list)
    for r in rows:
        bucket[str(r.get(key) or "_")].append(r)
    out: dict[str, dict] = {}
    for k, items in bucket.items():
        out[k] = _aggregate(items)
    return out


def _aggregate(items: list[dict]) -> dict:
    if not items:
        return {"n": 0}
    err = np.array([abs(it["pred_x"] - it["ref_x"]) for it in items], dtype=np.float64)
    chosen_err = np.array(
        [abs(it["pred_x"] - it["chosen_x"]) for it in items], dtype=np.float64
    )
    return {
        "n": int(len(items)),
        "mae_px": float(np.mean(err)),
        "median_px": float(np.median(err)),
        "p95_px": float(np.percentile(err, 95)) if len(err) >= 20 else None,
        "f1_at_5px": float(np.mean(err <= F1_THRESHOLD_PX)),
        "pct_agreement_consensus_30px": float(np.mean(chosen_err <= AGREEMENT_THRESHOLD_PX)),
        "catastrophic_fail_rate_30px": float(np.mean(err > CATASTROPHIC_PX)),
    }


def main(argv: list[str] | None = None) -> int:
    here = Path(__file__).resolve().parent
    ap = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument("--weights", required=True,
                    help="path to student.pt produced by train.py")
    ap.add_argument("--data-root", default=str(here / "data"))
    ap.add_argument("--holdout", default=None,
                    help="path to holdout.test.json (default: <data-root>/holdout.test.json)")
    ap.add_argument("--prefer-ground-truth", action="store_true",
                    help="use operator ground_truth_split_x as reference when present")
    ap.add_argument("--out", default=None)
    ap.add_argument("--device", default=None)
    ap.add_argument("--batch-size", type=int, default=4)
    ap.add_argument("--num-workers", type=int, default=0)
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args(argv)

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
    )

    weight_path = Path(args.weights)
    data_root = Path(args.data_root)
    holdout_path = Path(args.holdout) if args.holdout else data_root / "holdout.test.json"
    manifest_path = data_root / "manifest.parquet"

    if not weight_path.exists():
        logger.error("weights not found: %s", weight_path)
        return 4
    if not manifest_path.exists():
        logger.error("manifest not found: %s", manifest_path)
        return 4

    df = pd.read_parquet(manifest_path)
    holdout_ids = _load_holdout(holdout_path)
    test_df = df[df["scan_id"].isin(holdout_ids)].reset_index(drop=True)
    logger.info("holdout test rows=%d (manifest rows=%d)", len(test_df), len(df))
    if len(test_df) == 0:
        logger.error("empty test set; nothing to evaluate")
        return 6

    device = torch.device(
        args.device or ("cuda" if torch.cuda.is_available() else "cpu")
    )
    logger.info("device=%s", device)

    ckpt = torch.load(weight_path, map_location=device, weights_only=False)
    model = ContainerSplitUNet(pretrained=False).to(device)
    state = ckpt["state_dict"] if isinstance(ckpt, dict) and "state_dict" in ckpt else ckpt
    model.load_state_dict(state)
    model.eval()

    loader = DataLoader(
        ContainerSplitDataset(test_df, data_root, args.prefer_ground_truth),
        batch_size=args.batch_size,
        shuffle=False,
        num_workers=args.num_workers,
    )

    rows: list[dict] = []
    with torch.no_grad():
        for batch in loader:
            x = batch["input"].to(device, non_blocking=True)
            pred = model(x)
            pred_x = pred.argmax(dim=1).float().cpu().numpy()
            scan_ids = batch["scan_id"]
            for i, sid in enumerate(scan_ids):
                rec = test_df[test_df["scan_id"] == sid].iloc[0]
                ref_x = (
                    float(rec["ground_truth_x_resized"])
                    if args.prefer_ground_truth
                    and not pd.isna(rec.get("ground_truth_x_resized", float("nan")))
                    else float(rec["split_x_resized"])
                )
                if math.isnan(ref_x):
                    continue
                rows.append({
                    "scan_id": sid,
                    "pred_x": float(pred_x[i]),
                    "ref_x": ref_x,
                    "chosen_x": float(rec["split_x_resized"]),
                    "scanner_type": rec.get("scanner_type") or "",
                    "container_count": int(rec.get("container_count") or 2),
                    "fetch_status": rec.get("fetch_status") or "",
                })

    overall = _aggregate(rows)
    by_count = _slice_metrics(rows, "container_count")
    by_scanner = _slice_metrics(rows, "scanner_type")
    by_fetch = _slice_metrics(rows, "fetch_status")

    print()
    print("# §3.5 evaluation report")
    print()
    print(f"holdout n = {len(rows)} (loaded from {holdout_path})")
    print(f"weights = {weight_path}")
    print(f"reference = {'ground_truth_x_resized' if args.prefer_ground_truth else 'split_x_resized'}")
    print()
    print("## Overall")
    for k, v in overall.items():
        print(f"  {k:32s}  {v}")
    print()
    print("## By container_count")
    for k, v in sorted(by_count.items()):
        print(f"  count={k}: {v}")
    print()
    print("## By scanner_type")
    for k, v in sorted(by_scanner.items()):
        print(f"  scanner={k!r}: {v}")
    print()
    print("## By fetch_status")
    for k, v in sorted(by_fetch.items()):
        print(f"  fetch={k!r}: {v}")
    print()
    print("## Acceptance gates (section 3.5)")
    if overall.get("n", 0) > 0:
        print(f"  MAE <= 4 px                : observed {overall['mae_px']:.2f} px"
              + ("  PASS" if overall["mae_px"] <= 4.0 else "  FAIL"))
        print(f"  F1@5 px >= 0.97            : observed {overall['f1_at_5px']:.3f}"
              + ("  PASS" if overall["f1_at_5px"] >= 0.97 else "  FAIL"))
        print(f"  Consensus agreement >= 0.97: observed {overall['pct_agreement_consensus_30px']:.3f}"
              + ("  PASS" if overall["pct_agreement_consensus_30px"] >= 0.97 else "  FAIL"))
        print(f"  Catastrophic-fail < 0.005  : observed {overall['catastrophic_fail_rate_30px']:.4f}"
              + ("  PASS" if overall["catastrophic_fail_rate_30px"] < 0.005 else "  FAIL"))
    print()
    print("## Honest data-volume caveat")
    print(f"  v1 corpus is ~88 jobs / 21 ground-truth labels. The holdout above is")
    print(f"  ≈10 scans. §3.5 acceptance gates are written against ≥ 50 k samples.")
    print(f"  Treat any green tick on this run as 'pipeline works', NOT 'model ships'.")

    out_path = (
        Path(args.out) if args.out
        else weight_path.with_name("eval_metrics.json")
    )
    out_path.write_text(
        json.dumps(
            {
                "overall": overall,
                "by_container_count": by_count,
                "by_scanner_type": by_scanner,
                "by_fetch_status": by_fetch,
                "n_holdout": len(rows),
                "reference": (
                    "ground_truth_x_resized"
                    if args.prefer_ground_truth else "split_x_resized"
                ),
                "weights": str(weight_path),
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    logger.info("eval metrics → %s", out_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
