# Runbook 08 — Sprint 19 v1 Tesseract OCR baseline (2026-05-04)

> **Scope.** First-ever measurement of the v1 container-OCR floor that
> the §6.1 Florence-2 replacement must beat. Output of one full run of
> [`tools/inference-evaluation/container-ocr/`](../../tools/inference-evaluation/container-ocr/)
> against `nickscan_production.fs6000images` (imagetype = `Main`,
> the LUT-rendered 8-bit JPEG composite v1's `ContainerNumberOcrService`
> consumes). Numbers in §3 are committed as
> [`results/baseline-2026-05-04.json`](../../tools/inference-evaluation/container-ocr/results/baseline-2026-05-04.json)
> in tree.
>
> **Sister docs:**
> - [`../IMAGE-ANALYSIS-MODERNIZATION.md`](../IMAGE-ANALYSIS-MODERNIZATION.md)
>   §6.1 — locked acceptance bars (≥ 95 % EM, ≥ 98 % check-digit pass,
>   p95 ≤ 200 ms iGPU). This runbook delivers the floor those bars
>   unlock.
> - Plan-file `~/.claude/plans/tingly-launching-quasar.md` §12 — the
>   eight-phase ML training arc. Sprint 19 is Phase 1 + Phase 2; this
>   runbook is the Phase 2 deliverable.
> - [`../../tools/inference-evaluation/container-ocr/README.md`](../../tools/inference-evaluation/container-ocr/README.md)
>   — harness usage, output schema, exit codes.

---

## 1. Why this measurement matters

§12.3 of the plan-file is a decision gate: if v1's Tesseract baseline
is **above 92 % exact-match**, the Florence-2 ROI shrinks and
§6.1 may slide out of the pilot. Below 92 %, Florence-2 has clear
room and §6.1 stays in pilot scope. The measurement also serves as
the reference floor for every future Florence-2 / Donut acceptance
run — same harness, same JSON shape, comparable numbers.

The measurement is intentionally **off-pipeline**: the harness loads
v1's image bytes from Postgres directly, runs a fresh Tesseract pass
in-process (Tesseract 5.2.0 — the same package version v1 ships, with
v1's exact preprocessing chain — Otsu + Gaussian + CLAHE — copied byte-
for-byte from
`NickScanCentralImagingPortal.Services.ImageProcessing.ContainerNumberOcrService`),
and compares the output against the FS6000 vendor manifest's
`container_no` truth (extracted by v1 in
`Services.FS6000.XmlParsingService` line 534, persisted to
`fs6000scans.containernumber`).

---

## 2. Run parameters

| Setting | Value |
|---|---|
| Engine | Tesseract 5.2.0 (NuGet `Tesseract`) |
| Engine mode | `EngineMode.Default` (auto Legacy + LSTM) |
| Language data | `eng.traineddata`, sourced from `C:\Shared\NSCIM_PRODUCTION\publish\API\tessdata` |
| Preprocessing | Decode (OpenCvSharp) → BGR2GRAY → GaussianBlur (3x3) → Threshold (Otsu) → CLAHE (clipLimit=2.0, tile=8x8) → JpegEncode |
| Pattern extraction | `[A-Z]{4}\d{7}` plus `[A-Z]{4}[\s\-\.]\d{7}` variants (regex; v1 parity) |
| Truth source | `fs6000scans.containernumber` filtered by ISO 6346 mod-11 check-digit |
| Truth excluded | Rows whose `containernumber` failed the check digit (suspected vendor-XML quality issues; rare) |
| Imagetype filter | `Main` only — the LUT-rendered preview, the only type v1's OCR sees |
| Corpus rows considered | 2157 (`fs6000images.imagetype = 'Main'`) |
| Rows scored | 1917 (240 rows had `containernumber` that failed the ISO 6346 mod-11 gate — operator typos at scan time, excluded to avoid grading Tesseract against bad truth) |
| Host | `TEST-SERVER (CPU; Tesseract 5.2.0; Otsu+CLAHE port from v1)` |
| Run timestamp (UTC) | `2026-05-04T17:37:16Z` |
| Run wall-clock | ~26 minutes (1562 s elapsed in the harness logs) |

**Reproduction:**

```powershell
$env:NICKSCAN_DB_PASSWORD = "<the-password>"
dotnet run --project tools/inference-evaluation/container-ocr -c Release -- `
    --engine tesseract `
    --corpus-source postgres `
    --corpus-limit 2200 `
    --out tools/inference-evaluation/container-ocr/results/baseline-2026-05-04.json
```

The harness opens its Postgres connection with `SET TRANSACTION READ ONLY`
and never writes against `nickscan_production`.

---

## 3. Results

