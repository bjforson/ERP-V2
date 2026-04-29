#!/usr/bin/env python3
"""
v1 → v2 splitter-label exporter
================================

Purpose
-------
Read-only extraction of container-split training labels from v1's NSCIM image-
splitter so the v2 ERP rebuild can train a local student model on the Claude
Sonnet 4.5 teacher's per-image picks plus the multi-strategy agreement
signals. v1 is **never** modified — this script only issues SELECTs against
``nickscan_production`` (or reads JSONL fallback) and writes a single CSV
manifest under v2's tree.

Background
----------
The v1 splitter (``C:\\Shared\\NSCIM_PRODUCTION\\services\\image-splitter\\``)
runs an 8-strategy consensus pipeline on every dual-container scan. Per
``models/database.py``::

    image_split_jobs        — one row per scan
        - claude_vision_split_x, claude_vision_confidence,
          claude_vision_model, claude_vision_ran_at
        - best_strategy, best_score, split_x  (the chosen answer)
        - ground_truth_split_x  (operator click-set, 1.20.0)
        - correct_split_x       (analyst feedback)
    image_split_results     — one row per (job, strategy) — 8 strategies
        - strategy_name, split_x, confidence, processing_ms
        - metadata JSONB (verifier_picked, verifier_reasoning,
          verifier_claude_confidence, agreeing_strategies, consensus_bonus,
          c1_right_peak_x, c2_left_peak_x, gap_width_px, ...)
    splitter_consensus_corpus — operator-verified ground-truth subset

Persistence is fully DB-backed. There are no JSONL sidecars on disk under
``logs/`` for split decisions (only stdout-style FastAPI logs in
``services/image-splitter/splitter*.log``). A JSONL fallback path is supported
for future use, but DB is the canonical source.

Output schema (CSV columns)
---------------------------
    scan_id                          : image_split_jobs.id (UUID)
    image_path                       : empty (v1 stores image bytes inline,
                                       not paths). Falls back to
                                       'db://image_split_jobs/{id}'.
    container_numbers                : "AAAU1234567, BBBU7654321"
    image_width, image_height        : pixels
    split_xs_chosen                  : best split x (single value today;
                                       semicolon-separated reserved for
                                       multi-split future)
    split_xs_per_strategy_json       : {"steel_wall_midpoint": 666,
                                        "claude_vision": 660, ...} ordered by
                                       confidence desc
    confidences_per_strategy_json    : {"claude_vision": 0.97, ...}
    claude_verifier_confidence       : verifier self-reported confidence
                                       (from metadata.verifier_claude_confidence)
    agreement_score                  : float in [0,1] — share of strategies
                                       within AGREEMENT_THRESHOLD_PX (30) of
                                       the chosen split, computed locally
    container_count                  : 2 (always for v1's 1.x dual-container
                                       splitter)
    scanner_serial                   : empty (v1 doesn't capture serial here;
                                       see caveats)
    scanner_type                     : 'ASE' / 'FS6000' / null
    captured_at                      : image_split_jobs.created_at (ISO 8601)
    teacher_model_version            : claude_vision_model column or
                                       --teacher-version-tag
    ground_truth_split_x             : operator click-set value if any
    best_strategy                    : the strategy whose split was chosen
    source_log_path                  : 'pg://nickscan_production/image_split_jobs'

Usage
-----
    # Everything completed in March/April 2026
    python export_splits.py \\
        --source pg://localhost/nickscan_production \\
        --out "C:/Shared/ERP V2/tools/v1-label-export/labels.csv" \\
        --since 2026-03-01 \\
        --teacher-version-tag claude-sonnet-4-5

    # Dry-run row count without writing
    python export_splits.py --source pg --out /tmp/x.csv --dry-run

    # Reads NICKSCAN_DB_PASSWORD from env (same as the splitter service).
    # User=postgres unless overridden.

Exit codes
----------
    0   success
    2   bad CLI args
    3   refusing to write under v1 tree (read-only invariant)
    4   psycopg2 missing (only required for DB source)
    5   DB connection / query error
    6   no rows matched the time window

Limitations / caveats
---------------------
- ``scanner_type`` is sparse: as of 2026-04-28, 40/89 jobs have it null,
  48 are ASE, 1 is FS6000. ``scanner_serial`` is not on the splitter table
  at all — joining to ``crossrecordscans`` for it is out of scope here.
- ``image_path`` is empty: v1 stores PNG/JPEG bytes inline in
  ``image_split_jobs.image_data`` (LargeBinary). The student-trainer can
  fetch by scan_id via ``db://image_split_jobs/{id}`` (the splitter exposes
  ``GET /api/split/{job_id}/original``).
- ``teacher_model_version`` reflects what was set on the row at split time
  (``claude_vision_model``). Sonnet 4.5 has been the only teacher across the
  current corpus, so the column is uniform. Use ``--teacher-version-tag`` to
  stamp a more specific version (e.g. ``claude-sonnet-4-5-2025-09``) when
  Anthropic's exact build is known.
- ``agreement_score`` is computed here, not stored in v1. Definition: count
  of strategies (incl. chosen) with ``|split_x - chosen_split_x| <= 30``,
  divided by total strategies that produced a result. Matches the
  AGREEMENT_THRESHOLD_PX = 30 the orchestrator uses.
- ``container_count`` is hard-coded to 2: v1's splitter only handles the
  dual-container case. Triple+ scans would require a different schema.
- ``--since`` / ``--until`` filter on ``created_at``, not the (mostly null)
  ``claude_vision_ran_at``.
- The script is idempotent: same ``--source`` + window always produces the
  same CSV (full rewrite, no append, deterministic ordering by ``created_at,
  id``).

Read-only guarantees (verified by code inspection of v1 on 2026-04-28)
---------------------------------------------------------------------
- Only ``SELECT`` statements are issued. ``psycopg2.connect`` is opened
  read-only via ``set_session(readonly=True, autocommit=True)``.
- ``--out`` is rejected if it resolves under ``C:\\Shared\\NSCIM_PRODUCTION\\``
  (case-insensitive prefix match after normalisation).
- No file is written under v1's tree, ever. CSV target must be elsewhere.
"""
from __future__ import annotations

