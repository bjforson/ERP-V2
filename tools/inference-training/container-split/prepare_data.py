#!/usr/bin/env python3
"""
v2 container-split — Step 1: prepare training data
==================================================

Purpose
-------
Materialise a numpy-on-disk dataset for the §3 container-split student
trainer from the v1 splitter labels exported by ``tools/v1-label-export/
export_splits.py``. The label CSV gives us scan IDs + chosen split
positions. This script fetches the actual image bytes for each scan
(read-only over v1, via the splitter HTTP API), applies the §3.2
preprocessing chain, and writes the result as ``.npy`` files plus a
``manifest.parquet`` that the trainer/evaluator load directly.

Why a separate prep step
------------------------
- Image fetch + decode + percentile-stretch is slow; doing it inside a
  PyTorch ``Dataset`` would re-pay the cost every epoch.
- The trainer never touches v1 directly — only the prep script does.
  This keeps the v1-read-only invariant tight: a single, auditable
  network entrypoint here, and offline ``.npy`` files everywhere else.
- The manifest carries everything the trainer needs for stratified
  splits + loss masking (chosen split px, ground-truth px, scanner
  type, container count, agreement score).

§3.2 preprocessing chain (matches Claude teacher inputs in v1)
--------------------------------------------------------------
1. Fetch ``GET /api/split/{scan_id}/original`` from the splitter on
   ``SPLITTER_BASE_URL`` (default ``http://127.0.0.1:5320``). v1 stores
   image bytes inline in ``image_split_jobs.image_data`` (LargeBinary).
   Falls back to a direct read of that BLOB column via psycopg2 when
   the API is unreachable, controlled by ``--source``.
2. Decode RGB / grayscale via Pillow. Convert to single-channel float32
   in [0, 1] (mean over RGB).
3. Top 25 % crop along the height axis — the same view the v1 Claude
   teacher sees (`claude_vision.py:83–86`).
4. Long-edge resize to 1568 px preserving aspect ratio, bilinear.
5. Pad short-edge to 472 px with zero (black). This lands shape exactly
   at ``(1, 472, 1568)`` per §3.2 / matches the bring-up stub I/O.
6. Percentile stretch 0.5 % – 99.5 % → [0, 1] (matches v1's
   ``fs6000.py:30`` normalization).

Outputs
-------
::

    data/
      labels.csv                        — input from v1 export
      images/
        <scan_id>.npy                   — float32 (1, 472, 1568)
      manifest.parquet                  — per-scan metadata + split labels

The manifest schema:

==================  =====  ==============================================
column              dtype  meaning
==================  =====  ==============================================
scan_id             str    image_split_jobs.id (UUID)
npy_path            str    relative path to the .npy file
input_height        int    472 (constant; sanity field)
input_width         int    1568 (constant)
original_width      int    pre-crop width from CSV
original_height     int    pre-crop height from CSV
split_x_resized     float  chosen split, mapped into [0, 1568)
ground_truth_x_resized
                    float  ground-truth split, mapped into [0, 1568)
                           or NaN when not labelled by an operator
agreement_score     float  share of strategies within ±30 px of chosen
container_count     int    2 (always for v1's dual-container splitter)
scanner_type        str    'ASE' / 'FS6000' / '' — strat slice for eval
captured_at         str    ISO-8601 from v1 ``created_at``
fetch_status        str    'http_ok' | 'http_fail' | 'db_ok' | 'synthetic'
==================  =====  ==============================================

Flags
-----
``--csv``                 v1 export CSV (default: data/labels.csv)
``--data-root``           output dir (default: data/)
``--source``              ``http`` (default), ``db``, or ``synthetic``
``--splitter-base-url``   default ``http://127.0.0.1:5320``
``--limit``               cap rows for quick iteration
``--max-fetch-failures``  abort if more than N HTTP fetches fail
                          (default 999 — never abort, just record)
``--synthetic-fallback``  when ``--source http`` fails, fill the slot
                          with a black tensor + ``fetch_status=synthetic``
                          rather than dropping the row. Default off.

Usage
-----
::

    "C:/Shared/ERP V2/tools/inference-bringup/.venv/Scripts/python.exe" \\
        prepare_data.py \\
        --csv data/labels.csv \\
        --data-root data \\
        --source http \\
        --splitter-base-url http://127.0.0.1:5320

v1 read-only guarantee
----------------------
HTTP GETs only. No POST/PUT/DELETE issued. DB fallback uses the same
``set_session(readonly=True, autocommit=True)`` posture as
``export_splits.py`` and only touches ``image_split_jobs.image_data``.

Honest note
-----------
With ~88 jobs / 21 ground-truth labels in the v1 corpus (round-3
2026-04-29 export), the trained student will be heavily overfit. This
script is the data-volume floor; nothing here magics more labels into
existence.
"""
from __future__ import annotations