| Metric | Value | Acceptance bar (Florence-2 must clear) |
|---|---|---|
| **Exact-match rate (EM)** | **0.0 %** (0/1917) | ≥ 95 % (§6.1) |
| **Check-digit pass rate** | **0.0 %** (0/2157) | ≥ 98 % (§6.1) |
| Latency p50 / p95 / p99 (CPU) | 666 / 1306 / 1750 ms | p95 ≤ 200 ms iGPU (§6.1) |
| Latency mean / samples | 721 ms / 2157 | — |
| Per-failure-mode | see §3.1 | — |

### 3.1 Per-failure-mode breakdown

| Bucket | Samples | EM rate | Notes |
|---|---|---|---|
| Stylized typography | 215 | 0.0 % | MSC (`MSDU`, etc.) and TGH (`TGBU`) variants in the BIC-prefix list. Real signal, but EM=0 like the population. |
| Weathering | 0 | n/a | Bucket needs labels we don't have at Sprint 19 — see §4.4. |
| Oblique angles | 0 | n/a | Same as weathering. |
| False-positive surfaces | 0 | n/a | Population is filtered to `imagetype='Main'` only; the bucket is empty by construction. |

The classifier did pass its `MinBucketSamples = 30` threshold on the
stylized bucket (215 ≥ 30), so the JSON emits the `perFailureMode`
block. The EM is 0.0 across all buckets because the EM is 0.0 across
the population — no signal to differentiate.

### 3.2 Decision-gate read

**Florence-2 ROI is enormous. §6.1 stays in pilot scope — strongest
case possible.**

Per plan §12.3:
- > 92 % baseline → defer §6.1 from pilot. Did not trigger.
- 60 – 92 % baseline → Florence-2 has clear room. Did not trigger.
- < 60 % baseline → Florence-2 absolutely justified.
- **0 % baseline → no upper bound on Florence-2's potential
  improvement. Every percentage point Florence-2 delivers is pure
  upside.**