import argparse
import csv
import json
import logging
import os
import sys
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Tuple
from urllib.parse import urlparse

# ── constants ──────────────────────────────────────────────────────────
V1_FORBIDDEN_PREFIX = r"c:\shared\nscim_production"
AGREEMENT_THRESHOLD_PX = 30  # matches v1 orchestrator AGREEMENT_THRESHOLD_PX

EXIT_OK = 0
EXIT_BAD_ARGS = 2
EXIT_FORBIDDEN_OUT = 3
EXIT_NO_PSYCOPG2 = 4
EXIT_DB_ERROR = 5
EXIT_NO_ROWS = 6

CSV_FIELDS = [
    "scan_id",
    "image_path",
    "container_numbers",
    "image_width",
    "image_height",
    "split_xs_chosen",
    "split_xs_per_strategy_json",
    "confidences_per_strategy_json",
    "claude_verifier_confidence",
    "agreement_score",
    "container_count",
    "scanner_serial",
    "scanner_type",
    "captured_at",
    "teacher_model_version",
    "ground_truth_split_x",
    "best_strategy",
    "source_log_path",
]

logger = logging.getLogger("v1-label-export")


# ── data shape ─────────────────────────────────────────────────────────
@dataclass
class StrategyRow:
    name: str
    split_x: int
    confidence: Optional[float]
    metadata: Dict[str, Any] = field(default_factory=dict)


@dataclass
class JobRow:
    id: str
    container_numbers: str
    scanner_type: Optional[str]
    image_width: Optional[int]
    image_height: Optional[int]
    best_strategy: Optional[str]
    best_score: Optional[float]
    split_x: Optional[int]
    ground_truth_split_x: Optional[int]
    claude_vision_model: Optional[str]
    created_at: Optional[datetime]
    strategies: List[StrategyRow] = field(default_factory=list)


# ── safety: refuse to write under v1 tree ─────────────────────────────
def assert_out_not_in_v1(out_path: Path) -> None:
    norm = str(out_path.resolve()).lower().replace("/", "\\")
    if norm.startswith(V1_FORBIDDEN_PREFIX):
        sys.stderr.write(
            f"FATAL: --out {out_path} is under v1's tree "
            f"({V1_FORBIDDEN_PREFIX}). v1 is read-only during v2 dev. "
            "Write the CSV under v2 (e.g. C:/Shared/ERP V2/tools/...) or /tmp.\n"
        )
        sys.exit(EXIT_FORBIDDEN_OUT)