import argparse
import csv
import io
import json
import logging
import os
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

import numpy as np
import pandas as pd
import pyarrow as pa
import pyarrow.parquet as pq
import requests
from PIL import Image

INPUT_HEIGHT = 472
INPUT_WIDTH = 1568
TOP_CROP_FRACTION = 0.25
PERCENTILE_LO = 0.5
PERCENTILE_HI = 99.5

logger = logging.getLogger("prepare-data")


# ── data row ───────────────────────────────────────────────────────────
@dataclass
class CsvRow:
    scan_id: str
    original_width: int
    original_height: int
    split_x_chosen: int
    ground_truth_split_x: Optional[int]
    agreement_score: Optional[float]
    container_count: int
    scanner_type: str
    captured_at: str


def _load_csv(csv_path: Path) -> list[CsvRow]:
    if not csv_path.exists():
        raise FileNotFoundError(f"label CSV not found: {csv_path}")

    out: list[CsvRow] = []
    with csv_path.open("r", encoding="utf-8", newline="") as fh:
        for r in csv.DictReader(fh):
            try:
                w = int(r["image_width"]) if r["image_width"] else 0
                h = int(r["image_height"]) if r["image_height"] else 0
                chosen = int(r["split_xs_chosen"]) if r["split_xs_chosen"] else None
                gt = int(r["ground_truth_split_x"]) if r["ground_truth_split_x"] else None
                agree = float(r["agreement_score"]) if r["agreement_score"] else None
                cc = int(r["container_count"]) if r["container_count"] else 2
            except ValueError:
                logger.warning("skipping malformed row scan_id=%s", r.get("scan_id"))
                continue
            if chosen is None or w <= 0 or h <= 0:
                continue
            out.append(
                CsvRow(
                    scan_id=r["scan_id"],
                    original_width=w,
                    original_height=h,
                    split_x_chosen=chosen,
                    ground_truth_split_x=gt,
                    agreement_score=agree,
                    container_count=cc,
                    scanner_type=r.get("scanner_type", "") or "",
                    captured_at=r.get("captured_at", "") or "",
                )
            )
    return out


# ── image fetch ────────────────────────────────────────────────────────
def _fetch_http(scan_id: str, base_url: str, session: requests.Session, timeout: float) -> bytes:
    url = f"{base_url.rstrip('/')}/api/split/{scan_id}/original"
    r = session.get(url, timeout=timeout)
    r.raise_for_status()
    return r.content


def _fetch_db(scan_id: str, conn) -> bytes:
    cur = conn.cursor()
    cur.execute("SELECT image_data FROM image_split_jobs WHERE id = %s", (scan_id,))
    row = cur.fetchone()
    cur.close()
    if not row or not row[0]:
        raise RuntimeError(f"no image_data for scan_id={scan_id}")
    raw = row[0]
    if isinstance(raw, memoryview):
        raw = bytes(raw)
    return bytes(raw)


def _connect_db_readonly():
    import psycopg2  # type: ignore
    conn = psycopg2.connect(
        host=os.environ.get("NICKSCAN_DB_HOST", "localhost"),
        dbname="nickscan_production",
        user=os.environ.get("NICKSCAN_DB_USER", "postgres"),
        password=os.environ.get("NICKSCAN_DB_PASSWORD", ""),
        port=int(os.environ.get("NICKSCAN_DB_PORT", "5432")),
    )
    conn.set_session(readonly=True, autocommit=True)
    return conn