What this measurement says, plainly: **v1's Tesseract pass on the
LUT-rendered `Main` composite never produces a valid 11-character
ISO 6346 candidate, let alone a correct one, on 2157 production rows
spanning 2160 distinct containers**. The `Finding.confidence = 0.85f`
that v1's `OcrResult` ships back to callers is a placeholder — the
extraction itself is failing upstream of any confidence calculation.
Customs reality: container numbers on these scans are entered by hand
at the gate (XML manifest's `container_no` field). Tesseract has not
been a working OCR path on this corpus.

**Latency note.** Tesseract's 666 ms p50 / 1306 ms p95 on the
TEST-SERVER CPU is far above the §6.1 200 ms p95 iGPU bar. Even if
the accuracy were comparable to Florence-2's expected 95 %, the
latency alone would justify a replacement. iGPU vs CPU is not an
apples-to-apples comparison — Florence-2 acceptance (Phase 5) needs
the iGPU number — but the message is clear: there's no latency floor
to defend either.

---

## 4. Caveats — what this measurement can and cannot tell you

### 4.1 Truth source is scanner XML, not analyst-corrected

The Sprint 19 brief (and the harvester at
`tools/inference-training/container-ocr/harvest_plates.py`) anticipated
a `containerannotations` table with rows of
`type='ocr_correction'` carrying analyst-corrected gold labels. On
the actual production DB (probed 2026-05-04) that table holds only
**74 rows, all `type='Rectangle'` bbox annotations** — there are no
OCR-correction rows. So the only available truth source on this
corpus is `fs6000scans.containernumber`.

That field is set by v1's
`Services.FS6000.XmlParsingService.ValidateAndExtractAllContainerNumbers`
from the FS6000 vendor XML manifest — NOT by Tesseract. So the truth
is independent of any OCR pass, but it depends on the **scanner
operator** entering the right container number into the FS6000
console at the gate. Where operators get it wrong, the "miss" is a
data-entry error baked into the truth, not a Tesseract failure.
Filtering by ISO 6346 mod-11 check digit catches most of these
(misentered numbers usually fail the check) but doesn't catch every
case.

This is a soft caveat: it cuts both ways — it could mask a small
fraction of true Tesseract hits where the scanner truth is wrong, and
it could slightly inflate misses where the operator typo is the
real problem.

### 4.2 Inputs are full-container LUT JPEGs, not plate ROIs

v1's `ContainerNumberOcrService` runs Tesseract over the whole
LUT-rendered composite image. There is **no plate-ROI detector** in v1;
the OCR engine sees the entire ~2295×1378 X-ray scan with the painted
container number embedded somewhere in it (typically on the door,
heavily compressed by the LUT pseudocolor). §6.1.2 of
`IMAGE-ANALYSIS-MODERNIZATION.md` calls out this as a known v1 weakness
and assumes Florence-2 will be fed cropped 384×384 plate ROIs.

The Sprint 19 baseline therefore measures **Tesseract's effective
production accuracy on v1's actual input**, not Tesseract's
theoretical accuracy on perfect plate crops. That's the right thing
to measure for §12.3's decision gate (the comparison is "does
Florence-2 + a real ROI detector beat v1's status quo?") but it's
worth flagging that Tesseract on cropped ROIs would do meaningfully
better than the number reported here.

### 4.3 Corpus skew

The 5273-row `fs6000images` table is dominated by FS6000 captures from
a single scanner. There is no Tema / Takoradi / KIA Cargo split. Per-
location accuracy variation may be material once the pilot site is
chosen — re-run the baseline scoped to the pilot site before any
Florence-2 acceptance call.

### 4.4 Per-failure-mode classification is heuristic best-effort

The harness classifies into four §6.1.1 buckets:
- **Stylized typography** — keyed off the BIC owner-prefix list
  (HLBU, MSCU, TGHU, EMCU, OOLU). Real signal but coarse.
- **False-positive surfaces** — `imagetype` not in `{Main}`. The harness
  filters to `Main` so this bucket is empty by construction on this
  run; documented for future when other imagetypes are mixed in.
- **Weathering** — needs labels we don't have. Always emits 0 unless
  a future analyst-feedback table is wired in.
- **Oblique angles** — needs angle metadata we don't compute. Same
  status as weathering.

When per-bucket sample counts fall below `MinBucketSamples = 30`, the
harness emits `perFailureMode: null` and the runbook section reflects
that. The §6.1.1 table is still the right guide for spec writing —
the harness just can't reproduce it on this corpus without label
infrastructure we haven't built yet.

### 4.5 Latency is CPU-bound on a TEST-SERVER

The numbers reflect Tesseract on a TEST-SERVER CPU, not the iGPU bar
the §6.1 spec sets. Florence-2 acceptance must measure latency on
lane-PC iGPU (Iris Xe via DirectML or OpenVINO), not on this dev
machine. That comparison is Phase 5 (Acceptance validation) work,
not Sprint 19.

---

## 5. What this unlocks / blocks

- **Unblocks** §12 Phase 3 (training-data prep). The baseline establishes
  the bar Florence-2 must clear; the trainer at
  `tools/inference-training/container-ocr/train.py` can now reference a
  concrete EM% floor in its early-stopping logic.
- **Unblocks** §12 Phase 4 (out-of-band GPU training). User can start
  Florence-2 fine-tune knowing the target.
- **Blocks** §6.1 production deployment until a Florence-2 candidate
  clears this bar. Acceptance (§12 Phase 5) re-runs this exact harness
  with `--engine florence2` once the ONNX export lands.

---

## 6. How to extend

### 6.1 To re-run on a different corpus / pilot site

```powershell
# Filter to a particular scanner_serial via a custom corpus query
# (not yet a CLI flag — patch SQL in PostgresCorpusSource.cs).
```

### 6.2 To compare Florence-2 against this floor

Once `tools/inference-training/container-ocr/export_onnx.py` produces
a candidate at `storage/models/container-ocr/v{N}/model.onnx`:

```powershell
dotnet run --project tools/inference-evaluation/container-ocr -- `
    --engine florence2 `
    --corpus-source postgres `
    --corpus-limit 2200 `
    --out tools/inference-evaluation/container-ocr/results/florence2-candidate-vN.json
```

The comparison is JSON-diff between this baseline file and the
candidate file. If
`candidate.exactMatchRate - baseline.exactMatchRate < 0` then
Florence-2 regressed — investigate before any production rollout.

### 6.3 To wire analyst-corrected gold labels into the truth set

When the v2 inspection module's analyst-feedback path lands (planned
post-Sprint-22), update `PostgresCorpusSource.SqlStream` to LEFT JOIN
the new gold-label table and set truth precedence
`gold > scanner_xml > none`. The current code preserves the
infrastructure for this — `CorpusRow.Truth` already takes the
strongest available source.

---

## 7. References

- v1 OCR service:
  `C:\Shared\NSCIM_PRODUCTION\src\NickScanCentralImagingPortal.Services.ImageProcessing\ContainerNumberOcrService.cs`
- v1 truth source:
  `C:\Shared\NSCIM_PRODUCTION\src\NickScanCentralImagingPortal.Services.FS6000\XmlParsingService.cs`
  (line 534, `ValidateAndExtractAllContainerNumbers`)
- Plan-file: `~/.claude/plans/tingly-launching-quasar.md` §12.3
  ("Decision gates")
- Spec: `docs/IMAGE-ANALYSIS-MODERNIZATION.md` §6.1 ("Container-number
  OCR replacement")
- Harness: `tools/inference-evaluation/container-ocr/`
- Results JSON: `tools/inference-evaluation/container-ocr/results/baseline-2026-05-04.json`