# ── DB source ──────────────────────────────────────────────────────────
def _parse_source(source: str) -> Tuple[str, Dict[str, Any]]:
    """Return ('pg', conn_kwargs) or ('jsonl', {'path': ...})."""
    s = source.strip()
    if s == "pg" or s == "postgres":
        return "pg", {
            "host": "localhost",
            "dbname": "nickscan_production",
            "user": "postgres",
            "password": os.environ.get("NICKSCAN_DB_PASSWORD", ""),
            "port": 5432,
        }
    if s.startswith("pg://") or s.startswith("postgres://") or s.startswith("postgresql://"):
        u = urlparse(s.replace("pg://", "postgres://", 1))
        kw: Dict[str, Any] = {
            "host": u.hostname or "localhost",
            "dbname": (u.path or "/nickscan_production").lstrip("/"),
            "user": u.username or "postgres",
            "password": u.password or os.environ.get("NICKSCAN_DB_PASSWORD", ""),
            "port": u.port or 5432,
        }
        return "pg", kw
    if s.endswith(".jsonl") or os.path.isdir(s):
        return "jsonl", {"path": s}
    # Default: treat as DB name on localhost
    return "pg", {
        "host": "localhost",
        "dbname": s,
        "user": "postgres",
        "password": os.environ.get("NICKSCAN_DB_PASSWORD", ""),
        "port": 5432,
    }


def _connect_pg_readonly(conn_kw: Dict[str, Any]):
    try:
        import psycopg2  # type: ignore
        from psycopg2.extras import RealDictCursor  # noqa: F401
    except ImportError:
        sys.stderr.write(
            "FATAL: psycopg2 is required for DB sources. Install via:\n"
            "    pip install psycopg2-binary\n"
            "Or run inside the splitter's venv:\n"
            "    \"C:/Shared/NSCIM_PRODUCTION/services/image-splitter/venv/Scripts/python.exe\" "
            "export_splits.py ...\n"
        )
        sys.exit(EXIT_NO_PSYCOPG2)
    try:
        conn = psycopg2.connect(**conn_kw)
        # Read-only session: any UPDATE/INSERT/DELETE will be rejected by Postgres
        conn.set_session(readonly=True, autocommit=True)
        return conn
    except Exception as e:
        sys.stderr.write(f"FATAL: could not connect to {conn_kw.get('dbname')}: {e}\n")
        sys.exit(EXIT_DB_ERROR)


def fetch_pg_jobs(
    conn_kw: Dict[str, Any],
    since: Optional[datetime],
    until: Optional[datetime],
) -> List[JobRow]:
    conn = _connect_pg_readonly(conn_kw)
    try:
        from psycopg2.extras import RealDictCursor  # type: ignore
    except ImportError:
        sys.exit(EXIT_NO_PSYCOPG2)

    sql_jobs = """
        SELECT id, container_numbers, scanner_type, image_width, image_height,
               best_strategy, best_score, split_x, ground_truth_split_x,
               claude_vision_model, created_at
        FROM image_split_jobs
        WHERE status = 'completed'
          AND split_x IS NOT NULL
          AND (%s::timestamptz IS NULL OR created_at >= %s)
          AND (%s::timestamptz IS NULL OR created_at <  %s)
        ORDER BY created_at, id
    """
    sql_results = """
        SELECT job_id, strategy_name, split_x, confidence, metadata
        FROM image_split_results
        WHERE job_id = ANY(%s::uuid[])
        ORDER BY confidence DESC NULLS LAST, strategy_name
    """

    cur = conn.cursor(cursor_factory=RealDictCursor)
    try:
        cur.execute(sql_jobs, (since, since, until, until))
        job_rows = cur.fetchall()
        if not job_rows:
            return []

        ids = [str(r["id"]) for r in job_rows]
        cur.execute(sql_results, (ids,))
        result_rows = cur.fetchall()
    except Exception as e:
        sys.stderr.write(f"FATAL: query failed: {e}\n")
        sys.exit(EXIT_DB_ERROR)
    finally:
        cur.close()
        conn.close()

    by_id: Dict[str, JobRow] = {}
    for r in job_rows:
        jid = str(r["id"])
        by_id[jid] = JobRow(
            id=jid,
            container_numbers=r["container_numbers"] or "",
            scanner_type=r["scanner_type"],
            image_width=r["image_width"],
            image_height=r["image_height"],
            best_strategy=r["best_strategy"],
            best_score=float(r["best_score"]) if r["best_score"] is not None else None,
            split_x=r["split_x"],
            ground_truth_split_x=r["ground_truth_split_x"],
            claude_vision_model=r["claude_vision_model"],
            created_at=r["created_at"],
        )
    for rr in result_rows:
        jid = str(rr["job_id"])
        if jid not in by_id:
            continue
        meta = rr["metadata"] if isinstance(rr["metadata"], dict) else {}
        by_id[jid].strategies.append(
            StrategyRow(
                name=rr["strategy_name"],
                split_x=rr["split_x"],
                confidence=float(rr["confidence"]) if rr["confidence"] is not None else None,
                metadata=meta or {},
            )
        )
    return list(by_id.values())