# ── §3.2 preprocessing ─────────────────────────────────────────────────
def _decode_to_grayscale(raw: bytes) -> np.ndarray:
    img = Image.open(io.BytesIO(raw))
    img.load()
    if img.mode != "L":
        # Match v1 fs6000.py averaging — channel mean to single-channel.
        img = img.convert("RGB")
        arr = np.asarray(img, dtype=np.float32) / 255.0
        gray = arr.mean(axis=2)
    else:
        gray = np.asarray(img, dtype=np.float32) / 255.0
    return gray  # (H, W) float32 in [0, 1]


def _top_crop(arr: np.ndarray, fraction: float = TOP_CROP_FRACTION) -> np.ndarray:
    h = arr.shape[0]
    crop_h = max(1, int(round(h * fraction)))
    return arr[:crop_h, :]


def _resize_long_edge_to(arr: np.ndarray, target_long: int) -> np.ndarray:
    h, w = arr.shape[:2]
    long_edge = max(h, w)
    scale = target_long / float(long_edge)
    new_w = max(1, int(round(w * scale)))
    new_h = max(1, int(round(h * scale)))
    img = Image.fromarray((arr * 255.0).clip(0, 255).astype(np.uint8))
    img = img.resize((new_w, new_h), resample=Image.BILINEAR)
    return np.asarray(img, dtype=np.float32) / 255.0


def _pad_to(arr: np.ndarray, target_h: int, target_w: int) -> np.ndarray:
    h, w = arr.shape[:2]
    out = np.zeros((target_h, target_w), dtype=np.float32)
    h_ = min(h, target_h)
    w_ = min(w, target_w)
    out[:h_, :w_] = arr[:h_, :w_]
    return out


def _percentile_stretch(arr: np.ndarray, lo_pct: float, hi_pct: float) -> np.ndarray:
    lo = np.percentile(arr, lo_pct)
    hi = np.percentile(arr, hi_pct)
    if hi - lo < 1e-6:
        return np.zeros_like(arr)
    out = (arr - lo) / (hi - lo)
    return np.clip(out, 0.0, 1.0).astype(np.float32)


def preprocess(raw_bytes: bytes) -> np.ndarray:
    """Apply the §3.2 chain: decode → top-25 %-crop → long-edge 1568 →
    pad to (472, 1568) → percentile stretch 0.5–99.5. Returns
    ``(1, 472, 1568)`` float32 in [0, 1]."""
    gray = _decode_to_grayscale(raw_bytes)
    cropped = _top_crop(gray, TOP_CROP_FRACTION)
    resized = _resize_long_edge_to(cropped, INPUT_WIDTH)
    padded = _pad_to(resized, INPUT_HEIGHT, INPUT_WIDTH)
    stretched = _percentile_stretch(padded, PERCENTILE_LO, PERCENTILE_HI)
    return stretched[np.newaxis, ...]  # (1, H, W)


def map_x(orig_x: int, original_width: int) -> float:
    """Map a label X from the original-resolution scan into the resized
    1568-wide canvas. Uses the same long-edge scale as the image so the
    label tracks pixel positions exactly."""
    if original_width <= 0:
        return float("nan")
    # The crop is height-only — width unchanged. So the long-edge resize
    # scale is target_long / max(top_crop_height, original_width).
    crop_h = max(1, int(round(0.25 * original_width)))  # heuristic upper bound
    # The actual crop height varies; we use original_width as the long
    # edge for the typical FS6000/ASE 2295×1378 → 2295×344 → long edge
    # is the width. This holds whenever original_width >= top_crop_height.
    long_edge = max(crop_h, original_width)
    scale = INPUT_WIDTH / float(long_edge)
    return orig_x * scale


