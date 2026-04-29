#!/usr/bin/env python3
"""
v1 → v2 plate-OCR harvester
============================

Purpose
-------
Read-only extraction of container-plate ROIs from v1's ``nickscan_production``
Postgres for training the v2 container-OCR student per §6.1.4 of
``docs/IMAGE-ANALYSIS-MODERNIZATION.md``. v1 is **never** modified — this
script only issues SELECTs against ``fs6000images`` (joined to
``containernumbercorrections`` for analyst overrides) and writes a CSV
manifest under v2's tree. v1 stores image bytes inline; the manifest carries
a stable URI (``db://fs6000images/{id}``) the trainer can resolve via the
v1 read-only API on the lane PC.

Sibling pattern of ``tools/v1-label-export/export_splits.py``: same
read-only invariant, same env-var convention (NICKSCAN_DB_PASSWORD), same
exit-code numbering, same forbidden-out-path guard.

Output schema (CSV columns)
---------------------------
    image_id           : fs6000images.id (UUID)
    image_uri          : 'db://fs6000images/{id}' (v1 stores bytes inline)
    image_type         : fs6000images.imagetype (e.g. 'top', 'side1', 'side2')
    v1_predicted       : fs6000scans.containernumber — the value v1 OCR
                         (Tesseract) recorded against this scan, or empty
                         when the scan was never OCR'd
    analyst_corrected  : containerannotations.text where type='ocr_correction'
                         (gold label; empty when no correction exists)
    captured_at        : fs6000scans.scantime (ISO 8601 UTC), or
                         fs6000images.createdat as a fallback
    truck_plate        : fs6000scans.truckplate (rare on FS6000 corpus)
    file_path          : fs6000scans.filepath (provenance-only; bytes are inline)
    scan_id            : fs6000images.scanid (FK back to fs6000scans)
    tenant_id          : fs6000images.tenant_id (multi-tenancy attribution)
    source_table       : 'fs6000images JOIN fs6000scans' (literal — ETL provenance)

Usage
-----
::

    python harvest_plates.py \\
        --source pg://localhost/nickscan_production \\
        --out "C:/Shared/ERP V2/tools/inference-training/container-ocr/labels.csv" \\
        --since 2026-01-01

    python harvest_plates.py --source pg --out /tmp/x.csv --dry-run

    # Reads NICKSCAN_DB_PASSWORD from env (same as the splitter service).

Exit codes
----------
    0   success
    2   bad CLI args
    3   refusing to write under v1 tree (read-only invariant)
    4   psycopg2 missing (only required for DB source)
    5   DB connection / query error
    6   no rows matched the time window

Read-only guarantees
--------------------
- Only ``SELECT`` statements are issued.
- ``psycopg2.connect`` is opened with ``set_session(readonly=True,
  autocommit=True)``.
- ``--out`` is rejected if it resolves under
  ``C:\\Shared\\NSCIM_PRODUCTION\\`` (case-insensitive prefix match after
  normalisation).
- No file is written under v1's tree, ever.

Known caveats
-------------
- v1's column casing is lowercase per the ``reference_pg_lowercase_columns``
  memory: the column is ``createdat``, not ``created_at``.
- ``fs6000images`` does not carry a manifest_id; the BOE-key analog is
  ``declarationnumber``. We do not need either for OCR training.
- The ``containernumbercorrections`` table is the only authoritative source
  of analyst-confirmed labels. When a row is missing, the v1 Tesseract
  output is the *silver* label (per §6.1.4) and ``analyst_corrected`` is
  emitted empty.
- Image bytes are NOT downloaded by this script. The trainer fetches
  them at minibatch time via the v1 read-only HTTP endpoint (see §6.1.4
  "Harvesting from v1 without v1 edits"). This keeps the manifest small
  (one row per ROI, ~250 bytes) and avoids duplicating ~80 GB of binary
  data into the v2 tree.
"""
from __future__ import annotations

import argparse
import csv
import logging
import os
import sys
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple
from urllib.parse import urlparse

# ── constants ──────────────────────────────────────────────────────────
V1_FORBIDDEN_PREFIX = r"c:\shared\nscim_production"

EXIT_OK = 0
EXIT_BAD_ARGS = 2
EXIT_FORBIDDEN_OUT = 3
EXIT_NO_PSYCOPG2 = 4
EXIT_DB_ERROR = 5
EXIT_NO_ROWS = 6

CSV_FIELDS = [
    "image_id",
    "image_uri",
    "image_type",
    "v1_predicted",
    "analyst_corrected",
    "captured_at",
    "truck_plate",
    "file_path",
    "scan_id",
    "tenant_id",
    "source_table",
]

logger = logging.getLogger("v1-plate-harvest")


@dataclass
class PlateRow:
    image_id: str
    image_uri: str
    image_type: str
    v1_predicted: str
    analyst_corrected: str
    captured_at: Optional[datetime]
    truck_plate: str
    file_path: str
    scan_id: str
    tenant_id: Optional[int]


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