# ── JSONL fallback (forward-compatible; not exercised today) ──────────
def fetch_jsonl_jobs(path: str, since: Optional[datetime], until: Optional[datetime]) -> List[JobRow]:
    p = Path(path)
    files = [p] if p.is_file() else sorted(p.glob("*.jsonl"))
    out: Dict[str, JobRow] = {}
    for fp in files:
        with fp.open("r", encoding="utf-8") as fh:
            for ln, line in enumerate(fh, 1):
                line = line.strip()
                if not line:
                    continue
                try:
                    rec = json.loads(line)
                except json.JSONDecodeError as e:
                    logger.warning("skipping %s:%d — %s", fp, ln, e)
                    continue
                created = rec.get("created_at")
                ts = _parse_iso(created) if created else None
                if ts and since and ts < since:
                    continue
                if ts and until and ts >= until:
                    continue
                jid = str(rec.get("id") or rec.get("scan_id") or rec.get("job_id") or "")
                if not jid:
                    continue
                jr = out.setdefault(
                    jid,
                    JobRow(
                        id=jid,
                        container_numbers=rec.get("container_numbers", ""),
                        scanner_type=rec.get("scanner_type"),
                        image_width=rec.get("image_width"),
                        image_height=rec.get("image_height"),
                        best_strategy=rec.get("best_strategy"),
                        best_score=rec.get("best_score"),
                        split_x=rec.get("split_x"),
                        ground_truth_split_x=rec.get("ground_truth_split_x"),
                        claude_vision_model=rec.get("claude_vision_model"),
                        created_at=ts,
                    ),
                )
                for s in rec.get("strategies", []) or []:
                    jr.strategies.append(
                        StrategyRow(
                            name=s.get("strategy_name") or s.get("name", ""),
                            split_x=int(s.get("split_x", 0)),
                            confidence=s.get("confidence"),
                            metadata=s.get("metadata") or {},
                        )
                    )
    return list(out.values())


def _parse_iso(s: str) -> Optional[datetime]:
    try:
        return datetime.fromisoformat(s.replace("Z", "+00:00"))
    except (ValueError, AttributeError):
        return None


# ── transform: JobRow → CSV dict ──────────────────────────────────────
def _agreement_score(chosen_x: Optional[int], strategies: List[StrategyRow]) -> Optional[float]:
    if chosen_x is None or not strategies:
        return None
    total = len(strategies)
    if total == 0:
        return None
    near = sum(1 for s in strategies if abs(s.split_x - chosen_x) <= AGREEMENT_THRESHOLD_PX)
    return round(near / total, 4)


def _verifier_confidence(strategies: List[StrategyRow]) -> Optional[float]:
    # The verifier's self-reported confidence is stamped on whichever strategy
    # got picked (verifier_picked=true). All non-picks carry verifier_ranking
    # but not verifier_claude_confidence.
    for s in strategies:
        if s.metadata.get("verifier_picked") is True:
            v = s.metadata.get("verifier_claude_confidence")
            if v is not None:
                try:
                    return float(v)
                except (TypeError, ValueError):
                    return None
    return None