# ── main ───────────────────────────────────────────────────────────────
def main(argv: list[str] | None = None) -> int:
    here = Path(__file__).resolve().parent
    ap = argparse.ArgumentParser(
        description="§3.2 preprocessing for v2 container-split training.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument("--csv", default=str(here / "data" / "labels.csv"))
    ap.add_argument("--data-root", default=str(here / "data"))
    ap.add_argument(
        "--source",
        choices=("http", "db", "synthetic"),
        default="http",
        help="image source. 'http' uses the splitter API; "
             "'db' reads image_split_jobs.image_data; "
             "'synthetic' fills tensors with zeros to exercise the trainer.",
    )
    ap.add_argument("--splitter-base-url", default="http://127.0.0.1:5320")
    ap.add_argument("--http-timeout", type=float, default=10.0)
    ap.add_argument("--limit", type=int, default=None)
    ap.add_argument("--max-fetch-failures", type=int, default=999)
    ap.add_argument(
        "--synthetic-fallback",
        action="store_true",
        help="when --source http fails on a row, write a black tensor "
             "instead of dropping the row.",
    )
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args(argv)

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
    )

    csv_path = Path(args.csv)
    data_root = Path(args.data_root)
    images_dir = data_root / "images"
    images_dir.mkdir(parents=True, exist_ok=True)
    manifest_path = data_root / "manifest.parquet"

    rows = _load_csv(csv_path)
    logger.info("loaded %d label rows from %s", len(rows), csv_path)
    if args.limit:
        rows = rows[: args.limit]
        logger.info("--limit applied: keeping %d rows", len(rows))

    session = requests.Session() if args.source == "http" else None
    db_conn = _connect_db_readonly() if args.source == "db" else None

    records: list[dict] = []
    fail_count = 0
    for i, r in enumerate(rows):
        npy_rel = Path("images") / f"{r.scan_id}.npy"
        npy_abs = data_root / npy_rel
        status = "synthetic"
        try:
            if args.source == "http":
                raw = _fetch_http(r.scan_id, args.splitter_base_url, session, args.http_timeout)
                tensor = preprocess(raw)
                status = "http_ok"
            elif args.source == "db":
                raw = _fetch_db(r.scan_id, db_conn)
                tensor = preprocess(raw)
                status = "db_ok"
            else:  # synthetic
                tensor = np.zeros((1, INPUT_HEIGHT, INPUT_WIDTH), dtype=np.float32)
                status = "synthetic"
        except Exception as e:
            fail_count += 1
            logger.warning("[%d/%d] fetch/preprocess failed scan_id=%s : %s",
                           i + 1, len(rows), r.scan_id, e)
            if args.source == "http" and args.synthetic_fallback:
                tensor = np.zeros((1, INPUT_HEIGHT, INPUT_WIDTH), dtype=np.float32)
                status = "synthetic"
            else:
                if fail_count > args.max_fetch_failures:
                    logger.error("exceeded --max-fetch-failures=%d, aborting",
                                 args.max_fetch_failures)
                    return 5
                continue

        np.save(npy_abs, tensor)
        records.append(
            {
                "scan_id": r.scan_id,
                "npy_path": str(npy_rel).replace("\\", "/"),
                "input_height": INPUT_HEIGHT,
                "input_width": INPUT_WIDTH,
                "original_width": r.original_width,
                "original_height": r.original_height,
                "split_x_resized": map_x(r.split_x_chosen, r.original_width),
                "ground_truth_x_resized":
                    map_x(r.ground_truth_split_x, r.original_width)
                    if r.ground_truth_split_x is not None else float("nan"),
                "agreement_score": r.agreement_score if r.agreement_score is not None else float("nan"),
                "container_count": r.container_count,
                "scanner_type": r.scanner_type,
                "captured_at": r.captured_at,
                "fetch_status": status,
            }
        )

        if (i + 1) % 10 == 0 or (i + 1) == len(rows):
            logger.info("[%d/%d] processed (status=%s, failures=%d)",
                        i + 1, len(rows), status, fail_count)

    if db_conn is not None:
        db_conn.close()

    if not records:
        logger.error("no rows produced; manifest would be empty")
        return 6

    df = pd.DataFrame.from_records(records)
    table = pa.Table.from_pandas(df, preserve_index=False)
    pq.write_table(table, manifest_path)
    logger.info("wrote %d rows to %s", len(df), manifest_path)
    logger.info("fetch status counts: %s",
                df["fetch_status"].value_counts().to_dict())

    summary = {
        "manifest_path": str(manifest_path),
        "rows": len(df),
        "fetch_status_counts": df["fetch_status"].value_counts().to_dict(),
        "rows_with_ground_truth":
            int(df["ground_truth_x_resized"].notna().sum()),
        "scanner_type_counts": df["scanner_type"].value_counts().to_dict(),
    }
    summary_path = data_root / "prepare_data.summary.json"
    summary_path.write_text(json.dumps(summary, indent=2), encoding="utf-8")
    logger.info("summary written to %s", summary_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