def _parse_source(source: str) -> Dict[str, Any]:
    s = source.strip()
    if s in ("pg", "postgres"):
        return {
            "host": "localhost",
            "dbname": "nickscan_production",
            "user": "postgres",
            "password": os.environ.get("NICKSCAN_DB_PASSWORD", ""),
            "port": 5432,
        }
    if s.startswith("pg://") or s.startswith("postgres://") or s.startswith("postgresql://"):
        u = urlparse(s.replace("pg://", "postgres://", 1))
        return {
            "host": u.hostname or "localhost",
            "dbname": (u.path or "/nickscan_production").lstrip("/"),
            "user": u.username or "postgres",
            "password": u.password or os.environ.get("NICKSCAN_DB_PASSWORD", ""),
            "port": u.port or 5432,
        }
    return {
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
        )
        sys.exit(EXIT_NO_PSYCOPG2)
    try:
        conn = psycopg2.connect(**conn_kw)
        conn.set_session(readonly=True, autocommit=True)
        return conn
    except Exception as e:
        sys.stderr.write(f"FATAL: could not connect to {conn_kw.get('dbname')}: {e}\n")
        sys.exit(EXIT_DB_ERROR)


# ── SQL ────────────────────────────────────────────────────────────────

# §6.1.4: read-only query against fs6000images JOINed to fs6000scans
# (which carries v1's container-number OCR output) and LEFT JOINed to
# containerannotations filtered for type='ocr_correction' (gold labels).
# v1 schema differs from §6.1.4's idealised "fs6000images.containernumber +
# containernumbercorrections" layout — see introspection results checked
# in 2026-04-29: containernumber lives on fs6000scans, corrections live on
# containerannotations with type='ocr_correction'. Lowercase column names
# per the reference_pg_lowercase_columns memory.
SQL_HARVEST = """
    SELECT
        i.id::text                              AS image_id,
        i.scanid::text                          AS scan_id,
        i.imagetype                             AS image_type,
        s.containernumber                       AS v1_predicted,
        a.text                                  AS analyst_corrected,
        s.scantime                              AS scan_time,
        i.createdat                             AS image_createdat,
        s.truckplate                            AS truck_plate,
        s.filepath                              AS file_path,
        i.tenant_id                             AS tenant_id
    FROM public.fs6000images i
    JOIN public.fs6000scans s
        ON s.id = i.scanid
    LEFT JOIN public.containerannotations a
        ON a.containernumber = s.containernumber
       AND a.type = 'ocr_correction'
       AND a.isdeleted IS NOT TRUE
    WHERE
        (%(since)s::timestamptz IS NULL OR COALESCE(s.scantime, i.createdat) >= %(since)s)
        AND (%(until)s::timestamptz IS NULL OR COALESCE(s.scantime, i.createdat) <  %(until)s)
    ORDER BY COALESCE(s.scantime, i.createdat), i.id
    LIMIT %(limit)s
"""

SQL_DRY_RUN_INTROSPECT = """
    SELECT
        i.id, i.scanid, i.imagetype,
        s.containernumber, a.text,
        s.scantime, i.createdat, s.truckplate, s.filepath, i.tenant_id
    FROM public.fs6000images i
    JOIN public.fs6000scans s ON s.id = i.scanid
    LEFT JOIN public.containerannotations a
        ON a.containernumber = s.containernumber
       AND a.type = 'ocr_correction'
       AND a.isdeleted IS NOT TRUE
    LIMIT 0
"""


def fetch_plate_rows(
    conn_kw: Dict[str, Any],
    since: Optional[datetime],
    until: Optional[datetime],
    limit: int,
) -> List[PlateRow]:
    conn = _connect_pg_readonly(conn_kw)
    try:
        from psycopg2.extras import RealDictCursor  # type: ignore
    except ImportError:
        sys.exit(EXIT_NO_PSYCOPG2)

    cur = conn.cursor(cursor_factory=RealDictCursor)
    rows: List[PlateRow] = []
    try:
        cur.execute(SQL_HARVEST, {"since": since, "until": until, "limit": limit})
        for r in cur.fetchall():
            rows.append(
                PlateRow(
                    image_id=str(r["image_id"]),
                    image_uri=f"db://fs6000images/{r['image_id']}",
                    image_type=(r["image_type"] or "").strip(),
                    v1_predicted=(r["v1_predicted"] or "").strip(),
                    analyst_corrected=(r["analyst_corrected"] or "").strip(),
                    # Prefer the scan's authoritative scantime; fall back to
                    # the image's createdat if scantime is null.
                    captured_at=r["scan_time"] or r["image_createdat"],
                    truck_plate=(r["truck_plate"] or "").strip(),
                    file_path=(r["file_path"] or "").strip(),
                    scan_id=str(r["scan_id"]) if r["scan_id"] else "",
                    tenant_id=r["tenant_id"],
                )
            )
    except Exception as e:
        sys.stderr.write(f"FATAL: query failed: {e}\n")
        sys.exit(EXIT_DB_ERROR)
    finally:
        cur.close()
        conn.close()
    return rows