def to_row(jr: JobRow, teacher_tag: str, source_label: str) -> Dict[str, Any]:
    chosen = jr.split_x
    strat_x = {s.name: s.split_x for s in jr.strategies}
    strat_conf = {
        s.name: round(s.confidence, 4) if s.confidence is not None else None
        for s in jr.strategies
    }
    teacher = jr.claude_vision_model or teacher_tag or "unknown"
    captured_at = jr.created_at.isoformat() if jr.created_at else ""
    return {
        "scan_id": jr.id,
        "image_path": "",  # v1 stores image bytes inline; see docstring
        "container_numbers": jr.container_numbers,
        "image_width": jr.image_width if jr.image_width is not None else "",
        "image_height": jr.image_height if jr.image_height is not None else "",
        "split_xs_chosen": str(chosen) if chosen is not None else "",
        "split_xs_per_strategy_json": json.dumps(strat_x, sort_keys=True, separators=(",", ":")),
        "confidences_per_strategy_json": json.dumps(strat_conf, sort_keys=True, separators=(",", ":")),
        "claude_verifier_confidence": _verifier_confidence(jr.strategies) or "",
        "agreement_score": _agreement_score(chosen, jr.strategies) or "",
        "container_count": 2,
        "scanner_serial": "",  # not on splitter tables; see caveats
        "scanner_type": jr.scanner_type or "",
        "captured_at": captured_at,
        "teacher_model_version": teacher,
        "ground_truth_split_x": jr.ground_truth_split_x if jr.ground_truth_split_x is not None else "",
        "best_strategy": jr.best_strategy or "",
        "source_log_path": source_label,
    }


# ── CLI / main ─────────────────────────────────────────────────────────
def _parse_date(s: Optional[str]) -> Optional[datetime]:
    if not s:
        return None
    dt = _parse_iso(s)
    if dt is None:
        sys.stderr.write(f"FATAL: --since/--until value {s!r} is not ISO-8601.\n")
        sys.exit(EXIT_BAD_ARGS)
    return dt


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(
        prog="export_splits",
        description="Export v1 NSCIM splitter labels to a CSV for v2 student-model training.",
    )
    ap.add_argument(
        "--source",
        required=True,
        help="DB conn or 'pg' for default localhost/nickscan_production. "
             "Also accepts 'pg://user:pw@host:port/db' or a JSONL file/dir.",
    )
    ap.add_argument("--out", required=True, help="CSV output path. Refuses paths under v1's tree.")
    ap.add_argument("--since", default=None, help="ISO-8601 inclusive lower bound on created_at")
    ap.add_argument("--until", default=None, help="ISO-8601 exclusive upper bound on created_at")
    ap.add_argument(
        "--teacher-version-tag",
        default="claude-sonnet-4-5",
        help="String stamped on every row when claude_vision_model is null. Default: claude-sonnet-4-5",
    )
    ap.add_argument("--dry-run", action="store_true", help="Count rows; do not write CSV")
    ap.add_argument("--verbose", action="store_true", help="DEBUG-level logging")
    args = ap.parse_args(argv)

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
    )

    out_path = Path(args.out)
    assert_out_not_in_v1(out_path)

    since = _parse_date(args.since)
    until = _parse_date(args.until)
    src_kind, src_kw = _parse_source(args.source)

    if src_kind == "pg":
        source_label = f"pg://{src_kw.get('host')}/{src_kw.get('dbname')}/image_split_jobs"
        logger.info("Source: %s  window: [%s .. %s)", source_label, since or "-inf", until or "+inf")
        jobs = fetch_pg_jobs(src_kw, since, until)
    else:
        source_label = f"jsonl://{src_kw['path']}"
        logger.info("Source: %s  window: [%s .. %s)", source_label, since or "-inf", until or "+inf")
        jobs = fetch_jsonl_jobs(src_kw["path"], since, until)

    if not jobs:
        sys.stderr.write("No rows matched the time window.\n")
        return EXIT_NO_ROWS

    logger.info(
        "Matched %d completed jobs / %d strategy rows.",
        len(jobs),
        sum(len(j.strategies) for j in jobs),
    )

    if args.dry_run:
        logger.info("--dry-run: not writing CSV. Would write %d rows to %s.", len(jobs), out_path)
        return EXIT_OK

    out_path.parent.mkdir(parents=True, exist_ok=True)
    # Idempotent full rewrite — no append.
    tmp = out_path.with_suffix(out_path.suffix + ".tmp")
    with tmp.open("w", encoding="utf-8", newline="") as fh:
        w = csv.DictWriter(fh, fieldnames=CSV_FIELDS, quoting=csv.QUOTE_MINIMAL)
        w.writeheader()
        # Deterministic order: by created_at then id (already from DB).
        for jr in sorted(jobs, key=lambda j: (j.created_at or datetime.min, j.id)):
            w.writerow(to_row(jr, args.teacher_version_tag, source_label))
    os.replace(tmp, out_path)
    logger.info("Wrote %d rows to %s", len(jobs), out_path)
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