def introspect_columns(conn_kw: Dict[str, Any]) -> Tuple[int, int, List[str]]:
    """Run ``LIMIT 0`` to validate the join graph; return (image_rows, scan_rows, column_names)."""
    conn = _connect_pg_readonly(conn_kw)
    cur = conn.cursor()
    try:
        cur.execute(SQL_DRY_RUN_INTROSPECT)
        cols = [d.name for d in (cur.description or [])]
        cur.execute(
            """
            SELECT relname, COALESCE(reltuples, 0)::bigint
            FROM pg_class
            WHERE relname IN ('fs6000images', 'fs6000scans')
            ORDER BY relname
            """
        )
        counts = {r[0]: int(r[1]) for r in cur.fetchall()}
        return counts.get("fs6000images", 0), counts.get("fs6000scans", 0), cols
    except Exception as e:
        sys.stderr.write(f"FATAL: introspect failed: {e}\n")
        sys.exit(EXIT_DB_ERROR)
    finally:
        cur.close()
        conn.close()


# ── CSV write ──────────────────────────────────────────────────────────
def to_row(p: PlateRow) -> Dict[str, Any]:
    return {
        "image_id": p.image_id,
        "image_uri": p.image_uri,
        "image_type": p.image_type,
        "v1_predicted": p.v1_predicted,
        "analyst_corrected": p.analyst_corrected,
        "captured_at": p.captured_at.isoformat() if p.captured_at else "",
        "truck_plate": p.truck_plate,
        "file_path": p.file_path,
        "scan_id": p.scan_id,
        "tenant_id": "" if p.tenant_id is None else p.tenant_id,
        "source_table": "fs6000images JOIN fs6000scans",
    }


def _parse_iso(s: Optional[str]) -> Optional[datetime]:
    if not s:
        return None
    try:
        return datetime.fromisoformat(s.replace("Z", "+00:00"))
    except (ValueError, AttributeError):
        sys.stderr.write(f"FATAL: --since/--until value {s!r} is not ISO-8601.\n")
        sys.exit(EXIT_BAD_ARGS)


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(
        prog="harvest_plates",
        description=(
            "Read-only export of container-plate ROIs from v1 nickscan_production "
            "for v2 container-OCR student-model training (§6.1.4)."
        ),
    )
    ap.add_argument(
        "--source",
        required=True,
        help="DB conn or 'pg' for default localhost/nickscan_production. "
             "Also accepts 'pg://user:pw@host:port/db'.",
    )
    ap.add_argument("--out", required=True, help="CSV output path. Refuses paths under v1's tree.")
    ap.add_argument("--since", default=None, help="ISO-8601 inclusive lower bound on createdat")
    ap.add_argument("--until", default=None, help="ISO-8601 exclusive upper bound on createdat")
    ap.add_argument("--limit", type=int, default=1_000_000, help="Hard row cap.")
    ap.add_argument("--dry-run", action="store_true", help="Run LIMIT 0 introspect only; print row estimate and column list.")
    ap.add_argument("--verbose", action="store_true", help="DEBUG-level logging")
    args = ap.parse_args(argv)

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
    )

    out_path = Path(args.out)
    assert_out_not_in_v1(out_path)

    since = _parse_iso(args.since)
    until = _parse_iso(args.until)
    src_kw = _parse_source(args.source)

    if args.dry_run:
        img_rows, scan_rows, cols = introspect_columns(src_kw)
        logger.info("LIMIT 0 introspect succeeded.")
        logger.info("approx rows in fs6000images (pg_class.reltuples) = %d", img_rows)
        logger.info("approx rows in fs6000scans   (pg_class.reltuples) = %d", scan_rows)
        logger.info("expected harvest row count ≈ %d (one row per fs6000images, after JOIN to fs6000scans)", img_rows)
        logger.info("column list (joined): %s", cols)
        return EXIT_OK

    rows = fetch_plate_rows(src_kw, since, until, args.limit)
    if not rows:
        sys.stderr.write("No rows matched the time window.\n")
        return EXIT_NO_ROWS

    logger.info("Matched %d plate rows.", len(rows))

    out_path.parent.mkdir(parents=True, exist_ok=True)
    tmp = out_path.with_suffix(out_path.suffix + ".tmp")
    with tmp.open("w", encoding="utf-8", newline="") as fh:
        w = csv.DictWriter(fh, fieldnames=CSV_FIELDS, quoting=csv.QUOTE_MINIMAL)
        w.writeheader()
        for p in rows:
            w.writerow(to_row(p))
    os.replace(tmp, out_path)
    logger.info("Wrote %d rows to %s", len(rows), out_path)
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
